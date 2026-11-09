using PgProj.Core;
using PgProj.Deploy;

namespace PgProj.Engine;

/// <summary>
/// The words of a publish's warnings and refusals, and the checks on its request (G-309c; these were in <c>PublishCommand</c>, <c>TenantPublish</c>, and
/// <c>TenantDiffOutput</c>). The texts are what <c>pgproj publish</c> has always printed.
/// </summary>
internal static class PublishTexts
{
    /// <summary>
    /// The warning for a printed or written <c>--concurrently</c> script (G-271): the script carries its transaction (G-165), but the statements below the
    /// marker cannot run in a transaction block, so they run after it commits, and a client that goes on after an error would run them after a failed one.
    /// </summary>
    public const string ConcurrentScriptWarning =
        "Warning: the statements below the line \"" + ConcurrentPublishPlan.Marker + "\" run outside the script's transaction, after its COMMIT, one at a time. "
        + "Run the script with psql -v ON_ERROR_STOP=1 -f <file>, so they are not run after a failed transaction; or use pgproj publish --execute --concurrently.";

    /// <summary>
    /// A printed or written script for tenants (G-290): each part carries a transaction of its own (G-165), as <c>--execute</c> runs them, so a part that
    /// fails leaves the parts before it committed, and a client that goes on after an error runs the parts after it.
    /// </summary>
    public const string TenantScriptWarning =
        "Warning: the shared part and each tenant's part (after its \"-- pgproj: tenant\" line) are transactions of their own, as pgproj publish --execute runs them: "
        + "a part that fails is rolled back and leaves the parts before it committed. Run the script with psql -v ON_ERROR_STOP=1 -f <file>, so it stops at the part that fails.";

