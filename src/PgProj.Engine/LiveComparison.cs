using Npgsql;
using PgProj.Core;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Engine;

/// <summary>
/// The project's model against the live database (G-309b): reading the database, having the server render the project's views, bodies, defaults and the
/// rest the way it renders the live ones, and comparing. This is what <c>CliContext</c> held for <c>diff</c> and <c>publish</c>; <c>CliContext</c> still
/// forwards to it for <c>publish</c> until that moves into the engine (G-309c).
/// </summary>
internal sealed class LiveComparison
{
    private readonly PgQueryParser _parser = new();
    private readonly LiveDatabaseModelLoader _liveDatabaseModelLoader;
    private readonly DiffEngine _diffEngine = new();

    public LiveComparison()
    {
        _liveDatabaseModelLoader = new LiveDatabaseModelLoader(_parser, new SemanticBuilder());
    }

    /// <summary>The loader that reads a live database, for the commands that scaffold from it (<c>init</c>, source mode).</summary>
    public LiveDatabaseModelLoader Loader => _liveDatabaseModelLoader;

    /// <summary>Sets the session's <c>search_path</c> (G-290: a tenant's schema, then the project's <c>searchPath</c>, while that tenant is rendered and compared).</summary>
    public static async Task SetSearchPathAsync(NpgsqlConnection connection, IReadOnlyList<string> searchPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(searchPath);

        await using var command = new NpgsqlCommand("SET search_path TO " + string.Join(", ", searchPath.Select(SqlIdentifiers.QuoteName)), connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Reads the live database (scoped to <paramref name="scopeSchemas"/>; all of it when empty), has the server render the project's views, bodies, defaults,
    /// and the rest the way it renders the live ones, and compares: what <c>diff</c> and <c>publish</c> do before they write anything. The session's
    /// <c>search_path</c> is what the project's objects are rendered under.
    /// </summary>
    public async Task<(DatabaseModel LiveModel, ChangeSet Changes)> CompareAsync(
        NpgsqlConnection connection,
        DatabaseModel projectModel,
        IReadOnlyList<string> scopeSchemas,
        bool excludeGrants,
        bool excludeRoleMemberships,
        bool excludeOwners,
        IReadOnlyCollection<string>? scopeExtensions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(projectModel);

        var liveModel = await BuildLiveDatabaseModelAsync(connection, cancellationToken, scopeSchemas, excludeGrants, excludeRoleMemberships, excludeOwners, scopeExtensions);
        ApplyServerDefaultTablespace(projectModel, liveModel);
        await RenderProjectViewsAsync(connection, projectModel, liveModel, cancellationToken);
        await RenderProjectRoutineBodiesAsync(connection, projectModel, liveModel, cancellationToken);
        await RenderProjectPartitionBoundsAsync(connection, projectModel, liveModel, cancellationToken);
        await RenderProjectDefaultsAsync(connection, projectModel, liveModel, cancellationToken);
        await RenderProjectGeneratedExpressionsAsync(connection, projectModel, liveModel, cancellationToken);
        await RenderProjectIndexPredicatesAsync(connection, projectModel, liveModel, cancellationToken);
        await RenderProjectCheckConstraintsAsync(connection, projectModel, liveModel, cancellationToken);
        await RenderProjectRulesAsync(connection, projectModel, liveModel, cancellationToken);
        await RenderProjectPoliciesAsync(connection, projectModel, liveModel, cancellationToken);
        return (liveModel, _diffEngine.Diff(liveModel, projectModel));
    }

    /// <summary>
    /// Runs the session under the project's <c>searchPath</c> when it sets one (G-93), so a project view or default created on the server to render it
    /// (G-96, G-102) resolves its names the way it will be deployed.
    /// </summary>
    public static async Task ApplySearchPathAsync(NpgsqlConnection connection, PgProject project, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(project);

        if (project.HasConfiguredSearchPath)
        {
            await using var command = new NpgsqlCommand("SET search_path TO " + string.Join(", ", project.SearchPath.Select(SqlIdentifiers.QuoteName)) + ";", connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Opens a connection. A connection string Npgsql would refuse is said in words first (G-285): the exit code is the same, the message is better than
    /// <c>Couldn't set port (Parameter 'port')</c>. Where GSSAPI cannot be used, the connection does not try it (<see cref="KerberosLibrary"/>, G-324).
    /// </summary>
    public static async Task<NpgsqlConnection> OpenConnectionAsync(string connectionString, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        if (ConnectionStringCheck.Describe(connectionString) is { } invalid)
        {
            throw new RefusedException(invalid);
        }

        var connection = new NpgsqlConnection(KerberosLibrary.ForThisMachine(connectionString));
        try
        {
            await connection.OpenAsync(cancellationToken);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        return connection;
    }

    public Task<DatabaseModel> BuildLiveDatabaseModelAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? scopeSchemas = null,
        bool excludeGrants = false,
        bool excludeRoleMemberships = false,
        bool excludeOwners = false,
        IReadOnlyCollection<string>? scopeExtensions = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return _liveDatabaseModelLoader.LoadAsync(connection, cancellationToken, scopeSchemas, excludeGrants, excludeRoleMemberships, excludeOwners, scopeExtensions);
    }

    /// <summary>
    /// The tables the project's <c>LIKE</c> clauses copy from that it does not declare, read from the database (G-221), as syntax trees to build the model
    /// again with; null when there are none.
    /// </summary>
    public async Task<IReadOnlyList<SqlSyntaxTree>?> ReadLikeSourcesAsync(NpgsqlConnection connection, DatabaseModel sourceModel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(sourceModel);

        if (sourceModel.ExternalLikeSources.Count == 0
            || await _liveDatabaseModelLoader.ReadLikeSourcesAsync(connection, sourceModel.ExternalLikeSources, cancellationToken) is not { } sources)
        {
            return null;
        }

        return [sources];
    }

    /// <summary>
    /// The extensions a project declares (G-213), which a scoped live read takes wherever they are: an extension is named by its name alone, and one
    /// written without <c>SCHEMA</c> adds no schema to <see cref="GetLiveScanSchemas"/>.
    /// </summary>
    public static IReadOnlyCollection<string> GetScopeExtensions(DatabaseModel sourceModel)
        => [.. sourceModel.Types.Values.Where(static type => string.Equals(type.TypeKind, "extension", StringComparison.Ordinal)).Select(static type => type.Name)];

    /// <summary>
    /// Reads the project's explicit <c>TABLESPACE</c> clause the way the connected server stores it (G-153): a table written with the database's default tablespace has none
    /// of its own, so the clause is dropped from the project's model, and the table converges instead of being altered on every publish. A table naming any other tablespace,
    /// including <c>pg_default</c> in a database whose default is another one, keeps it.
    /// </summary>
    public static void ApplyServerDefaultTablespace(DatabaseModel projectModel, DatabaseModel liveModel)
    {
        ArgumentNullException.ThrowIfNull(projectModel);
        ArgumentNullException.ThrowIfNull(liveModel);

        DefaultTablespace.Apply(projectModel, liveModel.DefaultTablespaceName);
    }

    public Task RenderProjectViewsAsync(NpgsqlConnection connection, DatabaseModel projectModel, DatabaseModel liveModel, CancellationToken cancellationToken)
        => new ServerViewRenderer(_parser).RenderAsync(connection, projectModel, liveModel, cancellationToken);

    public Task RenderProjectRoutineBodiesAsync(NpgsqlConnection connection, DatabaseModel projectModel, DatabaseModel liveModel, CancellationToken cancellationToken)
        => new ServerRoutineBodyRenderer(_parser).RenderAsync(connection, projectModel, liveModel, cancellationToken);

    public Task RenderProjectPartitionBoundsAsync(NpgsqlConnection connection, DatabaseModel projectModel, DatabaseModel liveModel, CancellationToken cancellationToken)
        => new ServerPartitionBoundRenderer(_parser).RenderAsync(connection, projectModel, liveModel, cancellationToken);

    public Task RenderProjectDefaultsAsync(NpgsqlConnection connection, DatabaseModel projectModel, DatabaseModel liveModel, CancellationToken cancellationToken)
        => new ServerDefaultRenderer(_parser).RenderAsync(connection, projectModel, liveModel, cancellationToken);

    public Task RenderProjectGeneratedExpressionsAsync(NpgsqlConnection connection, DatabaseModel projectModel, DatabaseModel liveModel, CancellationToken cancellationToken)
        => new ServerGeneratedExpressionRenderer(_parser).RenderAsync(connection, projectModel, liveModel, cancellationToken);

    public Task RenderProjectIndexPredicatesAsync(NpgsqlConnection connection, DatabaseModel projectModel, DatabaseModel liveModel, CancellationToken cancellationToken)
        => new ServerIndexPredicateRenderer(_parser).RenderAsync(connection, projectModel, liveModel, cancellationToken);

    public async Task RenderProjectCheckConstraintsAsync(NpgsqlConnection connection, DatabaseModel projectModel, DatabaseModel liveModel, CancellationToken cancellationToken)
    {
        await new ServerCheckConstraintRenderer(_parser).RenderAsync(connection, projectModel, liveModel, cancellationToken);

        // A domain's checks too (G-288), the same way.
        await new ServerDomainCheckRenderer(_parser).RenderAsync(connection, projectModel, liveModel, cancellationToken);
    }

    public Task RenderProjectRulesAsync(NpgsqlConnection connection, DatabaseModel projectModel, DatabaseModel liveModel, CancellationToken cancellationToken)
        => new ServerRuleRenderer().RenderAsync(connection, projectModel, liveModel, cancellationToken);

    public Task RenderProjectPoliciesAsync(NpgsqlConnection connection, DatabaseModel projectModel, DatabaseModel liveModel, CancellationToken cancellationToken)
        => new ServerPolicyRenderer().RenderAsync(connection, projectModel, liveModel, cancellationToken);

    /// <summary>
    /// The schemas <c>diff</c>/<c>publish</c> scope their live database read to (G-85): empty when the project's <c>pgproj.json</c> never populates <c>schemas</c>,
    /// meaning no restriction at all. When <c>schemas</c> is populated, it is widened to also include every schema <paramref name="sourceModel"/> (the project's own
    /// parsed SQL) declares an object in — even one the user forgot to list — so a project's own objects can never become invisible to its own diff by omission; only a
    /// schema genuinely outside both is excluded.
    /// </summary>
    public static IReadOnlyList<string> GetLiveScanSchemas(PgProject project, DatabaseModel sourceModel)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sourceModel);

        if (project.Schemas.Count == 0)
        {
            return [];
        }

        var schemas = new HashSet<string>(project.Schemas, SqlIdentifiers.NameComparer);
        foreach (var node in sourceModel.GetAllNodes())
        {
            if (!string.IsNullOrWhiteSpace(node.SchemaName))
            {
                schemas.Add(node.SchemaName);
            }
        }

        return schemas.ToArray();
    }
}
