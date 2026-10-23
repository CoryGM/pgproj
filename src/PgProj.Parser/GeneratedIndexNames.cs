namespace PgProj.Parser;

/// <summary>The name PostgreSQL's <c>ChooseIndexName</c> gives an index created without one, for a caller outside the parser (G-271).</summary>
public static class GeneratedIndexNames
{
    /// <summary>
    /// The name PostgreSQL gives an unnamed plain index on <paramref name="tableName"/> with these key elements and <paramref name="includeColumns"/>: the table, the
    /// columns (an expression named as <c>FigureIndexColname</c> names it), and <c>idx</c>, clipped to 63 bytes, with a number while <paramref name="isTaken"/> says a
    /// relation in the schema has the name. It is the name an index cloned onto a partition from its parent's gets, which the live read recognizes as the parent's.
    /// </summary>
    public static string Choose(string tableName, IEnumerable<IndexKeyElementNode> keyElements, IEnumerable<string> includeColumns, Func<string, bool> isTaken)
    {
        ArgumentNullException.ThrowIfNull(tableName);
        ArgumentNullException.ThrowIfNull(keyElements);
        ArgumentNullException.ThrowIfNull(includeColumns);
        ArgumentNullException.ThrowIfNull(isTaken);

        var addition = DefaultConstraintNamer.BuildAddition(TableLikeExpander.ChooseIndexColumnNames(
            [.. keyElements.Select(static element => element.Name ?? TableLikeExpander.FigureIndexColumnName(element.ExpressionAst)), .. includeColumns]));
        var name = DefaultConstraintNamer.MakeObjectName(tableName, addition, "idx");
        for (var pass = 1; isTaken(name); pass++)
        {
            name = DefaultConstraintNamer.MakeObjectName(tableName, addition, "idx" + pass.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return name;
    }
}
