using System.Diagnostics;
using PgProj.Core;

namespace PgProj.Engine;

/// <summary>
/// pgproj as a library (G-309a): the pipeline behind <c>pgproj build</c>, callable by any shell (the CLI, an editor, a web API) without a
/// console, command-line parsing, or exit codes. It returns results; it never writes to the console or to a file. Stateless and safe to call
/// from several threads.
/// </summary>
public sealed partial class PgProjEngine
{
    /// <summary>
    /// Reads a project, parses its SQL, and builds its semantic model (what <c>pgproj build</c> does). The files are read once, at the start,
    /// into a snapshot, and the result describes that snapshot. With <see cref="BuildRequest.SourceConnectionString"/> a live database is built in place of the
    /// project's files (G-318). A project that is missing or not valid, or a request that cannot be run, is a <see cref="BuildStatus"/>, not an exception; a defect
    /// is an exception, as is a cancelled <paramref name="cancellationToken"/> (a cancelled task). Every message in the result is masked (G-318).
    /// </summary>
    public Task<BuildResult> BuildAsync(BuildRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sourceMode = !string.IsNullOrWhiteSpace(request.SourceConnectionString);
        if (request.ProjectFilePath is not null && request.Settings is not null)
        {
            throw new ArgumentException("A request names the project by its file (ProjectFilePath) or by its Settings, not both.", nameof(request));
        }

        if (!sourceMode && request.ProjectFilePath is null && request.Settings is null)
        {
            throw new ArgumentException("A request names the project by its file (ProjectFilePath) or by its Settings, and by one of them.", nameof(request));
        }

        if (request.Settings is not null && string.IsNullOrWhiteSpace(request.RootDirectory))
        {
            throw new ArgumentException("A request with Settings needs the RootDirectory its relative paths are resolved against.", nameof(request));
        }

        request = request with { Progress = EngineMasking.Progress(request.Progress) };
        if (sourceMode)
        {
            return BuildSourceAsync(request, cancellationToken);
        }

        try
        {
            return Task.FromResult(EngineMasking.Build(Build(request, cancellationToken)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<BuildResult>(cancellationToken);
        }
        catch (Exception exception)
        {
            return Task.FromException<BuildResult>(exception);
        }
    }

    /// <summary>
    /// A live database built in place of the project's files (G-318): scaffolded in memory as source mode does for <c>diff</c> (G-304b, G-309b), then built as a
    /// project is. A build has no target.
    /// </summary>
    private static async Task<BuildResult> BuildSourceAsync(BuildRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SourceProject source;
        try
        {
            source = await SourceProject.CreateAsync(
                request.Files,
                request.ProjectFilePath,
                request.Settings,
                request.RootDirectory,
                request.SourceConnectionString!,
                targetOption: null,
                withTarget: false,
                request.ExcludeGrants,
                request.ExcludeRoleMemberships,
                request.ExcludeOwners,
                new LiveComparison(),
                request.Progress,
                cancellationToken);
        }
        catch (PgProjectValidationException exception)
        {
            return EngineMasking.Build(new BuildResult { Status = BuildStatus.ProjectInvalid, Message = exception.Message, ProjectFilePath = FullPathOrNull(request.ProjectFilePath) });
        }
        catch (RefusedException exception)
        {
            return EngineMasking.Build(new BuildResult { Status = BuildStatus.Refused, Message = exception.Message });
        }
        catch (Npgsql.NpgsqlException exception)
        {
            return EngineMasking.Build(new BuildResult { Status = BuildStatus.ConnectionFailed, Message = exception.Message });
        }
        catch (System.Net.Sockets.SocketException exception)
        {
            return EngineMasking.Build(new BuildResult { Status = BuildStatus.ConnectionFailed, Message = exception.Message });
        }

        var inMemory = request with { Files = source.Files, ProjectFilePath = null, Settings = source.Settings, RootDirectory = SourceProject.VirtualRoot };
        return EngineMasking.Build(Build(inMemory, cancellationToken, source));
    }

    private static string? FullPathOrNull(string? path) => path is null ? null : Path.GetFullPath(path);

    private static BuildResult Build(BuildRequest request, CancellationToken cancellationToken, SourceProject? source = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        var projectFilePath = FullPathOrNull(request.ProjectFilePath);

        ProjectSnapshot snapshot;
        try
        {
            snapshot = ProjectSnapshot.Read(request.Files, request.ProjectFilePath, request.Settings, request.RootDirectory, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return new BuildResult { Status = BuildStatus.ProjectNotFound, ProjectFilePath = projectFilePath, ElapsedMilliseconds = stopwatch.ElapsedMilliseconds };
        }
        catch (PgProjectValidationException exception)
        {
            return new BuildResult { Status = BuildStatus.ProjectInvalid, Message = exception.Message, ProjectFilePath = projectFilePath, ElapsedMilliseconds = stopwatch.ElapsedMilliseconds };
        }

        request.Progress?.Report(new EngineEvent(EngineEventKind.ProjectRead, projectFilePath));
        // A database's in-memory scaffold is named source:/... in what is reported (G-309b), as for diff.
        Func<string, string> show = source is null ? static path => path : SourceProject.Display;
        foreach (var document in snapshot.Workspace.Documents)
        {
            request.Progress?.Report(new EngineEvent(EngineEventKind.DocumentRead, show(document.FilePath)));
        }

        var built = ProjectModelBuilder.Build(snapshot, request.ExcludeGrants, request.ExcludeRoleMemberships, request.ExcludeOwners, likeSourceTrees: null, cancellationToken, request.ParseCache);
        request.Progress?.Report(new EngineEvent(EngineEventKind.ModelBuilt, null, stopwatch.ElapsedMilliseconds));
        return ToBuildResult(snapshot, built, projectFilePath, stopwatch.ElapsedMilliseconds, source);
    }

    /// <summary>The result of a build of <paramref name="snapshot"/>: what <see cref="BuildAsync"/> returns, and what a scaffold read back is (G-309d).</summary>
    private static BuildResult ToBuildResult(ProjectSnapshot snapshot, ModelBuild built, string? projectFilePath, long elapsedMilliseconds, SourceProject? source = null)
    {
        Func<string, string> show = source is null ? static path => path : SourceProject.Display;
        var diagnostics = built.Model.Diagnostics.Select(EngineDiagnostic.From).Select(d => d.File is null ? d : d with { File = show(d.File) }).ToArray();
        return new BuildResult
        {
            Status = built.Model.HasErrors ? BuildStatus.Failed : BuildStatus.Succeeded,
            ProjectFilePath = projectFilePath,
            ProjectName = built.Project.Name,
            SourcePath = show(built.Project.SourcePath),
            Documents = [.. snapshot.Documents.Select(d => d with { Path = show(d.Path) })],
            TemplateDocuments = [.. snapshot.TemplateDocuments.Select(d => d with { Path = show(d.Path) })],
            Diagnostics = diagnostics,
            ErrorCount = diagnostics.Count(static d => d.Severity == EngineSeverity.Error),
            WarningCount = diagnostics.Count(static d => d.Severity == EngineSeverity.Warning),
            InformationCount = diagnostics.Count(static d => d.Severity == EngineSeverity.Information),
            TenantLines = [.. ProjectModelBuilder.DescribeTenants(built.Project, built.Workspace)],
            Source = source?.Source,
            ElapsedMilliseconds = elapsedMilliseconds,
            Model = built.Model,
            Project = built.Project,
            Workspace = built.Workspace,
        };
    }
}
