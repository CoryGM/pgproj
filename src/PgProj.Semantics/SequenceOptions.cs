using System.Globalization;

namespace PgProj.Semantics;

/// <summary>
/// The options a sequence really has once PostgreSQL's defaults are applied (G-57). A project may write
/// <c>CREATE SEQUENCE s;</c>, or declare an <c>integer</c> identity column, and leave every option implicit; the live
/// catalog always reports all of them. The values a project wrote stay on <see cref="SequenceDefinition"/> for
/// deployment; this is the form used to compare two sequences.
/// </summary>
/// <param name="DataType">Canonical type: <c>int2</c>, <c>int4</c>, or <c>int8</c>.</param>
/// <param name="IncrementBy">Decimal text.</param>
/// <param name="MinValue">Decimal text.</param>
/// <param name="MaxValue">Decimal text.</param>
/// <param name="StartValue">Decimal text.</param>
/// <param name="CacheSize">Decimal text.</param>
/// <param name="Cycle">Whether the sequence wraps around.</param>
public sealed record SequenceOptions(
    string DataType,
    string IncrementBy,
    string MinValue,
    string MaxValue,
    string StartValue,
    string CacheSize,
    bool Cycle)
{
    /// <summary>
    /// Resolves the effective options exactly as <c>CREATE SEQUENCE</c> does. A value that is not a plain integer is kept as
    /// written (trimmed), so an unusual spelling is compared, never guessed at.
    /// </summary>
    internal static SequenceOptions Resolve(SequenceDefinition sequence)
    {
        var type = ResolveType(sequence.DataType);
        var (typeMin, typeMax) = Range(type);

        var increment = ParseOrNull(sequence.IncrementBy);
        var ascending = increment is null or > 0;

        var min = ParseOrNull(sequence.MinValue) ?? (ascending ? 1 : typeMin);
        var max = ParseOrNull(sequence.MaxValue) ?? (ascending ? typeMax : -1);
        var start = ParseOrNull(sequence.StartValue) ?? (ascending ? min : max);
        var cache = ParseOrNull(sequence.CacheSize) ?? 1;

        return new SequenceOptions(
            type,
            Format(increment ?? 1, sequence.IncrementBy),
            Format(min, sequence.MinValue),
            Format(max, sequence.MaxValue),
            Format(start, sequence.StartValue),
            Format(cache, sequence.CacheSize),
            sequence.Cycle == true);
    }

    private static string ResolveType(string? dataType)
    {
        // An identity column names its type the way the column was declared, so 'integer', 'int4', and
        // 'pg_catalog.int4' must all mean the same thing. PostgreSQL's default is bigint.
        return ColumnTypeNormalizer.NormalizeBaseType(dataType) switch
        {
            "int2" => "int2",
            "int4" => "int4",
            _ => "int8",
        };
    }

    private static (long Min, long Max) Range(string type)
        => type switch
        {
            "int2" => (short.MinValue, short.MaxValue),
            "int4" => (int.MinValue, int.MaxValue),
            _ => (long.MinValue, long.MaxValue),
        };

    private static long? ParseOrNull(string? value)
        => long.TryParse(value?.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    /// <summary>The canonical text of a parsed value, or the trimmed original when it was not a plain integer.</summary>
    private static string Format(long value, string? original)
        => string.IsNullOrWhiteSpace(original) || ParseOrNull(original) is not null
            ? value.ToString(CultureInfo.InvariantCulture)
            : original.Trim();
}
