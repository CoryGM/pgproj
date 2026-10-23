namespace PgProj.Parser;

/// <summary>
/// A <c>CREATE COLLATION name (PROVIDER = ..., LOCALE = ..., ...)</c> statement (G-148): a named way of ordering and comparing text, in a schema. Only the form with
/// options is read; <c>CREATE COLLATION name FROM other</c> copies another collation's attributes and is reported as not modeled. <c>VERSION</c> is not read: it is the
/// version of the collation library the object was made against, which the server records itself and a project would never state.
/// </summary>
public sealed record CollationNode(string? Name, string? SchemaName) : SqlNode("Collation", Name, SchemaName)
{
    /// <summary>The provider in lower case: <c>libc</c> (PostgreSQL's default when none is written), <c>icu</c>, or <c>builtin</c> (PostgreSQL 17 and later).</summary>
    public string Provider { get; init; } = "libc";

    /// <summary>The locale of an <c>icu</c> or <c>builtin</c> collation (<c>LOCALE</c>, or <c>LC_COLLATE</c> when that is all that is written); null for <c>libc</c>.</summary>
    public string? Locale { get; init; }

    /// <summary>A <c>libc</c> collation's <c>LC_COLLATE</c> (the one <c>LOCALE</c> sets for both when it is written); null for the other providers.</summary>
    public string? LcCollate { get; init; }

    /// <summary>A <c>libc</c> collation's <c>LC_CTYPE</c>; null for the other providers.</summary>
    public string? LcCtype { get; init; }

    /// <summary>False for <c>DETERMINISTIC = false</c>, a collation that treats differently-encoded strings as equal; true otherwise.</summary>
    public bool IsDeterministic { get; init; } = true;

    /// <summary>An <c>icu</c> collation's tailoring rules (<c>RULES</c>, PostgreSQL 16 and later); null when there are none.</summary>
    public string? Rules { get; init; }
}
