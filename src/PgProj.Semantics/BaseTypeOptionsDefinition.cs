namespace PgProj.Semantics;

/// <summary>
/// A base type's options (G-267), in the form they are compared in: every option PostgreSQL stores, with its default filled in when the definition does not state it
/// (checked on PostgreSQL 14 and 18: <c>INTERNALLENGTH</c> <c>VARIABLE</c>, not <c>PASSEDBYVALUE</c>, <c>ALIGNMENT</c> <c>int4</c>, <c>STORAGE</c> <c>plain</c>,
/// <c>CATEGORY</c> <c>U</c>, not <c>PREFERRED</c>, <c>DELIMITER</c> <c>,</c>, not <c>COLLATABLE</c>, no other function, and <c>SUBSCRIPT</c>
/// <c>raw_array_subscript_handler</c> when an <c>ELEMENT</c> is stated without one). A function is named <c>schema.name</c>, as PostgreSQL resolved it; a type by its
/// comparison signature. What is deployed is the statement as written (<see cref="TypeDefinition.BaseTypeWrittenText"/>), never this.
/// </summary>
public sealed record BaseTypeOptionsDefinition
{
    /// <summary>The options that change in place, with <c>ALTER TYPE ... SET</c>; every other change drops and creates the type.</summary>
    public static readonly IReadOnlyList<string> AlterableOptions = ["RECEIVE", "SEND", "TYPMOD_IN", "TYPMOD_OUT", "ANALYZE", "SUBSCRIPT", "STORAGE"];

    /// <summary>The options that name a function, in the order <c>CREATE TYPE</c> lists them.</summary>
    public static readonly IReadOnlyList<string> FunctionOptions = ["INPUT", "OUTPUT", "RECEIVE", "SEND", "TYPMOD_IN", "TYPMOD_OUT", "ANALYZE", "SUBSCRIPT"];

    /// <summary>Each function option's function, <c>schema.name</c>, by option name (upper case); an option with none is not in it.</summary>
    public IReadOnlyDictionary<string, string> Functions { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Each function option's function as written (<c>t_in</c>, <c>app."T in"</c>), by option name, for <c>ALTER TYPE ... SET</c>.</summary>
    public IReadOnlyDictionary<string, string> WrittenFunctions { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary><c>INTERNALLENGTH</c> in bytes, -1 for <c>VARIABLE</c>; null when it comes from <see cref="Like"/>.</summary>
    public int? InternalLength { get; init; } = -1;

    /// <summary><c>PASSEDBYVALUE</c>; null when it comes from <see cref="Like"/>.</summary>
    public bool? PassedByValue { get; init; } = false;

    /// <summary><c>ALIGNMENT</c> as <c>pg_type.typalign</c> stores it (<c>c</c>, <c>s</c>, <c>i</c>, <c>d</c>); null when it comes from <see cref="Like"/>.</summary>
    public string? Alignment { get; init; } = "i";

    /// <summary><c>STORAGE</c> as <c>pg_type.typstorage</c> stores it (<c>p</c>, <c>e</c>, <c>m</c>, <c>x</c>); null when it comes from <see cref="Like"/>.</summary>
    public string? Storage { get; init; } = "p";

    /// <summary>
    /// <c>LIKE</c>'s type, by its comparison signature, while the four options it gives are not known: a database stores them, not <c>LIKE</c>, so against a database the
    /// server resolves it (<c>ServerBaseTypeLikeResolver</c>), and between two projects it is compared as written. Null when not written, or once resolved.
    /// </summary>
    public string? Like { get; init; }

    /// <summary><c>LIKE</c>'s type as written (<c>int4</c>, <c>app."My Type"</c>), for the server to resolve; never compared.</summary>
    public string? WrittenLike { get; init; }

    /// <summary><c>CATEGORY</c>, one character.</summary>
    public string Category { get; init; } = "U";

    /// <summary><c>PREFERRED</c>.</summary>
    public bool Preferred { get; init; }

    /// <summary><c>DEFAULT</c>'s text, or null for none.</summary>
    public string? Default { get; init; }

    /// <summary><c>ELEMENT</c>'s type by its comparison signature, or null for none.</summary>
    public string? Element { get; init; }

    /// <summary><c>DELIMITER</c>, one character.</summary>
    public string Delimiter { get; init; } = ",";

    /// <summary><c>COLLATABLE</c>.</summary>
    public bool Collatable { get; init; }

    /// <summary>The option names the definition states (upper case), so a <c>LIKE</c> resolved later keeps an option written beside it.</summary>
    public IReadOnlySet<string> Stated { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The options whose values differ, by name (upper case), in <see cref="FunctionOptions"/> order, then the others.</summary>
    public IReadOnlyList<string> DifferencesFrom(BaseTypeOptionsDefinition other)
    {
        ArgumentNullException.ThrowIfNull(other);

        var differences = new List<string>();
        foreach (var option in FunctionOptions)
        {
            if (!string.Equals(Functions.GetValueOrDefault(option), other.Functions.GetValueOrDefault(option), StringComparison.Ordinal))
            {
                differences.Add(option);
            }
        }

        void Compare<T>(string name, T mine, T theirs)
        {
            if (!EqualityComparer<T>.Default.Equals(mine, theirs))
            {
                differences.Add(name);
            }
        }

        Compare("INTERNALLENGTH", InternalLength, other.InternalLength);
        Compare("PASSEDBYVALUE", PassedByValue, other.PassedByValue);
        Compare("ALIGNMENT", Alignment, other.Alignment);
        Compare("STORAGE", Storage, other.Storage);
        Compare("LIKE", Like, other.Like);
        Compare("CATEGORY", Category, other.Category);
        Compare("PREFERRED", Preferred, other.Preferred);
        Compare("DEFAULT", Default, other.Default);
        Compare("ELEMENT", Element, other.Element);
        Compare("DELIMITER", Delimiter, other.Delimiter);
        Compare("COLLATABLE", Collatable, other.Collatable);
        return differences;
    }
}
