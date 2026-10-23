namespace PgProj.Parser;

public sealed record ProcedureNode(
    string? Name,
    string? SchemaName,
    string? Language,
    string? RawBodyExpression) : SqlNode("Procedure", Name, SchemaName)
{
    /// <summary>
    /// The procedure body exactly as the user wrote it, preserved verbatim for deployment.
    /// </summary>
    public string? RawBody { get; init; }

    /// <summary>
    /// The original quoting delimiter that wrapped the body, such as <c>$$</c> or <c>$proc$</c>.
    /// </summary>
    public string? BodyDelimiter { get; init; }

    /// <summary>
    /// True for a SQL-standard body (<c>BEGIN ATOMIC ... END</c> or <c>RETURN expression</c>, G-95): <see cref="RawBody"/> is then that text
    /// from its first word, deployed as it is, with no <c>AS</c> and no quoting.
    /// </summary>
    public bool IsSqlStandardBody { get; init; }

    /// <summary>
    /// Signature metadata used to regenerate the CREATE header around the verbatim body.
    /// </summary>
    public RoutineSignature? Signature { get; init; }

    /// <summary>pg_query's tree for a SQL-language body, as for <see cref="FunctionNode.BodyAst"/> (G-90).</summary>
    public string? BodyAst { get; init; }

    /// <summary>The SQL a PL/pgSQL body runs, as for <see cref="FunctionNode.PlpgsqlStatementAsts"/> (G-303a).</summary>
    public IReadOnlyList<string> PlpgsqlStatementAsts { get; init; } = [];
}
