using Npgsql;
using PgProj.Semantics;

namespace PgProj.Core;

/// <summary>A tablespace the project names that the server does not have, and the project's tables that name it (G-151).</summary>
public sealed record MissingTablespace(string TablespaceName, IReadOnlyList<string> Tables);

/// <summary>
/// The tablespaces a project's tables name that the server it is published to lacks (G-151). A tablespace is cluster-wide, needs a superuser
/// and a directory that already exists on the database server, and cannot be created inside a transaction, so pgproj does not create one: it
/// stays outside the project, like a role. A table that names a missing one would stop the deployment with <c>42704</c> halfway through;
/// <c>publish --execute</c> asks first, and names each tablespace and table.
/// </summary>
public static class ServerTablespaceRequirements
{
    /// <summary>The names of the server's tablespaces, including the built-in <c>pg_default</c> and <c>pg_global</c>.</summary>
    public static async Task<IReadOnlyList<string>> ReadServerTablespacesAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var command = new NpgsqlCommand("select spcname from pg_catalog.pg_tablespace order by spcname", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var names = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    /// <summary>
    /// The tablespaces named by <paramref name="model"/>'s tables that are not in <paramref name="serverTablespaces"/>, each with its tables, in
    /// name order. A name is compared exactly, as PostgreSQL compares names (G-206), as identifiers are everywhere else in the model, so a quoted mixed-case name
    /// that differs from the server's only in case is not reported (the server would still refuse it).
    /// </summary>
    public static IReadOnlyList<MissingTablespace> FindMissing(DatabaseModel model, IEnumerable<string> serverTablespaces)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(serverTablespaces);

        var present = new HashSet<string>(serverTablespaces, PgProj.Parser.SqlIdentifiers.NameComparer);
        // A materialized view's tablespace is checked the same way (G-219).
        return model.Tables.Values.Select(static table => (table.Key, table.TablespaceName))
            .Concat(model.Views.Values.Where(static view => view.IsMaterialized).Select(static view => (view.Key, view.TablespaceName)))
            .Where(static table => !string.IsNullOrWhiteSpace(table.TablespaceName))
            .Where(table => !present.Contains(table.TablespaceName!))
            .GroupBy(static table => table.TablespaceName!, PgProj.Parser.SqlIdentifiers.NameComparer)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .Select(static group => new MissingTablespace(
                group.Key,
                group.Select(static table => table.Key).OrderBy(static key => key, StringComparer.Ordinal).ToArray()))
            .ToArray();
    }
}
