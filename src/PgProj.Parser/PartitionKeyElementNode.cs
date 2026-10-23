namespace PgProj.Parser;

/// <summary>
/// One element of a <c>PARTITION BY RANGE/LIST/HASH (...)</c> key, in the order written (G-24c). Exactly one of
/// <see cref="Name"/> (a plain column) or <see cref="RawExpression"/> (anything else, sliced verbatim rather than re-rendered,
/// the same reasoning as <c>ConstraintIndexTextReader</c>, G-73) is set.
/// </summary>
public sealed record PartitionKeyElementNode(string? Name, string? RawExpression)
{
    /// <summary>The key column's <c>COLLATE</c>, its name parts joined with dots, or null when it states none (G-240). An expression's is part of its <see cref="RawExpression"/>.</summary>
    public string? CollationName { get; init; }

    /// <summary>The key column's operator class (<c>text_pattern_ops</c>), its name parts joined with dots, or null (G-240). An expression's is part of its <see cref="RawExpression"/>.</summary>
    public string? OperatorClassName { get; init; }

    /// <summary>The element as SQL: the column or expression, then <c>COLLATE</c> and the operator class when it states them (G-240). <paramref name="quoteName"/> quotes a collation or operator class name.</summary>
    public string ToSql(Func<string, string>? quoteName = null)
    {
        quoteName ??= static name => name;
        var text = Name ?? RawExpression ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(CollationName))
        {
            text += " COLLATE " + quoteName(CollationName);
        }

        if (!string.IsNullOrWhiteSpace(OperatorClassName))
        {
            text += " " + quoteName(OperatorClassName);
        }

        return text;
    }
}
