namespace PgProj.Parser;

/// <summary>
/// A single routine parameter as written in the original CREATE statement.
/// </summary>
public sealed record RoutineParameter(
    string? Name,
    string? DataType,
    string? Mode,
    string? DefaultExpression)
{
    /// <summary>
    /// The parameter's type exactly as written (<c>INT</c>, <c>"char"</c>, <c>"My S"."My T"</c>), which is what is deployed (G-207); null when
    /// it could not be read. <see cref="DataType"/> is the normalized name, used for the routine's identity and comparison.
    /// </summary>
    public string? WrittenDataType { get; init; }

    /// <summary>
    /// An argument of the routine: not an <c>OUT</c> parameter and not a <c>RETURNS TABLE</c> column (mode <c>TABLE</c>, G-198), which are part of
    /// the result. Only arguments identify an overload and are written in <c>DROP</c>, <c>COMMENT</c>, <c>OWNER</c>, and <c>GRANT</c>.
    /// </summary>
    public bool IsArgument
        => !string.Equals(Mode, "OUT", StringComparison.OrdinalIgnoreCase) && !IsTableColumn;

    /// <summary>A column of <c>RETURNS TABLE (...)</c> (G-198).</summary>
    public bool IsTableColumn
        => string.Equals(Mode, "TABLE", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A routine's <c>SET name TO value [, ...]</c> or <c>SET name FROM CURRENT</c> clause (G-197). <see cref="Name"/> is the setting's
/// name in lower case; <see cref="Values"/> are its values as text, without quotes (<c>search_path = pg_catalog, public</c> is two
/// values), and are empty for <c>FROM CURRENT</c>.
/// </summary>
public sealed record RoutineSetting(string Name, IReadOnlyList<string> Values, bool IsFromCurrent)
{
    /// <summary>
    /// The clause exactly as the project wrote it (<c>SET search_path = pg_catalog, public</c>), which is what is deployed (G-207); null when
    /// it could not be read, and then <see cref="Render"/> builds it from <see cref="Values"/>. <see cref="Name"/> and <see cref="Values"/>
    /// are for the comparison.
    /// </summary>
    public string? WrittenText { get; init; }

    /// <summary>The clause as written into <c>CREATE FUNCTION</c>/<c>CREATE PROCEDURE</c>: <see cref="WrittenText"/>, or each value a string literal.</summary>
    public string Render()
        => WrittenText ?? (IsFromCurrent
            ? $"SET {RenderName()} FROM CURRENT"
            : $"SET {RenderName()} TO {string.Join(", ", Values.Select(static value => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'"))}");

    // A custom setting's name is dotted (myapp.tenant); each part is a name of its own.
    private string RenderName()
        => string.Join(".", Name.Split('.').Select(SqlIdentifiers.QuotePlain));
}

/// <summary>
/// Signature metadata for a function or procedure. This is captured so the CREATE header can be
/// regenerated faithfully while the routine body is replayed verbatim.
/// </summary>
public sealed record RoutineSignature
{
    public IReadOnlyList<RoutineParameter> Parameters { get; init; } = [];

    public string? ReturnType { get; init; }

    /// <summary>The result type exactly as written, without <c>SETOF</c>, which is what is deployed (G-207); null when it could not be read.</summary>
    public string? WrittenReturnType { get; init; }

    /// <summary>
    /// What follows <c>RETURNS</c>: <c>TABLE (name type, ...)</c> for a function declared that way (G-198), each column's type as written, or
    /// the result type with its <c>SETOF</c>; null for a routine with no result.
    /// </summary>
    public string? RenderReturnsClause()
    {
        var columns = Parameters.Where(static parameter => parameter.IsTableColumn).ToArray();
        if (columns.Length > 0)
        {
            return "TABLE (" + string.Join(", ", columns.Select(static column =>
                SqlIdentifiers.QuoteName(column.Name ?? "?") + " " + (column.WrittenDataType ?? SqlIdentifiers.QuoteTypeName(column.DataType ?? "text")))) + ")";
        }

        return RenderReturnType() is { } type ? (ReturnsSet ? "SETOF " : string.Empty) + type : null;
    }

    /// <summary>The result type as <c>RETURNS</c> writes it, without <c>SETOF</c>: <see cref="WrittenReturnType"/>, or the normalized name quoted.</summary>
    public string? RenderReturnType()
        => WrittenReturnType ?? (ReturnType is null ? null : SqlIdentifiers.QuoteTypeName(ReturnType));

    public bool ReturnsSet { get; init; }

    public string? Language { get; init; }

    public string? Volatility { get; init; }

    public bool IsStrict { get; init; }

    public bool IsSecurityDefiner { get; init; }

    /// <summary><c>PARALLEL SAFE</c>, <c>RESTRICTED</c>, or <c>UNSAFE</c> as written (lower case), or null when not stated (G-197).</summary>
    public string? Parallel { get; init; }

    /// <summary><c>LEAKPROOF</c> (G-197); <c>NOT LEAKPROOF</c> and no clause are both false.</summary>
    public bool IsLeakproof { get; init; }

    /// <summary>The <c>COST</c> as written, or null when not stated (G-197).</summary>
    public string? Cost { get; init; }

    /// <summary>The <c>ROWS</c> as written, or null when not stated (G-197).</summary>
    public string? Rows { get; init; }

    /// <summary><c>WINDOW</c> (G-197): a window function, which <c>CREATE OR REPLACE</c> cannot turn into an ordinary one or back.</summary>
    public bool IsWindow { get; init; }

