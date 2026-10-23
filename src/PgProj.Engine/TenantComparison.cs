using Npgsql;
using PgProj.Core;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Engine;

/// <summary>One tenant's comparison (G-290): the changes in its schema, and its project model (the shared files with the template read for it).</summary>
internal sealed record TenantChanges(TenantTarget Tenant, ChangeSet Changes, DatabaseModel ProjectModel, DatabaseModel LiveModel);

/// <summary>A project with tenants compared with a database (G-290): the shared side once, and each tenant on its own.</summary>
internal sealed record TenantComparisonResult(
    DatabaseModel SharedModel,
    DatabaseModel SharedLiveModel,
    ChangeSet SharedChanges,
    TenantList List,
    IReadOnlyList<TenantChanges> Tenants);

/// <summary>
/// Compares a project with tenants with the connected database (G-290). The shared files are compared once, against every schema but the tenants'
/// (or the project's <c>schemas</c>). Each tenant is compared on its own: the shared files with the template read for the tenant's schema (the stand-in
/// mapped to it where the template names the schema itself), against the tenant's schema alone, with the session's <c>search_path</c> set to the
/// tenant's schema and then the project's, as PostgreSQL resolves the template's names for that tenant. A tenant's changes are kept to its schema; the
/// shared objects are the shared side's.
/// </summary>
internal static class TenantComparison
{
    public static async Task<TenantComparisonResult> CompareAsync(
        LiveComparison comparison,
        NpgsqlConnection connection,
        PgProject project,
        Workspace workspace,
        IReadOnlyList<SqlSyntaxTree> sharedTrees,
        bool? excludeGrants,
        bool? excludeRoleMemberships,
        bool? excludeOwners,
        IReadOnlyList<string> only,
        IProgress<EngineEvent>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentNullException.ThrowIfNull(project);
        var tenants = project.Tenants ?? throw new ArgumentException("The project has no tenants.", nameof(project));
        var grants = excludeGrants ?? project.ExcludeGrants;
        var memberships = excludeRoleMemberships ?? project.ExcludeRoleMemberships;

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var sharedModel = ProjectModelBuilder.BuildModel(project, sharedTrees, excludeGrants, excludeRoleMemberships, excludeOwners);
        IReadOnlyList<string> sharedScope = project.Schemas.Count > 0
            ? LiveComparison.GetLiveScanSchemas(project, sharedModel)
            : await TenantDiscovery.ReadSharedSchemasAsync(connection, tenants, cancellationToken);
        if (sharedScope.Count == 0)
        {
            // An empty scope reads every schema, tenants included; a database with no other schema has nothing shared to read.
            sharedScope = [project.DefaultSchema];
        }

        progress?.Report(new EngineEvent(EngineEventKind.TenantStep, null, stopwatch.ElapsedMilliseconds, $"Reading the shared schemas: {string.Join(", ", sharedScope)}."));
        var (sharedLive, sharedChanges) = await comparison.CompareAsync(
            connection, sharedModel, sharedScope, grants, memberships, (excludeOwners ?? project.ExcludeOwners) || sharedModel.Owners.Count == 0, LiveComparison.GetScopeExtensions(sharedModel), cancellationToken);
        progress?.Report(new EngineEvent(EngineEventKind.TenantStep, null, stopwatch.ElapsedMilliseconds, $"Compared the shared schemas in {stopwatch.ElapsedMilliseconds} ms: {sharedChanges.Added.Count} added, {sharedChanges.Removed.Count} removed, {sharedChanges.Modified.Count} modified."));

        var list = await TenantDiscovery.FindAsync(connection, tenants, only, cancellationToken);
        var results = new List<TenantChanges>();
        var previousSearchPath = await ReadSearchPathAsync(connection, cancellationToken);
        try
        {
            foreach (var tenant in list.Tenants)
            {
                stopwatch.Restart();
                var (model, live, changes) = await CompareTenantAsync(comparison, connection, project, workspace, sharedTrees, tenant, excludeGrants, excludeRoleMemberships, excludeOwners, cancellationToken);
                results.Add(new TenantChanges(tenant, changes, model, live));
                progress?.Report(new EngineEvent(EngineEventKind.TenantStep, null, stopwatch.ElapsedMilliseconds, $"Compared {tenant.Schema}{(tenant.Exists ? string.Empty : " (no schema yet)")} in {stopwatch.ElapsedMilliseconds} ms: {changes.Added.Count} added, {changes.Removed.Count} removed, {changes.Modified.Count} modified."));
            }
        }
        finally
        {
            await RestoreSearchPathAsync(connection, previousSearchPath, CancellationToken.None);
        }

        return new TenantComparisonResult(sharedModel, sharedLive, sharedChanges, list, results);
    }

