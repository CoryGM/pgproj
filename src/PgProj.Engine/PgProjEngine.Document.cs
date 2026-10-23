using System.Diagnostics;
using Npgsql;
using PgProj.Engine.Documentation;
using PgProj.Semantics;

namespace PgProj.Engine;

public sealed partial class PgProjEngine
{
    /// <summary>
    /// Documents a project or a live database (what <c>pgproj document</c> does, G-309d): reads it, builds the data dictionary and the diagram from the objects and
    /// their comments, and renders it in each format asked for. It returns the files' text and writes none. A project or request that cannot be run is a
    /// <see cref="DocumentStatus"/>, not an exception; a defect is an exception, as is a cancelled <paramref name="cancellationToken"/>.
    /// </summary>
    public async Task<DocumentResult> DocumentAsync(DocumentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var databaseMode = !string.IsNullOrWhiteSpace(request.SourceConnectionString);
        if (!databaseMode && (request.ProjectFilePath is null) == (request.Settings is null))
        {
            throw new ArgumentException("A request names the project by its file (ProjectFilePath) or by its Settings, and by one of them, or names a SourceConnectionString.", nameof(request));
        }

        if (!databaseMode && request.Settings is not null && string.IsNullOrWhiteSpace(request.RootDirectory))
        {
            throw new ArgumentException("A request with Settings needs the RootDirectory its relative paths are resolved against.", nameof(request));
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Every message it returns or reports is masked (G-318); the documentation files are the comments' text, as they are.
        request = request with { Progress = EngineMasking.Progress(request.Progress) };
        return EngineMasking.Document(await DocumentOrStopAsync(request, databaseMode, cancellationToken));
    }

    private static async Task<DocumentResult> DocumentOrStopAsync(DocumentRequest request, bool databaseMode, CancellationToken cancellationToken)
    {
        var generatedUtc = request.GeneratedUtc ?? DateTime.UtcNow;
        var projectFilePath = databaseMode || request.ProjectFilePath is null ? null : Path.GetFullPath(request.ProjectFilePath);
        var connecting = false;
        try
        {
            var stopwatch = Stopwatch.StartNew();
            DatabaseModel model;
            DocSource source;
            BuildResult? build = null;
            if (databaseMode)
            {
                connecting = true;
                await using var connection = await LiveComparison.OpenConnectionAsync(request.SourceConnectionString!, cancellationToken);
                connecting = false;
                request.Progress?.Report(new EngineEvent(EngineEventKind.SourceConnected, null, stopwatch.ElapsedMilliseconds, $"Connected to PostgreSQL {connection.PostgreSqlVersion} (database \"{connection.Database}\") in {stopwatch.ElapsedMilliseconds} ms."));
                stopwatch.Restart();

                // Documentation needs the objects and their comments, not who may use them, so grants, memberships, and owners are not read.
                model = await new LiveComparison().BuildLiveDatabaseModelAsync(connection, cancellationToken, null, excludeGrants: true, excludeRoleMemberships: true, excludeOwners: true);
                request.Progress?.Report(new EngineEvent(EngineEventKind.LiveRead, null, stopwatch.ElapsedMilliseconds, $"Read the live database in {stopwatch.ElapsedMilliseconds} ms."));
                source = new DocSource("database", connection.Database ?? "database");
            }
            else
            {
                build = Build(new BuildRequest { Files = request.Files, ProjectFilePath = request.ProjectFilePath, Settings = request.Settings, RootDirectory = request.RootDirectory, Progress = request.Progress }, cancellationToken);
                switch (build.Status)
                {
                    case BuildStatus.ProjectNotFound:
                        return new DocumentResult { Status = DocumentStatus.ProjectNotFound, ProjectFilePath = projectFilePath, GeneratedUtc = generatedUtc };
                    case BuildStatus.ProjectInvalid:
                        return new DocumentResult { Status = DocumentStatus.ProjectInvalid, Message = build.Message, ProjectFilePath = projectFilePath, GeneratedUtc = generatedUtc };
                }

                if (request.AfterBuild is { } afterBuild)
                {
                    await afterBuild(EngineMasking.Build(build), cancellationToken);
                }

                if (build.Status == BuildStatus.Failed)
                {
                    return new DocumentResult { Status = DocumentStatus.Failed, ProjectFilePath = projectFilePath, Build = build, GeneratedUtc = generatedUtc };
                }

                model = build.Model!;
                source = new DocSource("project", build.ProjectName!);
            }

            var filter = request.Filter ?? ErdFilter.None;
            var missing = filter.Names.Where(name => !DocumentationBuilder.SchemaNames(model).Contains(name, PgProj.Parser.SqlIdentifiers.NameComparer)).ToArray();
            if (missing.Length > 0)
            {
                throw new RefusedException($"{(filter.Exclude ? "--exclude-schemas" : "--schemas")} names {(missing.Length == 1 ? "a schema" : "schemas")} with no documented object: {string.Join(", ", missing)}.");
            }

            stopwatch.Restart();
            var document = DocumentationBuilder.Build(model, source, filter, generatedUtc);
            request.Progress?.Report(new EngineEvent(EngineEventKind.DocumentationBuilt, null, stopwatch.ElapsedMilliseconds, $"Built the documentation in {stopwatch.ElapsedMilliseconds} ms."));
            var files = request.Formats.Distinct().Select(format => new DocumentFile(format, DocumentationFiles.BaseName + DocumentationFiles.Extension(format), DocumentationFiles.Render(document, format))).ToArray();

            return new DocumentResult
            {
                Status = DocumentStatus.Succeeded,
                ProjectFilePath = projectFilePath,
                Build = build,
                GeneratedUtc = generatedUtc,
                Files = files,
                Problems = document.Problems,
                Counts = document.Counts,
            };
        }
        catch (RefusedException exception)
        {
            return new DocumentResult { Status = DocumentStatus.Refused, Message = exception.Message, ProjectFilePath = projectFilePath, GeneratedUtc = generatedUtc };
        }
        catch (NpgsqlException exception) when (connecting)
        {
            return new DocumentResult { Status = DocumentStatus.ConnectionFailed, Message = exception.Message, GeneratedUtc = generatedUtc };
        }
        catch (System.Net.Sockets.SocketException exception) when (connecting)
        {
            return new DocumentResult { Status = DocumentStatus.ConnectionFailed, Message = exception.Message, GeneratedUtc = generatedUtc };
        }
    }
}