    /// <summary>
    /// The link symbol of a C function, <c>AS 'library', 'symbol'</c> (G-211): the second string of <c>AS</c>, null when there is none (PostgreSQL then
    /// looks for the function's own name in the library). The library is the routine's body.
    /// </summary>
    public string? LinkSymbol { get; init; }

    /// <summary>The <c>SUPPORT</c> function's name parts joined with dots (<c>pg_catalog.textlike_support</c>), or null (G-205).</summary>
    public string? SupportFunction { get; init; }

    /// <summary>The <c>SUPPORT</c> clause as written, which is what is deployed (G-205); null when there is none or it could not be read.</summary>
    public string? WrittenSupportClause { get; init; }

    /// <summary>The types of <c>TRANSFORM FOR TYPE ...</c>, as the parser names them, in the order written (G-205).</summary>
    public IReadOnlyList<string> TransformTypes { get; init; } = [];

    /// <summary>The <c>TRANSFORM</c> clause as written, which is what is deployed (G-205); null when there is none or it could not be read.</summary>
    public string? WrittenTransformClause { get; init; }

    /// <summary>The <c>SUPPORT</c> clause to deploy: as written, or built from <see cref="SupportFunction"/>.</summary>
    public string? RenderSupportClause()
        => WrittenSupportClause ?? (SupportFunction is null ? null : "SUPPORT " + string.Join(".", SupportFunction.Split('.').Select(SqlIdentifiers.QuoteName)));

    /// <summary>The <c>TRANSFORM</c> clause to deploy: as written, or built from <see cref="TransformTypes"/>.</summary>
    public string? RenderTransformClause()
        => WrittenTransformClause ?? (TransformTypes.Count == 0 ? null : "TRANSFORM " + string.Join(", ", TransformTypes.Select(static type => "FOR TYPE " + SqlIdentifiers.QuoteTypeName(type))));

    /// <summary>The <c>SET</c> clauses, in the order written (G-197).</summary>
    public IReadOnlyList<RoutineSetting> Settings { get; init; } = [];

    /// <summary>
    /// Renders the parameter list as it will appear inside the regenerated CREATE header.
    /// </summary>
    public string RenderParameterList()
        => string.Join(", ", Parameters.Where(static parameter => !parameter.IsTableColumn).Select(RenderParameter));

    /// <summary>
    /// Renders the argument-type signature PostgreSQL uses to identify a specific overload:
    /// the raw (as-declared) data types of the <c>IN</c>, <c>INOUT</c>, and <c>VARIADIC</c>
    /// parameters, in declared order, comma-joined. <c>OUT</c>-only parameters are excluded
    /// because they do not participate in overload identity or in <c>DROP FUNCTION</c>/
    /// <c>DROP PROCEDURE</c> argument lists. The raw type text is preserved rather than
    /// canonicalized so genuinely distinct overloads (e.g. <c>varchar</c> vs <c>varchar(10)</c>,
    /// where applicable) are never conflated.
    /// </summary>
    public string RenderArgumentTypeSignature()
        => string.Join(
            ", ",
            Parameters
                .Where(static parameter => parameter.IsArgument)
                .Select(static parameter => parameter.DataType ?? "text"));

    /// <summary>
    /// <see cref="RenderArgumentTypeSignature"/> written for SQL (<c>DROP FUNCTION</c>, <c>COMMENT ON FUNCTION</c>): a type name that needs quotes
    /// (<c>"MySchema"."Mood"</c>) has them. The plain form stays the identity used to build routine keys (G-70).
    /// </summary>
    /// <summary>True when a parameter is <c>OUT</c>: a procedure's <c>OUT</c> parameters are part of its signature from PostgreSQL 14 (G-232).</summary>
    public bool HasOutParameter
        => Parameters.Any(static parameter => string.Equals(parameter.Mode, "OUT", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Every parameter but a <c>RETURNS TABLE</c> column, each with its mode spelled out (<c>OUT integer, IN integer</c>), for a statement that names a procedure whose name an overload with an
    /// <c>OUT</c> parameter shares (G-232): a bare type list matches both the procedure's input arguments and another's full list, and PostgreSQL refuses it as not unique.
    /// </summary>
    public string RenderFullIdentitySql()
        => string.Join(
            ", ",
            Parameters
                .Where(static parameter => !parameter.IsTableColumn)
                .Select(static parameter => $"{(string.IsNullOrWhiteSpace(parameter.Mode) ? "IN" : parameter.Mode.ToUpperInvariant())} {parameter.WrittenDataType ?? SqlIdentifiers.QuoteTypeName(parameter.DataType ?? "text")}"));

    public string RenderArgumentTypeSignatureSql()
        => string.Join(
            ", ",
            Parameters
                .Where(static parameter => parameter.IsArgument)
                .Select(static parameter => parameter.WrittenDataType ?? SqlIdentifiers.QuoteTypeName(parameter.DataType ?? "text")));

    private static string RenderParameter(RoutineParameter parameter)
    {
        var builder = new System.Text.StringBuilder();

        if (!string.IsNullOrWhiteSpace(parameter.Mode)
            && !string.Equals(parameter.Mode, "IN", StringComparison.OrdinalIgnoreCase))
        {
            builder.Append(parameter.Mode).Append(' ');
        }

        if (!string.IsNullOrWhiteSpace(parameter.Name))
        {
            builder.Append(SqlIdentifiers.QuoteName(parameter.Name)).Append(' ');
        }

        builder.Append(parameter.WrittenDataType ?? SqlIdentifiers.QuoteTypeName(parameter.DataType ?? "text"));

        if (!string.IsNullOrWhiteSpace(parameter.DefaultExpression))
        {
            builder.Append(" DEFAULT ").Append(parameter.DefaultExpression);
        }

        return builder.ToString();
    }
}