    /// <summary>A duration PostgreSQL's <c>lock_timeout</c> accepts: a number with an optional unit (<c>us</c>, <c>ms</c>, <c>s</c>, <c>min</c>, <c>h</c>, <c>d</c>), milliseconds without one (G-270).</summary>
    public static bool IsDuration(string value)
        => System.Text.RegularExpressions.Regex.IsMatch(value.Trim(), @"^\d+(\.\d+)?\s*(us|ms|s|min|h|d)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>What is wrong with a request's options taken together, or null: the words <c>pgproj publish</c> says them in (without its <c>Publish failed:</c> lead).</summary>
    public static string? RequestProblem(bool concurrently, bool commentsOnly, string? lockTimeout)
    {
        if (concurrently && commentsOnly)
        {
            return "--concurrently and --comments-only cannot be used together (a comments-only script builds nothing).";
        }

        return lockTimeout is not null && !IsDuration(lockTimeout)
            ? $"--lock-timeout takes a duration such as 5s, 500ms, or 2min, not '{lockTimeout}'."
            : null;
    }

    /// <summary>Every relation name the database has outside the system schemas, which an index <c>--concurrently</c> builds on a partition must not take (G-271).</summary>
    public static async Task<IReadOnlySet<(string Schema, string Name)>> ReadRelationNamesAsync(Npgsql.NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string Sql = "select n.nspname, c.relname from pg_catalog.pg_class c inner join pg_catalog.pg_namespace n on n.oid = c.relnamespace where n.nspname not in ('pg_catalog', 'information_schema') and n.nspname not like 'pg_toast%'";
        var names = new HashSet<(string, string)>();
        await using var command = new Npgsql.NpgsqlCommand(Sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            names.Add((reader.GetString(0), reader.GetString(1)));
        }

        return names;
    }

    /// <summary>
    /// What the project uses that the server it is published to does not accept (G-121). <c>--execute</c> refuses to run the script, since the
    /// server would fail partway through; a script that is only shown or written (<c>--dry-run</c>, <c>--output</c>) may be meant for another
    /// server, so it only warns.
    /// </summary>
    public static string DescribeNewerThanServer(IReadOnlyList<ServerFeature> features, int serverMajorVersion, bool executing)
    {
        var lines = features.Take(10).Select(static feature => $"  {feature.Description} (needs PostgreSQL {feature.MinimumMajorVersion})").ToList();
        if (features.Count > 10)
        {
            lines.Add($"  ... and {features.Count - 10} more");
        }

        var header = executing
            ? $"the project uses features the server (PostgreSQL {serverMajorVersion}) does not have, so the script was not run:"
            : $"Warning: the project uses features the server (PostgreSQL {serverMajorVersion}) does not have, so this script cannot run on it:";
        return header + Environment.NewLine + string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// The tablespaces the project's tables name that the server does not have (G-151). pgproj never creates a tablespace (it is cluster-wide and
    /// needs a directory on the database server), so <c>--execute</c> refuses to run the script, which PostgreSQL would stop partway through;
    /// a script that is only shown or written may be meant for another server, so it only warns.
    /// </summary>
    public static string DescribeMissingTablespaces(IReadOnlyList<MissingTablespace> missing, bool executing)
    {
        var lines = missing.Take(10).Select(static item => $"  tablespace \"{item.TablespaceName}\", named by {string.Join(", ", item.Tables.Take(5))}{(item.Tables.Count > 5 ? $" and {item.Tables.Count - 5} more" : string.Empty)}").ToList();
        if (missing.Count > 10)
        {
            lines.Add($"  ... and {missing.Count - 10} more");
        }

        var header = executing
            ? "the project's tables name tablespaces the server does not have, so the script was not run:"
            : "Warning: the project's tables name tablespaces the server does not have, so this script cannot run on it:";
        var advice = executing
            ? "Create the tablespaces first (CREATE TABLESPACE needs a superuser and a directory on the database server; pgproj does not create them), or remove the TABLESPACE clause."
            : "pgproj does not create tablespaces: create them on the server first, or remove the TABLESPACE clause.";
        return header + Environment.NewLine + string.Join(Environment.NewLine, lines) + Environment.NewLine + advice;
    }

    /// <summary>
    /// The roles the project's grants name that the server does not have and the pre-deploy script does not create (G-147). A role is
    /// cluster-wide, so pgproj does not model or create one; <c>--execute</c> refuses to run the script, which PostgreSQL would stop partway
    /// through, and a script that is only shown or written may be meant for another server, so it only warns.
    /// </summary>
    public static string DescribeMissingRoles(IReadOnlyList<MissingRole> missing, bool executing)
    {
        var lines = missing.Take(10).Select(static item => $"  role \"{item.RoleName}\", named by {string.Join(", ", item.NamedBy.Take(5))}{(item.NamedBy.Count > 5 ? $" and {item.NamedBy.Count - 5} more" : string.Empty)}").ToList();
        if (missing.Count > 10)
        {
            lines.Add($"  ... and {missing.Count - 10} more");
        }

        var header = executing
            ? "the project's grants and owners name roles the server does not have, so the script was not run:"
            : "Warning: the project's grants and owners name roles the server does not have, so this script cannot run on it:";
        const string Advice = "pgproj does not create roles (they are cluster-wide): create them on the server first, or create them in the pre-deploy script.";
        return header + Environment.NewLine + string.Join(Environment.NewLine, lines) + Environment.NewLine + Advice;
    }

    /// <summary>The warning for schemas named as tenants' are that the query or file no longer lists (G-290, the user's wording of 2026-10-14).</summary>
    public static string DescribeFormerTenants(IReadOnlyList<string> schemas, bool forPublish)
        => $"{(schemas.Count == 1 ? $"{schemas[0]} is" : $"{string.Join(", ", schemas)} are")} no longer {(schemas.Count == 1 ? "a tenant" : "tenants")}: the tenant list does not include "
            + $"{(schemas.Count == 1 ? "it" : "them")}. pgproj {(forPublish ? "left" : "leaves")} {(schemas.Count == 1 ? "the schema" : "these schemas")} and everything in "
            + $"{(schemas.Count == 1 ? "it" : "them")} as they are, and never drops a tenant's schema. If {(schemas.Count == 1 ? "it is" : "they are")} no longer needed, drop "
            + $"{(schemas.Count == 1 ? "it" : "them")} by other means (for example DROP SCHEMA {schemas[0]} CASCADE).";
}
