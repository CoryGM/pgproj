namespace PgProj.Parser;

public sealed record FunctionNode(
    string? Name,
    string? SchemaName,
    string? Language,
    string? RawBodyExpression) : SqlNode("Function", Name, SchemaName)
{
    /// <summary>
    /// The routine body exactly as the user wrote it, preserved verbatim for deployment.
    /// Unlike <see cref="RawBodyExpression"/>, this is never normalized or re-rendered.
    /// </summary>
    public string? RawBody { get; init; }

    /// <summary>
    /// The original quoting delimiter that wrapped the body, such as <c>$$</c> or <c>$fn$</c>.
    /// Null when the body was written as a standard single-quoted literal.
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

    /// <summary>
    /// pg_query's tree for a SQL-language body, as a JSON array of statements: a <c>BEGIN ATOMIC</c> body's own tree, or a
    /// <c>LANGUAGE sql</c> string body parsed on its own. Null for any other language or a body pg_query cannot parse (G-90).
    /// </summary>
    public string? BodyAst { get; init; }

    /// <summary>
    /// For a PL/pgSQL body (G-303a): pg_query's tree (a JSON array of statements) of each piece of SQL the body runs, read from libpg_query's parse of the body. Empty for any other language, and for a body
    /// libpg_query cannot read. Used to report what the body names that the project does not define; it adds no dependency, since PostgreSQL does not check a PL/pgSQL body when the routine is created.
    /// </summary>
    public IReadOnlyList<string> PlpgsqlStatementAsts { get; init; } = [];
}
