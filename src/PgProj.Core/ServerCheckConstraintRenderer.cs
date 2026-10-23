using System.Text.Json;
using Npgsql;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core;

/// <summary>
/// Gives <c>CHECK</c> constraints a <see cref="ConstraintDefinition.ServerRenderedCheck"/> (G-172). PostgreSQL stores a check with
/// each literal typed by the column it is compared with (<c>status = 'x'::s.st</c> on an enum), <c>IN</c> lists as <c>= ANY (ARRAY[...])</c>,
/// and a domain column cast to its base type, none of which the parsed text can know. So a project constraint is compared by adding
/// it to a temporary copy of the live table (<c>LIKE</c>) and reading back PostgreSQL's rendering. Everything happens in a
/// transaction that is always rolled back.
/// </summary>
public sealed class ServerCheckConstraintRenderer
{
    private readonly PgQueryParser _parser;

    public ServerCheckConstraintRenderer(PgQueryParser parser)
    {
        ArgumentNullException.ThrowIfNull(parser);
        _parser = parser;
    }

    /// <summary>
    /// Sets <see cref="ConstraintDefinition.ServerRenderedCheck"/> on each <c>CHECK</c> constraint of <paramref name="projectModel"/> whose
    /// live constraint has one too. An expression the server rejects (it uses a column the live table doesn't have yet) is left without
    /// one and compared as before.
    /// </summary>
    public async Task RenderAsync(NpgsqlConnection connection, DatabaseModel projectModel, DatabaseModel liveModel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(projectModel);
        ArgumentNullException.ThrowIfNull(liveModel);

        var candidates = projectModel.Constraints.Values
            .Where(constraint => string.Equals(constraint.ConstraintType, "CHECK", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(constraint.RawCheckExpression)
                && !string.IsNullOrWhiteSpace(constraint.TableName)
                && liveModel.Constraints.TryGetValue(constraint.Key, out var liveConstraint)
                && liveConstraint.ServerRenderedCheck is not null
                && IsCheckExpression(constraint.RawCheckExpression!))
            .OrderBy(static constraint => constraint.Key, StringComparer.Ordinal)
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
                for (var position = 0; position < candidates.Length; position++)
                {
                    var rendering = await TryRenderAsync(connection, transaction, candidates[position], position, cancellationToken);
                    if (rendering is not null)
                    {
                        renderings[candidates[position].Key] = rendering;
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
            projectModel.Constraints = WithRenderings(projectModel.Constraints, constraint => renderings.GetValueOrDefault(constraint.Key));
        }
    }

    /// <summary>Gives each live <c>CHECK</c> constraint the expression it was read with (G-172).</summary>
    internal static void AttachLiveRenderings(DatabaseModel liveModel, IReadOnlyList<LiveConstraintSnapshot> snapshots)
    {
        if (!snapshots.Any(static snapshot => snapshot.CheckExpression is not null))
        {
            return;
        }

        liveModel.Constraints = WithRenderings(liveModel.Constraints, constraint =>
            snapshots.FirstOrDefault(candidate =>
                    candidate.CheckExpression is not null
                    && string.Equals(candidate.ConstraintName, constraint.Name, StringComparison.Ordinal)
                    && string.Equals(candidate.TableName, constraint.TableName, StringComparison.Ordinal)
                    && string.Equals(candidate.SchemaName, constraint.SchemaName ?? "public", StringComparison.Ordinal))
                ?.CheckExpression
                ?.Trim());
    }

    /// <summary>
    /// The expression is project SQL placed inside <c>CHECK (...)</c> of a temporary <c>ALTER TABLE</c>, so it must be one expression
    /// and nothing else; PgQuery confirms that before anything is sent to the server.
    /// </summary>
    internal bool IsCheckExpression(string expression)
    {
        var tree = _parser.Parse($"ALTER TABLE pgproj_check_table ADD CONSTRAINT pgproj_check CHECK ({expression})");
        if (tree.HasErrors || string.IsNullOrWhiteSpace(tree.RawJsonAst))
        {
            return false;
        }

        using var document = PgQueryJson.Parse(tree.RawJsonAst);
        return document.RootElement.TryGetProperty("stmts", out var statements)
            && statements.ValueKind == JsonValueKind.Array
            && statements.GetArrayLength() == 1
            && statements[0].TryGetProperty("stmt", out var statement)
            && statement.TryGetProperty("AlterTableStmt", out var alter)
            && alter.TryGetProperty("cmds", out var commands)
            && commands.GetArrayLength() == 1;
    }

    private static async Task<string?> TryRenderAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ConstraintDefinition constraint,
        int position,
        CancellationToken cancellationToken)
    {
        var liveTable = $"{SqlIdentifiers.Quote(constraint.SchemaName ?? "public")}.{SqlIdentifiers.Quote(constraint.TableName!)}";
        var tableCopy = $"pgproj_check_{position}";
        var rendering = await RolledBackRendering.TryRenderAsync(
            connection,
            transaction,
            tableCopy,
            [
                $"CREATE TEMPORARY TABLE {tableCopy} (LIKE {liveTable});",
                $"ALTER TABLE {tableCopy} ADD CONSTRAINT pgproj_check CHECK ({constraint.RawCheckExpression});",
            ],
            $"SELECT pg_catalog.pg_get_expr(con.conbin, con.conrelid) FROM pg_catalog.pg_constraint con WHERE con.conrelid = 'pg_temp.{tableCopy}'::regclass AND con.conname = 'pgproj_check';",
            cancellationToken);
        return rendering?.Trim();
    }

    private static Dictionary<string, ConstraintDefinition> WithRenderings(
        IReadOnlyDictionary<string, ConstraintDefinition> constraints,
        Func<ConstraintDefinition, string?> renderingFor)
    {
        var result = new Dictionary<string, ConstraintDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer);
        foreach (var (key, constraint) in constraints)
        {
            var rendering = renderingFor(constraint);
            result[key] = rendering is null ? constraint : constraint with { ServerRenderedCheck = rendering };
        }

        return result;
    }
}
