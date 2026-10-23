using System.Text;

namespace PgProj.Parser;

/// <summary>
/// Reproduces the names PostgreSQL gives to constraints declared without an explicit name
/// (<c>t_pkey</c>, <c>t_a_key</c>, <c>t_a_check</c>, <c>t_a_fkey</c>, <c>t_a_excl</c>), including its
/// 63-byte truncation and numeric de-duplication suffixes (<c>t_a_check1</c>). The live catalog reports
/// those generated names, so a project constraint must carry the same name to compare equal to its
/// deployed form (G-42).
/// </summary>
/// <remarks>
/// Ports <c>makeObjectName</c>, <c>ChooseIndexNameAddition</c>, <c>ChooseForeignKeyConstraintNameAddition</c>,
/// <c>ChooseRelationName</c>, and <c>ChooseConstraintName</c>. This class only builds candidate names;
/// <see cref="ConstraintNameAssigner"/> resolves clashes across all files in a schema.
/// </remarks>
internal static class DefaultConstraintNamer
{
    // NAMEDATALEN is 64, so identifiers hold at most 63 bytes.
    private const int MaxIdentifierBytes = 63;

    public const string PrimaryKeyConstraintType = "CONSTR_PRIMARY";
    public const string UniqueConstraintType = "CONSTR_UNIQUE";
    public const string ForeignKeyConstraintType = "CONSTR_FOREIGN";
    public const string CheckConstraintType = "CONSTR_CHECK";
    public const string ExclusionConstraintType = "CONSTR_EXCLUSION";

    /// <summary>
    /// Describes how PostgreSQL would name an unnamed constraint: the table, the column-derived middle part
    /// (or null), and the label (<c>pkey</c>, <c>key</c>, <c>fkey</c>, <c>check</c>, <c>excl</c>). Returns false
    /// when the constraint type does not receive a generated name.
    /// </summary>
    internal static bool TryGetNameParts(ConstraintNode constraint, out string table, out string? addition, out string label)
    {
        ArgumentNullException.ThrowIfNull(constraint);

        table = constraint.TableName?.Trim() ?? string.Empty;
        addition = null;
        label = string.Empty;

        if (table.Length == 0)
        {
            return false;
        }

        switch (constraint.ConstraintType?.Trim().ToUpperInvariant())
        {
            case PrimaryKeyConstraintType:
                label = "pkey";
                return true;
            case UniqueConstraintType:
                // The index's columns name it, its INCLUDE columns too, as ChooseIndexColumnNames gives them (G-226).
                label = "key";
                addition = BuildAddition(TableLikeExpander.ChooseIndexColumnNames([.. constraint.KeyColumns, .. constraint.IncludeColumns]));
                return true;
            case ForeignKeyConstraintType:
                label = "fkey";
                addition = BuildAddition(constraint.ForeignKeyColumns);
                return true;
            case CheckConstraintType:
                // A CHECK is named after its column only when it references exactly one distinct column.
                label = "check";
                addition = constraint.CheckReferencedColumnNames.Count == 1
                    ? constraint.CheckReferencedColumnNames[0]
                    : null;
                return true;
            case ExclusionConstraintType:
                label = "excl";
                addition = BuildAddition(TableLikeExpander.ChooseIndexColumnNames(
                    [.. constraint.ExclusionElements.Select(static element => element.Name ?? TableLikeExpander.FigureIndexColumnName(element.ExpressionAst)), .. constraint.IncludeColumns]));
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// True for constraint kinds backed by an index. PostgreSQL gives those names through
    /// <c>ChooseRelationName</c>, so they must also avoid table, view, sequence, and index names.
    /// </summary>
    internal static bool IsIndexBacked(string? constraintType)
        => constraintType?.Trim().ToUpperInvariant() is PrimaryKeyConstraintType or UniqueConstraintType or ExclusionConstraintType;

    /// <summary>Joins column names with underscores, stopping once 64 bytes are reached.</summary>
    internal static string? BuildAddition(IReadOnlyList<string> names)
    {
        if (names.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder();
        var length = 0;
        foreach (var name in names)
        {
            if (length > 0)
            {
                builder.Append('_');
                length++;
            }

            var clipped = ClipToBytes(name, MaxIdentifierBytes);
            builder.Append(clipped);
            length += Encoding.UTF8.GetByteCount(clipped);

            if (length >= MaxIdentifierBytes + 1)
            {
                break;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Builds <c>name1[_name2]_label</c>, truncating the longer of the two names first so the result fits in
    /// 63 bytes, and never splitting a multi-byte character.
    /// </summary>
    internal static string MakeObjectName(string name1, string? name2, string label)
    {
        var name1Bytes = Encoding.UTF8.GetByteCount(name1);
        var name2Bytes = name2 is null ? 0 : Encoding.UTF8.GetByteCount(name2);
        var overhead = (name2 is null ? 0 : 1) + label.Length + 1;
        var available = MaxIdentifierBytes - overhead;

        while (name1Bytes + name2Bytes > available)
        {
            if (name1Bytes > name2Bytes)
            {
                name1Bytes--;
            }
            else
            {
                name2Bytes--;
            }
        }

        var builder = new StringBuilder();
        builder.Append(ClipToBytes(name1, name1Bytes));
        if (name2 is not null)
        {
            builder.Append('_').Append(ClipToBytes(name2, name2Bytes));
        }

        return builder.Append('_').Append(label).ToString();
    }

    /// <summary>Keeps the longest prefix of <paramref name="value"/> that fits in <paramref name="maxBytes"/> UTF-8 bytes.</summary>
    private static string ClipToBytes(string value, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
        {
            return value;
        }

        var builder = new StringBuilder();
        var used = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            var runeBytes = rune.Utf8SequenceLength;
            if (used + runeBytes > maxBytes)
            {
                break;
            }

            builder.Append(rune.ToString());
            used += runeBytes;
        }

        return builder.ToString();
    }
}
