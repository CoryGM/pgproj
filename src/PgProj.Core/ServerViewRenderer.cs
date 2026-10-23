using System.Text.Json;
using Npgsql;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core;

/// <summary>
/// Gives views a <see cref="ViewDefinition.ServerRenderedBody"/> (G-96). PostgreSQL stores a view in its own
/// rendering, not as written, so a project view is compared by creating its body as a temporary view on the target
/// server and asking PostgreSQL to render it the same way it renders the live view. Every temporary view is created
/// inside a transaction that is always rolled back, so nothing is left behind.
/// </summary>
public sealed class ServerViewRenderer
{
    private readonly PgQueryParser _parser;

    public ServerViewRenderer(PgQueryParser parser)
    {
        ArgumentNullException.ThrowIfNull(parser);
        _parser = parser;
    }

    /// <summary>
    /// Sets <see cref="ViewDefinition.ServerRenderedBody"/> on each view of <paramref name="projectModel"/> that also
    /// exists in <paramref name="liveModel"/> with a rendering of its own. A view whose body cannot be created on the
    /// server as it stands (it uses an object the server doesn't have yet, or the role may not create temporary
    /// objects) is left without one and compared the way it was before G-96.
    /// </summary>
    public async Task RenderAsync(NpgsqlConnection connection, DatabaseModel projectModel, DatabaseModel liveModel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(projectModel);
        ArgumentNullException.ThrowIfNull(liveModel);

        var candidates = projectModel.Views.Values
            .Where(view => !string.IsNullOrWhiteSpace(view.RawBodyExpression)
                && liveModel.Views.TryGetValue(view.Key, out var liveView)
                && liveView.ServerRenderedBody is not null
                && IsSingleViewBody(view.RawBodyExpression!))
            .OrderBy(static view => view.Key, StringComparer.Ordinal)
            .ToArray();

        if (candidates.Length == 0)
        {
            return;
        }

        var renderings = new Dictionary<string, string>(PgProj.Parser.SqlIdentifiers.NameComparer);
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            try
            {
                await ApplyColumnRenamesAsync(connection, transaction, projectModel, liveModel, cancellationToken);

                // In rounds (G-276): a view whose columns turn out to be its live ones renamed has them renamed here too, so a view that selects the
                // new names from it renders in the next round.
                var pending = Enumerable.Range(0, candidates.Length).ToList();
                var progress = true;
                while (pending.Count > 0 && progress)
                {
                    progress = false;
                    foreach (var index in pending.ToArray())
                    {
                        var rendering = await TryRenderAsync(connection, transaction, candidates[index].RawBodyExpression!, candidates[index].ColumnAliases, index, cancellationToken);
                        if (rendering is null)
                        {
                            continue;
                        }

                        renderings[candidates[index].Key] = rendering;
                        pending.Remove(index);
                        progress = true;
                        await ApplyViewColumnRenamesAsync(connection, transaction, liveModel.Views[candidates[index].Key], rendering, index, cancellationToken);
                    }
                }
            }
            finally
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
        }

