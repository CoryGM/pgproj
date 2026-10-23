namespace PgProj.Semantics;

/// <summary>
/// The database's default tablespace and a table's explicit <c>TABLESPACE</c> clause (G-153). PostgreSQL stores a table created in the default tablespace as having no
/// tablespace of its own (<c>reltablespace</c> is 0), whether or not the statement named it, so a project that writes <c>TABLESPACE pg_default</c> can never be told from the
/// live table. The default is not always <c>pg_default</c>: a database created with <c>TABLESPACE x</c> has <c>x</c> for its default, and there <c>pg_default</c> is a real,
/// different tablespace.
/// </summary>
public static class DefaultTablespace
{
    /// <summary>
    /// Removes the <c>TABLESPACE</c> clause from the tables of <paramref name="projectModel"/> that name <paramref name="defaultTablespaceName"/> (compared without regard to
    /// case), so they compare equal to the live tables. Nothing changes when the default is not known.
    /// </summary>
    public static void Apply(DatabaseModel projectModel, string? defaultTablespaceName)
    {
        ArgumentNullException.ThrowIfNull(projectModel);

        if (string.IsNullOrWhiteSpace(defaultTablespaceName) || projectModel.Tables is not IDictionary<string, TableDefinition> tables)
        {
            return;
        }

        foreach (var (key, table) in tables.Where(pair => string.Equals(pair.Value.TablespaceName, defaultTablespaceName, StringComparison.Ordinal)).ToArray())
        {
            tables[key] = table with { TablespaceName = null };
        }

        // A materialized view is stored the same way (G-219).
        if (projectModel.Views is IDictionary<string, ViewDefinition> views)
        {
            foreach (var (key, view) in views.Where(pair => string.Equals(pair.Value.TablespaceName, defaultTablespaceName, StringComparison.Ordinal)).ToArray())
            {
                views[key] = view with { TablespaceName = null };
            }
        }
    }
}