    /// <summary>One tenant: its model, its schema read from the database, and the changes in its schema.</summary>
    public static async Task<(DatabaseModel Model, DatabaseModel Live, ChangeSet Changes)> CompareTenantAsync(
        LiveComparison comparison,
        NpgsqlConnection connection,
        PgProject project,
        Workspace workspace,
        IReadOnlyList<SqlSyntaxTree> sharedTrees,
        TenantTarget tenant,
        bool? excludeGrants,
        bool? excludeRoleMemberships,
        bool? excludeOwners,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(tenant);
        var tenants = project.Tenants!;

        var template = TenantStandIn.Replace(ProjectModelBuilder.ParseTemplate(project, workspace, tenant.Schema), tenants.StandInSchema, tenant.Schema);
        var model = ProjectModelBuilder.BuildModel(project, [.. sharedTrees, .. template], excludeGrants, excludeRoleMemberships, excludeOwners);
        if (model.HasErrors)
        {
            var errors = model.Diagnostics.Where(static diagnostic => diagnostic.Severity == SemanticDiagnosticSeverity.Error).Take(5).Select(static diagnostic => diagnostic.Message);
            throw new RefusedException($"The template read for {tenant.Schema} has errors: {string.Join(" | ", errors)}");
        }

        await LiveComparison.SetSearchPathAsync(connection, model.SearchPathFor(tenant.Schema), cancellationToken);
        var (live, changes) = await comparison.CompareAsync(
            connection,
            model,
            [tenant.Schema],
            excludeGrants ?? project.ExcludeGrants,
            excludeRoleMemberships ?? project.ExcludeRoleMemberships,
            (excludeOwners ?? project.ExcludeOwners) || model.Owners.Count == 0,
            [],
            cancellationToken);
        return (model, live, KeepSchema(changes, tenant.Schema));
    }

    /// <summary>The changes to what is in <paramref name="schema"/>, and to the schema itself (its declaration, comment, owner, and grants).</summary>
    public static ChangeSet KeepSchema(ChangeSet changes, string schema)
    {
        ArgumentNullException.ThrowIfNull(changes);

        bool In(ISemanticNode node)
            => string.Equals(node.SchemaName, schema, StringComparison.Ordinal)
                || (node is SchemaDefinition or OwnerDefinition && node.SchemaName is null && string.Equals(node.Name, schema, StringComparison.Ordinal));

        return new ChangeSet
        {
            Added = [.. changes.Added.Where(change => In(change.Node))],
            Removed = [.. changes.Removed.Where(change => In(change.Node))],
            Modified = [.. changes.Modified.Where(change => In(change.NewNode))],
        };
    }

    internal static async Task<string?> ReadSearchPathAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("select pg_catalog.current_setting('search_path')", connection);
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    internal static async Task RestoreSearchPathAsync(NpgsqlConnection connection, string? previous, CancellationToken cancellationToken)
    {
        if (previous is null || connection.State != System.Data.ConnectionState.Open)
        {
            return;
        }

        await using var command = new NpgsqlCommand("select pg_catalog.set_config('search_path', @path, false)", connection);
        command.Parameters.AddWithValue("path", previous);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