        if (renderings.Count > 0)
        {
            var columns = new Dictionary<string, IReadOnlyList<string>>(PgProj.Parser.SqlIdentifiers.NameComparer);
            foreach (var (key, rendering) in renderings.ToArray())
            {
                var separator = rendering.LastIndexOf('\u001e');
                if (separator >= 0)
                {
                    renderings[key] = rendering[..separator];
                    columns[key] = SplitColumns(rendering[(separator + 1)..]);
                }
            }

            projectModel.Views = WithOutputColumns(projectModel.Views, columns);
            projectModel.Views = WithRenderings(projectModel.Views, view => renderings.GetValueOrDefault(view.Key));
        }
    }

    /// <summary>
    /// Renames, in the transaction that is rolled back, each live column the project renames with <c>@renamed-from</c> (G-276), so a view that selects the new
    /// name renders, and its output columns are known: they show whether the view's columns are only renamed, which <c>publish</c> then does before
    /// <c>CREATE OR REPLACE VIEW</c>. A rename the server refuses is undone to its savepoint and left out.
    /// </summary>
    internal static async Task ApplyColumnRenamesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, DatabaseModel projectModel, DatabaseModel liveModel, CancellationToken cancellationToken)
    {
        var renames = ColumnRenameStatements(projectModel, liveModel);
        for (var index = 0; index < renames.Count; index++)
        {
            var savepoint = $"pgproj_rename_{index}";
            await using (var save = new NpgsqlCommand($"SAVEPOINT {savepoint};", connection, transaction))
            {
                await save.ExecuteNonQueryAsync(cancellationToken);
            }

            try
            {
                await using var rename = new NpgsqlCommand(renames[index], connection, transaction);
                await rename.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (PostgresException)
            {
                await using var undo = new NpgsqlCommand($"ROLLBACK TO SAVEPOINT {savepoint};", connection, transaction);
                await undo.ExecuteNonQueryAsync(cancellationToken);
            }
        }
    }

    /// <summary>
    /// Renames a live view's output columns, in the transaction that is rolled back, when its project rendering shows them only renamed (G-276), so a
    /// view on it that selects the new names can render. A rename the server refuses is undone to its savepoint.
    /// </summary>
    private static async Task ApplyViewColumnRenamesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, ViewDefinition liveView, string rendering, int index, CancellationToken cancellationToken)
    {
        var separator = rendering.LastIndexOf('\u001e');
        if (separator < 0 || ViewColumnRenames.Plan(liveView.ServerOutputColumns, SplitColumns(rendering[(separator + 1)..])) is not { Count: > 0 } renames)
        {
            return;
        }

        var savepoint = $"pgproj_view_rename_{index}";
        await using (var save = new NpgsqlCommand($"SAVEPOINT {savepoint};", connection, transaction))
        {
            await save.ExecuteNonQueryAsync(cancellationToken);
        }

        try
        {
            foreach (var (from, to) in renames)
            {
                await using var rename = new NpgsqlCommand($"ALTER VIEW {SqlIdentifiers.Quote(liveView.SchemaName ?? "public")}.{SqlIdentifiers.Quote(liveView.Name)} RENAME COLUMN {from} TO {to};", connection, transaction);
                await rename.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        catch (PostgresException)
        {
            await using var undo = new NpgsqlCommand($"ROLLBACK TO SAVEPOINT {savepoint};", connection, transaction);
            await undo.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>
    /// The <c>ALTER TABLE ... RENAME COLUMN</c> of each project column renamed from a column the live table has, and whose new name it lacks. The annotation is
    /// matched as <c>AlterOperation</c> matches it, so the rendering agrees with what <c>publish</c> renames (a quoted or capitalized one is G-287).
    /// </summary>
    internal static IReadOnlyList<string> ColumnRenameStatements(DatabaseModel projectModel, DatabaseModel liveModel)
    {
        var statements = new List<string>();
        foreach (var table in projectModel.Tables.Values.OrderBy(static table => table.Key, StringComparer.Ordinal))
        {
            if (!liveModel.Tables.TryGetValue(table.Key, out var liveTable))
            {
                continue;
            }

            var liveNames = liveTable.Columns.Select(static column => column.Name).ToHashSet(PgProj.Parser.SqlIdentifiers.NameComparer);
            foreach (var column in table.Columns)
            {
                if (!string.IsNullOrWhiteSpace(column.RenamedFrom) && liveNames.Contains(column.RenamedFrom) && !liveNames.Contains(column.Name))
                {
                    statements.Add($"ALTER TABLE {SqlIdentifiers.Quote(table.SchemaName ?? "public")}.{SqlIdentifiers.Quote(table.Name)} RENAME COLUMN {SqlIdentifiers.Quote(column.RenamedFrom)} TO {SqlIdentifiers.Quote(column.Name)};");
                }
            }
        }

        return statements;
    }

    /// <summary>Trims PostgreSQL's leading space and trailing semicolon from a <c>pg_get_viewdef</c> result.</summary>
    internal static string NormalizeRendering(string rendering)
        => rendering.Trim().TrimEnd(';').Trim();

    /// <summary>Gives each view with known output columns its <see cref="ViewDefinition.ServerOutputColumns"/> (G-104).</summary>
    internal static Dictionary<string, ViewDefinition> WithOutputColumns(IReadOnlyDictionary<string, ViewDefinition> views, IReadOnlyDictionary<string, IReadOnlyList<string>> columns)
    {
        var result = new Dictionary<string, ViewDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer);
        foreach (var (key, view) in views)
        {
            result[key] = columns.TryGetValue(key, out var outputColumns) ? view with { ServerOutputColumns = outputColumns } : view;
        }

        return result;
    }

    /// <summary>The <c>name type</c> entries of an output column list joined with the unit separator.</summary>
    internal static IReadOnlyList<string> SplitColumns(string joined)
        => string.IsNullOrEmpty(joined) ? [] : joined.Split('\u001f');

    /// <summary>Gives each live view the rendering it was read with (G-96).</summary>
    internal static void AttachLiveRenderings(DatabaseModel liveModel, IReadOnlyList<LiveViewSnapshot> snapshots)
    {
        if (snapshots.Count == 0 || liveModel.Views.Count == 0)
        {
            return;
        }

        liveModel.Views = WithRenderings(liveModel.Views, view =>
        {
            var snapshot = snapshots.FirstOrDefault(candidate =>
                string.Equals(candidate.ViewName, view.Name, StringComparison.Ordinal)
                && string.Equals(candidate.SchemaName, view.SchemaName ?? "public", StringComparison.Ordinal));
            return snapshot is null ? null : NormalizeRendering(snapshot.ViewDefinition);
        });
    }

    private static Dictionary<string, ViewDefinition> WithRenderings(
        IReadOnlyDictionary<string, ViewDefinition> views,
        Func<ViewDefinition, string?> renderingFor)
    {
        var result = new Dictionary<string, ViewDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer);
        foreach (var (key, view) in views)
        {
            var rendering = renderingFor(view);
            result[key] = rendering is null ? view : view with { ServerRenderedBody = rendering };
        }

        return result;
    }

    /// <summary>
    /// The body is project SQL placed inside <c>CREATE TEMP VIEW ... AS</c>, so it must be exactly one view's query and
    /// nothing else; PgQuery confirms that before anything is sent to the server.
    /// </summary>
    internal bool IsSingleViewBody(string body)
    {
        var tree = _parser.Parse($"CREATE VIEW pgproj_render_check AS {body}");
        if (tree.HasErrors || string.IsNullOrWhiteSpace(tree.RawJsonAst))
        {
            return false;
        }

        using var document = PgQueryJson.Parse(tree.RawJsonAst);
        if (!document.RootElement.TryGetProperty("stmts", out var statements)
            || statements.ValueKind != JsonValueKind.Array
            || statements.GetArrayLength() != 1)
        {
            return false;
        }

        return statements[0].TryGetProperty("stmt", out var statement) && statement.TryGetProperty("ViewStmt", out _);
    }

    private static async Task<string?> TryRenderAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string body,
        IReadOnlyList<string> columnAliases,
        int index,
        CancellationToken cancellationToken)
    {
        var viewName = $"pgproj_render_{index}";

        // The rendering, then (after a record separator) the view's output columns, each "name type" (G-104).
        var rendering = await RolledBackRendering.TryRenderAsync(
            connection,
            transaction,
            viewName,
            [$"CREATE TEMPORARY VIEW {viewName}{(columnAliases.Count == 0 ? string.Empty : $" ({string.Join(", ", columnAliases.Select(SqlIdentifiers.Quote))})")} AS {body};"],
            $"""
            SELECT pg_catalog.pg_get_viewdef('pg_temp.{viewName}'::regclass, true) || chr(30) || coalesce((
                SELECT string_agg(pg_catalog.quote_ident(attname) || ' ' || pg_catalog.format_type(atttypid, atttypmod), chr(31) ORDER BY attnum)
                FROM pg_catalog.pg_attribute WHERE attrelid = 'pg_temp.{viewName}'::regclass AND attnum > 0 AND NOT attisdropped), '');
            """,
            cancellationToken);
        if (rendering is null)
        {
            return null;
        }

        var separator = rendering.LastIndexOf('\u001e');
        return separator < 0 ? NormalizeRendering(rendering) : NormalizeRendering(rendering[..separator]) + '\u001e' + rendering[(separator + 1)..];
    }
}
