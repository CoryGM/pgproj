using System.Reflection;
using System.Runtime.InteropServices;
using Npgsql;
using PgProj.Core;
using PgProj.Parser;

namespace PgProj.Engine;

public sealed partial class PgProjEngine
{
    /// <summary>
    /// Describes pgproj's environment and how it reads a project and its connections (what <c>pgproj about</c> prints, G-332): the engine's version, the
    /// operating system and runtime, whether PostgreSQL's parser loads, the project (read, not built), and for the target and the source database where its
    /// connection string came from, what it names, and, connected to, the server's version and the role's privileges. Nothing it cannot read is an error: a
    /// project that is not there or not valid, and a database that cannot be reached, are reported in the result. Every message is masked, and no password is
    /// returned. A cancelled <paramref name="cancellationToken"/> is a cancelled task.
    /// </summary>
    public async Task<AboutResult> AboutAsync(AboutRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var (parserLoaded, parserMessage) = ParserLoads();
        var projectFilePath = request.ProjectFilePath is null ? null : Path.GetFullPath(request.ProjectFilePath);
        var projectStatus = AboutProjectStatus.NotRequested;
        string? projectMessage = null;
        AboutProject? project = null;
        PgProject? loaded = null;
        if (request.ProjectFilePath is not null)
        {
            try
            {
                var snapshot = ProjectSnapshot.Read(request.Files, request.ProjectFilePath, settings: null, rootDirectory: null, cancellationToken);
                loaded = snapshot.Project;
                project = Describe(snapshot);
                projectStatus = AboutProjectStatus.Found;
            }
            catch (FileNotFoundException)
            {
                projectStatus = AboutProjectStatus.NotFound;
            }
            catch (PgProjectValidationException exception)
            {
                projectStatus = AboutProjectStatus.Invalid;
                projectMessage = EngineMasking.Text(exception.Message);
            }
        }

        var target = await DescribeConnectionAsync(request.TargetConnectionString, request.TargetConnectionOrigin, loaded?.TargetConnectionString, "targetConnectionString in pgproj.json", request, cancellationToken);
        var source = await DescribeConnectionAsync(request.SourceConnectionString, request.SourceConnectionOrigin, loaded?.SourceConnectionString, "sourceConnectionString in pgproj.json", request, cancellationToken);

        return new AboutResult
        {
            EngineVersion = typeof(PgProjEngine).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
            OperatingSystem = RuntimeInformation.OSDescription,
            OperatingSystemArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Runtime = RuntimeInformation.FrameworkDescription,
            RuntimeIdentifier = RuntimeInformation.RuntimeIdentifier,
            ParserLoaded = parserLoaded,
            ParserMessage = parserMessage,
            ProjectStatus = projectStatus,
            ProjectFilePath = projectFilePath,
            ProjectMessage = projectMessage,
            Project = project,
            Target = target,
            Source = source,
        };
    }

    /// <summary>Whether libpg_query loads here: one statement parsed, and the parser's own message when it cannot load.</summary>
    private static (bool Loaded, string? Message) ParserLoads()
    {
        const string LoadFailure = "Unable to load libpg_query: ";
        var failure = new PgQueryParser().Parse("SELECT 1;").Diagnostics.FirstOrDefault(static diagnostic => diagnostic.Message.StartsWith(LoadFailure, StringComparison.Ordinal));
        return failure is null ? (true, null) : (false, FirstLine(failure.Message[LoadFailure.Length..]));
    }

    private static AboutProject Describe(ProjectSnapshot snapshot)
    {
        var project = snapshot.Project;
        return new AboutProject
        {
            Name = project.Name,
            Version = project.Version,
            SourcePath = project.SourcePath,
            SqlFileCount = snapshot.Documents.Count,
            Schemas = project.Schemas,
            SearchPath = project.SearchPath,
            PreDeployScript = project.PreDeployScript,
            PostDeployScript = project.PostDeployScript,
            ExcludeGrants = project.ExcludeGrants,
            ExcludeRoleMemberships = project.ExcludeRoleMemberships,
            ExcludeOwners = project.ExcludeOwners,
            TenantLines = [.. ProjectModelBuilder.DescribeTenants(project, snapshot.Workspace)],
        };
    }

