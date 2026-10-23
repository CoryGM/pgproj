using PgProj.Core;
using PgProj.Diff;
using PgProj.Engine;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Engine;

/// <summary>
/// The pre- and post-deploy scripts a publish runs (G-290e): each file of a script runs unless its <c>-- pgproj: run when=...</c> marker says otherwise for
/// this part, and a skipped file (with what it includes) is a comment saying so in the script. A tenant's scripts also get the tenant's values for psql's
/// variables, and settings for function and <c>DO</c> bodies.
/// </summary>
internal static class DeployScripts
{
    /// <summary>The project's own scripts for a part with <paramref name="changes"/>: a file marked <c>when=changed</c> runs only when there are changes.</summary>
    public static (string? Pre, string? Post) ForProject(PgProject project, ChangeSet changes)
    {
        ArgumentNullException.ThrowIfNull(project);

        var changed = Count(changes) > 0;
        bool Runs(ScriptPart part) => part.RunWhen is null || (changed && string.Equals(part.RunWhen, PgProject.RunWhenChanged, StringComparison.OrdinalIgnoreCase));
        // A script with nothing left to run (its files skipped) is no script, not a comment to run.
        string? Render(ScriptPart? parts, string? content)
            => parts is null ? content : parts.Render(Runs) is var rendered && ScriptVariables.HasStatements(rendered) ? rendered : null;
        return (Render(project.PreDeployScriptParts, project.PreDeployScriptContent), Render(project.PostDeployScriptParts, project.PostDeployScriptContent));
    }

    /// <summary>
    /// A tenant's scripts: each file that runs for this tenant (no marker; <c>tenant-changed</c> when it has changes; <c>tenant-created</c> when pgproj creates
    /// its schema or it has no objects yet), its variables substituted. When anything runs, the script starts by creating the tenant's schema if it is new
    /// (so the scripts can use it before the template's objects are created) and setting <c>pgproj.tenant_schema</c> and <c>pgproj.tenant_id</c> for the
    /// transaction. Null when nothing runs.
    /// </summary>
    public static (string? Pre, string? Post) ForTenant(PgProjectTenants tenants, TenantTarget tenant, ChangeSet changes, DatabaseModel live)
    {
        ArgumentNullException.ThrowIfNull(tenants);
        ArgumentNullException.ThrowIfNull(tenant);

        var changed = Count(changes) > 0;
        var created = !tenant.Exists || IsEmpty(live, tenant.Schema);
        bool Runs(ScriptPart part)
            => part.RunWhen switch
            {
                null => true,
                var value when string.Equals(value, PgProjectTenants.TenantChanged, StringComparison.OrdinalIgnoreCase) => changed,
                var value when string.Equals(value, PgProjectTenants.TenantCreated, StringComparison.OrdinalIgnoreCase) => created,
                _ => false,
            };

        var variables = tenants.VariablesFor(tenant.Identifier);
        string? Render(ExpandedScript? script)
            => script?.Root is { } root && root.Render(Runs) is var rendered && ScriptVariables.HasStatements(rendered) ? ScriptVariables.Substitute(rendered, variables).Text : null;

        var pre = Render(tenants.PreDeploy);
        var post = Render(tenants.PostDeploy);
        if (pre is null && post is null)
        {
            return (null, null);
        }

        var setup = (tenant.Exists ? string.Empty : $"CREATE SCHEMA IF NOT EXISTS {SqlIdentifiers.QuoteName(tenant.Schema)};{Environment.NewLine}")
            + $"SET LOCAL pgproj.tenant_schema = {Literal(tenant.Schema)};{Environment.NewLine}"
            + $"SET LOCAL pgproj.tenant_id = {Literal(tenant.Identifier)};{Environment.NewLine}";
        return (setup + (pre ?? string.Empty), post);
    }

    /// <summary><c>build</c>'s check of the scripts' variables and syntax (G-290e); the engine makes it now (G-309a).</summary>
    public static IEnumerable<SemanticDiagnostic> Check(PgProject project, PgQueryParser parser)
        => PgProj.Engine.DeployScriptCheck.Check(project, parser);

    /// <summary>A tenant's schema has no table, view, sequence, type, or routine yet: the first time pgproj fills it (prefix mode, where the application creates it).</summary>
    private static bool IsEmpty(DatabaseModel live, string schema)
        => !live.Tables.Values.Cast<ISemanticNode>()
            .Concat(live.Views.Values)
            .Concat(live.Sequences.Values)
            .Concat(live.Types.Values)
            .Concat(live.Functions.Values)
            .Concat(live.Procedures.Values)
            .Any(node => string.Equals(node.SchemaName, schema, StringComparison.Ordinal));

    private static int Count(ChangeSet changes) => changes.Added.Count + changes.Removed.Count + changes.Modified.Count;

    private static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
