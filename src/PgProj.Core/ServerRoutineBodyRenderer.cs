using System.Text.Json;
using Npgsql;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core;

/// <summary>
/// Gives SQL-standard routine bodies (<c>BEGIN ATOMIC ... END</c>, <c>RETURN expression</c>) a server rendering (G-95). PostgreSQL
/// stores such a body parsed, not as written, and prints it back in its own form (<c>pg_get_functiondef</c>), so a project routine is
/// compared by creating it as a temporary routine of the same name and parameters on the target server and reading its body back the
/// same way the live routine's was read. Every temporary routine is created inside a transaction that is always rolled back.
/// </summary>
public sealed class ServerRoutineBodyRenderer
{
    private readonly PgQueryParser _parser;

    public ServerRoutineBodyRenderer(PgQueryParser parser)
    {
        ArgumentNullException.ThrowIfNull(parser);
        _parser = parser;
    }

    /// <summary>
    /// Sets <see cref="FunctionDefinition.ServerRenderedBody"/> and <see cref="ProcedureDefinition.ServerRenderedBody"/> on each
    /// SQL-standard routine of <paramref name="projectModel"/> whose live routine has a SQL-standard body too. A body the server cannot
    /// create as it stands (it uses an object the server doesn't have yet) is left without one and compared as before.
    /// </summary>
    public async Task RenderAsync(NpgsqlConnection connection, DatabaseModel projectModel, DatabaseModel liveModel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(projectModel);
        ArgumentNullException.ThrowIfNull(liveModel);

        var functions = projectModel.Functions.Values
            .Where(function => function.IsSqlStandardBody
                && !string.IsNullOrWhiteSpace(function.RawBody)
                && liveModel.Functions.TryGetValue(function.Key, out var live)
                && live.ServerRenderedBody is not null)
            .OrderBy(static function => function.Key, StringComparer.Ordinal)
            .ToArray();
        var procedures = projectModel.Procedures.Values
            .Where(procedure => procedure.IsSqlStandardBody
                && !string.IsNullOrWhiteSpace(procedure.RawBody)
                && liveModel.Procedures.TryGetValue(procedure.Key, out var live)
                && live.ServerRenderedBody is not null)
            .OrderBy(static procedure => procedure.Key, StringComparer.Ordinal)
            .ToArray();

        if (functions.Length == 0 && procedures.Length == 0)
        {
            return;
        }

        var functionRenderings = new Dictionary<string, string>(PgProj.Parser.SqlIdentifiers.NameComparer);
        var procedureRenderings = new Dictionary<string, string>(PgProj.Parser.SqlIdentifiers.NameComparer);
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            try
            {
                var index = 0;
                foreach (var function in functions)
                {
                    var header = $"CREATE FUNCTION pg_temp.{SqlIdentifiers.QuoteName(function.Name)}({function.Signature?.RenderParameterList()}) RETURNS "
                        + (function.Signature?.RenderReturnsClause() ?? "void");
                    if (await TryRenderAsync(connection, transaction, header, function.Name, function.Signature, function.RawBody!, index++, cancellationToken) is { } rendering)
                    {
                        functionRenderings[function.Key] = rendering;
                    }
                }

                foreach (var procedure in procedures)
                {
                    var header = $"CREATE PROCEDURE pg_temp.{SqlIdentifiers.QuoteName(procedure.Name)}({procedure.Signature?.RenderParameterList()})";
                    if (await TryRenderAsync(connection, transaction, header, procedure.Name, procedure.Signature, procedure.RawBody!, index++, cancellationToken) is { } rendering)
                    {
                        procedureRenderings[procedure.Key] = rendering;
                    }
                }
            }
            finally
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
        }

        projectModel.Functions = projectModel.Functions.ToDictionary(
            static pair => pair.Key,
            pair => functionRenderings.TryGetValue(pair.Key, out var rendering) ? pair.Value with { ServerRenderedBody = rendering } : pair.Value,
            PgProj.Parser.SqlIdentifiers.NameComparer);
        projectModel.Procedures = projectModel.Procedures.ToDictionary(
            static pair => pair.Key,
            pair => procedureRenderings.TryGetValue(pair.Key, out var rendering) ? pair.Value with { ServerRenderedBody = rendering } : pair.Value,
            PgProj.Parser.SqlIdentifiers.NameComparer);
    }

    /// <summary>
    /// Gives each live routine with a SQL-standard body its body as read: the live model is built from <c>pg_get_functiondef</c>, so that
    /// body is already PostgreSQL's rendering.
    /// </summary>
    internal static void AttachLiveRenderings(DatabaseModel liveModel)
    {
        liveModel.Functions = liveModel.Functions.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.IsSqlStandardBody && pair.Value.RawBody is { } body ? pair.Value with { ServerRenderedBody = body } : pair.Value,
            PgProj.Parser.SqlIdentifiers.NameComparer);
        liveModel.Procedures = liveModel.Procedures.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.IsSqlStandardBody && pair.Value.RawBody is { } body ? pair.Value with { ServerRenderedBody = body } : pair.Value,
            PgProj.Parser.SqlIdentifiers.NameComparer);
    }

    /// <summary>
    /// The statement carries project SQL, so it must be exactly one <c>CREATE FUNCTION</c>/<c>PROCEDURE</c> with a SQL-standard body and
    /// nothing else; PgQuery confirms that before anything is sent to the server.
    /// </summary>
    internal bool IsSingleRoutineStatement(string sql)
    {
        var tree = _parser.Parse(sql);
        if (tree.HasErrors || string.IsNullOrWhiteSpace(tree.RawJsonAst))
        {
            return false;
        }

        using var document = PgQueryJson.Parse(tree.RawJsonAst);
        return document.RootElement.TryGetProperty("stmts", out var statements)
            && statements.ValueKind == JsonValueKind.Array
            && statements.GetArrayLength() == 1
            && statements[0].TryGetProperty("stmt", out var statement)
            && statement.TryGetProperty("CreateFunctionStmt", out var create)
            && create.TryGetProperty("sql_body", out _);
    }

    private async Task<string?> TryRenderAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string header,
        string name,
        RoutineSignature? signature,
        string body,
        int index,
        CancellationToken cancellationToken)
    {
        var create = $"{header} LANGUAGE sql {body}";
        if (!IsSingleRoutineStatement(create))
        {
            return null;
        }

        // The temporary routine has the project routine's name, since PostgreSQL qualifies a parameter with it (total.p).
        var identity = $"pg_temp.{SqlIdentifiers.QuoteName(name)}({signature?.RenderArgumentTypeSignatureSql()})".Replace("'", "''", StringComparison.Ordinal);
        var definition = await RolledBackRendering.TryRenderAsync(
            connection,
            transaction,
            $"pgproj_render_routine_{index}",
            [create],
            $"SELECT pg_catalog.pg_get_functiondef('{identity}'::pg_catalog.regprocedure);",
            cancellationToken);

        // The body is read out of the definition the way the live routine's was, so both are printed and sliced alike.
        if (definition is null)
        {
            return null;
        }

        var tree = _parser.Parse(definition);
        return tree.Functions.FirstOrDefault() is { IsSqlStandardBody: true } function ? function.RawBody
            : tree.Procedures.FirstOrDefault() is { IsSqlStandardBody: true } procedure ? procedure.RawBody
            : null;
    }
}