    /// <summary>
    /// One database: the shell's connection string, or else the project's; null when neither names one. Connected to with the request's timeout, in a read-only
    /// session that reads three settings and changes nothing.
    /// </summary>
    private static async Task<AboutConnection?> DescribeConnectionAsync(string? shellValue, string? shellOrigin, string? projectValue, string projectOrigin, AboutRequest request, CancellationToken cancellationToken)
    {
        var (connectionString, origin) = !string.IsNullOrWhiteSpace(shellValue)
            ? (shellValue, string.IsNullOrWhiteSpace(shellOrigin) ? "the caller" : shellOrigin)
            : !string.IsNullOrWhiteSpace(projectValue) ? (projectValue, projectOrigin) : (null, null);
        if (connectionString is null)
        {
            return null;
        }

        if (ConnectionStringCheck.Describe(connectionString) is { } invalid)
        {
            return new AboutConnection { Origin = origin!, Status = AboutConnectionStatus.Failed, Message = EngineMasking.Text(invalid) };
        }

        var settings = new NpgsqlConnectionStringBuilder(connectionString);
        var named = new AboutConnection
        {
            Origin = origin!,
            Status = AboutConnectionStatus.NotTried,
            Host = string.IsNullOrWhiteSpace(settings.Host) ? null : settings.Host,
            Port = settings.Port,
            Database = string.IsNullOrWhiteSpace(settings.Database) ? null : settings.Database,
            Username = string.IsNullOrWhiteSpace(settings.Username) ? null : settings.Username,
        };
        if (!request.Connect)
        {
            return named;
        }

        var seconds = Math.Max(1, (int)Math.Ceiling(request.ConnectTimeout.TotalSeconds));
        settings.Timeout = settings.Timeout <= 0 ? seconds : Math.Min(settings.Timeout, seconds);
        settings.CommandTimeout = seconds;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        try
        {
            await using var connection = await LiveComparison.OpenConnectionAsync(settings.ConnectionString, timeout.Token);
            await using var command = new NpgsqlCommand(
                """
                select pg_catalog.current_setting('server_version'), pg_catalog.current_database(), current_user,
                       coalesce((select r.rolsuper from pg_catalog.pg_roles r where r.rolname = current_user), false),
                       pg_catalog.has_database_privilege(pg_catalog.current_database(), 'TEMPORARY')
                """,
                connection);
            await using var reader = await command.ExecuteReaderAsync(timeout.Token);
            await reader.ReadAsync(timeout.Token);
            return named with
            {
                Status = AboutConnectionStatus.Connected,
                ServerVersion = reader.GetString(0),
                Database = reader.GetString(1),
                Username = reader.GetString(2),
                IsSuperuser = reader.GetBoolean(3),
                CanCreateTemporaryObjects = reader.GetBoolean(4),
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return named with { Status = AboutConnectionStatus.Failed, Message = $"no answer within {seconds} s" };
        }
        catch (Exception exception) when (exception is NpgsqlException or System.Net.Sockets.SocketException or TimeoutException or RefusedException)
        {
            return named with { Status = AboutConnectionStatus.Failed, Message = EngineMasking.Text(FirstLine(Reason(exception))) };
        }
    }

    /// <summary>The driver's reason: a timeout as words, otherwise the innermost message that says something.</summary>
    private static string Reason(Exception exception)
        => exception is NpgsqlException { InnerException: TimeoutException } or TimeoutException
            ? "no answer in time"
            : exception is NpgsqlException { InnerException: System.Net.Sockets.SocketException socket } ? socket.Message : exception.Message;

    private static string FirstLine(string text)
    {
        var end = text.IndexOfAny(['\r', '\n']);
        return (end < 0 ? text : text[..end]).Trim();
    }
}
