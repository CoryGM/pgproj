using System.Diagnostics;
using Npgsql;
using PgProj.Core;
using PgProj.Diff;
using PgProj.Semantics;

namespace PgProj.Engine;

public sealed partial class PgProjEngine
{
    /// <summary>
    /// Compares a project with a live database (what <c>pgproj diff</c> does, G-309b): reads the project, reads the target, has the server render the project's
    /// objects the way it renders the live ones, and compares. With <see cref="CompareRequest.SourceConnectionString"/> a live database is compared in place of the
    /// project's files. A project or request that cannot be run is a <see cref="CompareStatus"/>, not an exception; a defect is an exception, as is a cancelled
    /// <paramref name="cancellationToken"/>.
    /// </summary>
    public async Task<CompareResult> CompareAsync(CompareRequest request, CancellationToken cancellationToken = default)
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

        cancellationToken.ThrowIfCancellationRequested();

        // Every message it returns or reports is masked (G-318).
        request = request with { Progress = EngineMasking.Progress(request.Progress) };
        return EngineMasking.Compare(await CompareOrStopAsync(request, sourceMode, cancellationToken));
    }

    private static async Task<CompareResult> CompareOrStopAsync(CompareRequest request, bool sourceMode, CancellationToken cancellationToken)
    {
        var state = new CompareState();
        try
        {
            return await RunCompareAsync(request, sourceMode, state, cancellationToken);
        }
        catch (FileNotFoundException) when (state.Project is null)
        {
            return state.ToResult(CompareStatus.ProjectNotFound, null);
        }
        catch (PgProjectValidationException exception) when (state.Project is null)
        {
            return state.ToResult(CompareStatus.ProjectInvalid, exception.Message);
        }
        catch (RefusedException exception)
        {
            return state.ToResult(CompareStatus.Refused, exception.Message);
        }
        catch (NpgsqlException exception) when (state.Connecting)
        {
            return state.ToResult(CompareStatus.ConnectionFailed, exception.Message);
        }
        catch (System.Net.Sockets.SocketException exception) when (state.Connecting)
        {
            return state.ToResult(CompareStatus.ConnectionFailed, exception.Message);
        }
    }

    private static async Task<CompareResult> RunCompareAsync(CompareRequest request, bool sourceMode, CompareState state, CancellationToken cancellationToken)
    {
        var live = new LiveComparison();
        var progress = request.Progress;
        var opened = await OpenProjectAsync(
            request.Files,
            request.ProjectFilePath,
            request.Settings,
            request.RootDirectory,
            request.SourceConnectionString,
            request.TargetConnectionString,
            request.ExcludeGrants,
            request.ExcludeRoleMemberships,
            request.ExcludeOwners,
            live,
            progress,
            state,
            cancellationToken);
        var (snapshot, built, source, stopwatch) = (opened.Snapshot, opened.Built, opened.Source, opened.Stopwatch);
        var project = snapshot.Project;
        var model = built.Model;

        if (request.AfterBuild is { } afterBuild)
        {
            await afterBuild(EngineMasking.Build(state.ToBuildResult()), cancellationToken);
        }

        if (model.HasErrors)
        {
            return state.ToResult(CompareStatus.Failed, null);
        }

        if (request.Tenants.Count > 0 && project.Tenants is null)
        {
            throw new RefusedException("--tenants is only for a project with tenants ('tenants' in pgproj.json).");
        }

        var targetConnectionString = source?.TargetConnectionString ?? SourceProject.ResolveTarget(request.TargetConnectionString, project.TargetConnectionString);

        // A project with tenants is compared in parts: the shared schemas once, then each tenant's schema against the template (G-290).
        if (project.Tenants is { } tenants)
        {
            stopwatch.Restart();
            state.Connecting = true;
            await using var tenantConnection = await LiveComparison.OpenConnectionAsync(targetConnectionString, cancellationToken);
            state.Connecting = false;
            progress?.Report(new EngineEvent(EngineEventKind.TargetConnected, null, stopwatch.ElapsedMilliseconds, $"Connected to PostgreSQL {tenantConnection.PostgreSqlVersion} (database \"{tenantConnection.Database}\") in {stopwatch.ElapsedMilliseconds} ms."));
            await LiveComparison.ApplySearchPathAsync(tenantConnection, project, cancellationToken);
            var comparison = await TenantComparison.CompareAsync(
                live,
                tenantConnection,
                project,
                snapshot.Workspace,
                [.. built.SyntaxTrees.Where(static tree => tree.SearchPath is null)],
                request.ExcludeGrants,
                request.ExcludeRoleMemberships,
                request.ExcludeOwners,
                request.Tenants,
                progress,
                cancellationToken);
            var tenantsResult = new TenantsComparison(
                tenants.StandInSchema,
                comparison.List,
                [.. comparison.Tenants.Select(static tenant => new TenantDifference(tenant.Tenant, tenant.Changes))]);
            var (added, removed, modified) = tenantsResult.Totals;
            return state.ToResult(CompareStatus.Succeeded, null) with
            {
                Changes = comparison.SharedChanges,
                Tenants = tenantsResult,
                AddedCount = comparison.SharedChanges.Added.Count + added,
                RemovedCount = comparison.SharedChanges.Removed.Count + removed,
                ModifiedCount = comparison.SharedChanges.Modified.Count + modified,
                UndroppedSchemas = UndroppedSchemas.Find(comparison.SharedLiveModel, comparison.SharedModel),
            };
        }

        stopwatch.Restart();
        state.Connecting = true;
        await using var connection = await LiveComparison.OpenConnectionAsync(targetConnectionString, cancellationToken);
        state.Connecting = false;
        progress?.Report(new EngineEvent(EngineEventKind.TargetConnected, null, stopwatch.ElapsedMilliseconds, $"Connected to PostgreSQL {connection.PostgreSqlVersion} (database \"{connection.Database}\") in {stopwatch.ElapsedMilliseconds} ms."));
        DatabaseEnd? sourceEnd = null;
        DatabaseEnd? targetEnd = null;
        if (source is not null)
        {
            await source.EnsureNotTheTargetAsync(connection, cancellationToken);
            sourceEnd = source.Source;
            targetEnd = SourceProject.EndOf(connection);

            // Said once the target is known, before it is read, as diff has always said it (G-316).
            progress?.Report(new EngineEvent(EngineEventKind.Direction, Message: SourceProject.Direction(sourceEnd, targetEnd), Level: EngineEventLevel.Information));
            progress?.Report(new EngineEvent(
                EngineEventKind.Direction,
                Message: "In what follows, an object marked + exists only in the source, one marked - only in the target, and ~ in both with a difference.",
                Level: EngineEventLevel.Information));
        }

        await LiveComparison.ApplySearchPathAsync(connection, project, cancellationToken);
        if (await live.ReadLikeSourcesAsync(connection, model, cancellationToken) is { } likeSources)
        {
            built = ProjectModelBuilder.Build(snapshot, request.ExcludeGrants, request.ExcludeRoleMemberships, request.ExcludeOwners, likeSources, cancellationToken);
            model = built.Model;
            state.SetBuilt(snapshot, built, source, stopwatch.ElapsedMilliseconds);
        }

        var scopeSchemas = LiveComparison.GetLiveScanSchemas(project, model);
        var excludeGrants = request.ExcludeGrants ?? project.ExcludeGrants;
        var excludeRoleMemberships = request.ExcludeRoleMemberships ?? project.ExcludeRoleMemberships;

        // A project that states no owner compares none (an owner only the database has is never a change), so the database's are not read at all (G-146).
        var excludeOwners = (request.ExcludeOwners ?? project.ExcludeOwners) || model.Owners.Count == 0;
        progress?.Report(new EngineEvent(
            EngineEventKind.LiveReadStarted,
            null,
            0,
            scopeSchemas.Count == 0 ? "Reading the live database (all schemas)." : $"Reading the live database (schemas: {string.Join(", ", scopeSchemas)})."));
        stopwatch.Restart();
        var (liveModel, changeSet) = await live.CompareAsync(connection, model, scopeSchemas, excludeGrants, excludeRoleMemberships, excludeOwners, LiveComparison.GetScopeExtensions(model), cancellationToken);
        progress?.Report(new EngineEvent(EngineEventKind.LiveRead, null, stopwatch.ElapsedMilliseconds, $"Read the live database in {stopwatch.ElapsedMilliseconds} ms."));
        progress?.Report(new EngineEvent(EngineEventKind.Compared, null, 0, $"Compared the project with the live database: {changeSet.Added.Count} added, {changeSet.Removed.Count} removed, {changeSet.Modified.Count} modified."));

        return state.ToResult(CompareStatus.Succeeded, null) with
        {
            Source = sourceEnd,
            Target = targetEnd,
            Changes = changeSet,
            AddedCount = changeSet.Added.Count,
            RemovedCount = changeSet.Removed.Count,
            ModifiedCount = changeSet.Modified.Count,
            BodyNotes = PlpgsqlBodyNotes.Find(changeSet, model),
            UndroppedSchemas = UndroppedSchemas.Find(liveModel, model),
        };
    }

    /// <summary>A project opened for a run: its files read into a snapshot and built, and in source mode the database it was scaffolded from.</summary>
    private sealed record OpenedProject(ProjectSnapshot Snapshot, ModelBuild Built, SourceProject? Source, Stopwatch Stopwatch);

    /// <summary>
    /// The start every run that reads a project shares (compare and publish): in source mode the source database is scaffolded in memory, then the files are read into a
    /// snapshot and built, and <paramref name="state"/> learns what was read.
    /// </summary>
    private static async Task<OpenedProject> OpenProjectAsync(
        IProjectFileSource files,
        string? projectFile,
        ProjectSettings? settings,
        string? root,
        string? sourceConnectionString,
        string? targetConnectionString,
        bool? excludeGrants,
        bool? excludeRoleMemberships,
        bool? excludeOwners,
        LiveComparison live,
        IProgress<EngineEvent>? progress,
        CompareState state,
        CancellationToken cancellationToken)
    {
        state.ProjectFilePath = projectFile is null ? null : Path.GetFullPath(projectFile);

        SourceProject? source = null;
        if (!string.IsNullOrWhiteSpace(sourceConnectionString))
        {
            state.Connecting = true;
            source = await SourceProject.CreateAsync(
                files,
                projectFile,
                settings,
                root,
                sourceConnectionString,
                targetConnectionString,
                withTarget: true,
                excludeGrants,
                excludeRoleMemberships,
                excludeOwners,
                live,
                progress,
                cancellationToken);
            state.Connecting = false;
            files = source.Files;
            projectFile = null;
            settings = source.Settings;
            root = SourceProject.VirtualRoot;
            state.ProjectFilePath = null;
        }

        var stopwatch = Stopwatch.StartNew();
        var snapshot = ProjectSnapshot.Read(files, projectFile, settings, root, cancellationToken);
        state.Project = snapshot.Project;
        var built = ProjectModelBuilder.Build(snapshot, excludeGrants, excludeRoleMemberships, excludeOwners, likeSourceTrees: null, cancellationToken);
        state.SetBuilt(snapshot, built, source, stopwatch.ElapsedMilliseconds);
        return new OpenedProject(snapshot, built, source, stopwatch);
    }

    /// <summary>What is known of a comparison so far, so a status can carry the build part whatever stopped it.</summary>
    private sealed class CompareState
    {
        private ProjectSnapshot? _snapshot;
        private ModelBuild? _built;
        private SourceProject? _source;
        private long _elapsed;

        public PgProject? Project { get; set; }

        public string? ProjectFilePath { get; set; }

        /// <summary>True while a database is being connected to, so a driver error then is a status and not a defect.</summary>
        public bool Connecting { get; set; }

        public void SetBuilt(ProjectSnapshot snapshot, ModelBuild built, SourceProject? source, long elapsed)
        {
            _snapshot = snapshot;
            _built = built;
            _source = source;
            _elapsed = elapsed;
        }

        private string Show(string path) => _source is null ? path : SourceProject.Display(path);

        private IReadOnlyList<EngineDiagnostic> Diagnostics()
            => _built is null
                ? []
                : [.. _built.Model.Diagnostics.Select(EngineDiagnostic.From).Select(d => d.File is null ? d : d with { File = Show(d.File) })];

        private IReadOnlyList<DocumentInfo> Documents(IReadOnlyList<DocumentInfo> documents)
            => [.. documents.Select(d => d with { Path = Show(d.Path) })];

        public BuildResult ToBuildResult()
        {
            var diagnostics = Diagnostics();
            return new BuildResult
            {
                Status = _built!.Model.HasErrors ? BuildStatus.Failed : BuildStatus.Succeeded,
                ProjectFilePath = ProjectFilePath,
                ProjectName = _built.Project.Name,
                SourcePath = Show(_built.Project.SourcePath),
                Documents = Documents(_snapshot!.Documents),
                TemplateDocuments = Documents(_snapshot.TemplateDocuments),
                Diagnostics = diagnostics,
                ErrorCount = diagnostics.Count(static d => d.Severity == EngineSeverity.Error),
                WarningCount = diagnostics.Count(static d => d.Severity == EngineSeverity.Warning),
                InformationCount = diagnostics.Count(static d => d.Severity == EngineSeverity.Information),
                TenantLines = [.. ProjectModelBuilder.DescribeTenants(_built.Project, _built.Workspace)],
                ElapsedMilliseconds = _elapsed,
                Model = _built.Model,
                Project = _built.Project,
                Workspace = _built.Workspace,
            };
        }

        public CompareResult ToResult(CompareStatus status, string? message)
        {
            var diagnostics = Diagnostics();
            return new CompareResult
            {
                Status = status,
                Message = message,
                ProjectFilePath = ProjectFilePath,
                ProjectName = _built?.Project.Name,
                SourcePath = _built is null ? null : Show(_built.Project.SourcePath),
                Documents = _snapshot is null ? [] : Documents(_snapshot.Documents),
                TemplateDocuments = _snapshot is null ? [] : Documents(_snapshot.TemplateDocuments),
                Diagnostics = diagnostics,
                ErrorCount = diagnostics.Count(static d => d.Severity == EngineSeverity.Error),
                WarningCount = diagnostics.Count(static d => d.Severity == EngineSeverity.Warning),
                InformationCount = diagnostics.Count(static d => d.Severity == EngineSeverity.Information),
            };
        }
    }
}
