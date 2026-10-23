namespace PgProj.Semantics;

public sealed record RangeTypeOptionsDefinition(
    string? Subtype,
    string? SubtypeOperatorClass,
    string? CollationName,
    string? CanonicalFunction,
    string? SubtypeDiffFunction)
{
    /// <summary>
    /// <c>MULTIRANGE_TYPE_NAME</c> as written, which is what is deployed (G-204); null when the range states none and PostgreSQL names the
    /// multirange itself (<see cref="DefaultMultirangeName"/>).
    /// </summary>
    public string? MultirangeTypeName { get; init; }

    /// <summary>
    /// The name PostgreSQL gives a range's multirange when it states none: the range's name with its first <c>range</c> replaced by
    /// <c>multirange</c>, or with <c>_multirange</c> added when it has no <c>range</c> (G-204).
    /// </summary>
    public static string DefaultMultirangeName(string rangeName)
    {
        var index = rangeName.IndexOf("range", StringComparison.Ordinal);
        return index < 0 ? rangeName + "_multirange" : rangeName[..index] + "multirange" + rangeName[(index + "range".Length)..];
    }

    /// <summary>
    /// The subtype in the canonical form a column's type is compared in (<see cref="ColumnTypeNormalizer.BuildComparableTypeSignature"/>),
    /// built from pg_query's parse of it, so <c>float8</c> and PostgreSQL's <c>double precision</c> are one subtype (G-115). Null when the
    /// parse was not available; <see cref="Subtype"/> is compared then, and <see cref="Subtype"/> stays what is deployed.
    /// </summary>
    public string? ComparableSubtype { get; init; }

    private readonly PgProj.Parser.PlaceSlot<SemanticLocation> _location = PgProj.Parser.PlaceSlot<SemanticLocation>.Empty;

    /// <summary>
    /// Where the definition was written: its file and range (G-309e), for the diagnostics about it; null for one read from a database. Never part of the
    /// definition's equality, never compared, and not in <c>--dump-model</c>.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public SemanticLocation? Location
    {
        get => _location.Value;
        init => _location = new PgProj.Parser.PlaceSlot<SemanticLocation>(value);
    }
}
