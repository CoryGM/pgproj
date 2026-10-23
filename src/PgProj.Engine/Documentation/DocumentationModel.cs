using System.Text.Json.Serialization;
using PgProj.Engine.Documentation;

namespace PgProj.Engine;

/// <summary>A reference from an object's <c>@see-also</c> tags: the text written, and the object in this document it names (null for a URL or an object that is not documented).</summary>
internal sealed record DocLink(string Value, string? Target);

/// <summary>What every documented object has: its name, the structure read from its comment, and the problems found in that comment.</summary>
internal abstract record DocItem
{
    [JsonPropertyOrder(-3)]
    public required string Name { get; set; }

    /// <summary>The name with its schema (<c>app.customer</c>, or <c>app.customer.email</c> for a column); also the anchor in the HTML and Markdown output.</summary>
    [JsonPropertyOrder(-2)]
    public required string QualifiedName { get; set; }

    [JsonPropertyOrder(-1)]
    public string? Description { get; set; }

    [JsonPropertyOrder(100)]
    public string? BusinessName { get; set; }

    [JsonPropertyOrder(101)]
    public string? Owner { get; set; }

    [JsonPropertyOrder(102)]
    public string? Classification { get; set; }

    [JsonPropertyOrder(103)]
    public bool? Pii { get; set; }

    [JsonPropertyOrder(104)]
    public DocDeprecation? Deprecated { get; set; }

    [JsonPropertyOrder(105)]
    public string? SinceVersion { get; set; }

    [JsonPropertyOrder(106)]
    public string? SinceDate { get; set; }

    [JsonPropertyOrder(107)]
    public int? RetentionMonths { get; set; }

    [JsonPropertyOrder(108)]
    public string? RetentionDate { get; set; }

    [JsonPropertyOrder(109)]
    public IReadOnlyList<string>? Examples { get; set; }

    [JsonPropertyOrder(110)]
    public IReadOnlyList<DocLink>? SeeAlso { get; set; }

    [JsonPropertyOrder(111)]
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? Custom { get; set; }

    /// <summary>The text of tags whose value could not be read, by name.</summary>
    [JsonPropertyOrder(112)]
    public IReadOnlyDictionary<string, string>? InvalidTags { get; set; }

    [JsonPropertyOrder(113)]
    public IReadOnlyList<DocIssue>? Issues { get; set; }
}

internal sealed record DocColumn : DocItem
{
    public required int Position { get; init; }

    public string? Type { get; init; }

    public bool Nullable { get; init; }

    public string? Default { get; init; }

    /// <summary>The expression of a generated column.</summary>
    public string? Generated { get; init; }

    /// <summary><c>ALWAYS</c> or <c>BY DEFAULT</c> for an identity column.</summary>
    public string? Identity { get; init; }

    public bool PrimaryKey { get; init; }

    public bool ForeignKey { get; init; }

    public bool Unique { get; init; }

    /// <summary>The columns this column's foreign keys point at, as <c>schema.table(column)</c>.</summary>
    public IReadOnlyList<string>? References { get; init; }
}

internal sealed record DocConstraint : DocItem
{
    public required string Type { get; init; }

    public IReadOnlyList<string> Columns { get; init; } = [];

    /// <summary>The check expression, the exclusion elements, or <c>schema.table(columns)</c> for a foreign key.</summary>
    public string? Definition { get; init; }

    public string? OnUpdate { get; init; }

    public string? OnDelete { get; init; }

    public bool Deferrable { get; init; }
}

/// <summary>A trigger on a table (G-163): when it fires, and what it runs.</summary>
internal sealed record DocTrigger : DocItem
{
    /// <summary><c>BEFORE</c>, <c>AFTER</c>, or <c>INSTEAD OF</c>.</summary>
    public string? Timing { get; init; }

    /// <summary>The events it fires on (<c>INSERT</c>, <c>UPDATE</c>, <c>DELETE</c>, <c>TRUNCATE</c>).</summary>
    public IReadOnlyList<string> Events { get; init; } = [];

    /// <summary><c>row</c> or <c>statement</c>.</summary>
    public string Level { get; init; } = "statement";

    /// <summary>The function it runs, with its arguments.</summary>
    public string? Function { get; init; }

    /// <summary>The <c>WHEN</c> condition, if it has one.</summary>
    public string? When { get; init; }

    public bool ConstraintTrigger { get; init; }
}

/// <summary>A row-level security policy on a table (G-164): what it applies to and the conditions it states.</summary>
internal sealed record DocPolicy : DocItem
{
    /// <summary><c>ALL</c>, <c>SELECT</c>, <c>INSERT</c>, <c>UPDATE</c>, or <c>DELETE</c>.</summary>
    public string Command { get; init; } = "ALL";

    /// <summary>True for a <c>PERMISSIVE</c> policy (the default), false for <c>RESTRICTIVE</c>.</summary>
    public bool Permissive { get; init; } = true;

    public IReadOnlyList<string> Roles { get; init; } = [];

    /// <summary>The <c>USING</c> expression: which existing rows the policy lets a role see or change.</summary>
    public string? Using { get; init; }

    /// <summary>The <c>WITH CHECK</c> expression: which new rows the policy lets a role write.</summary>
    public string? WithCheck { get; init; }
}

internal sealed record DocIndex : DocItem
{
    public string? Method { get; init; }

    public bool Unique { get; init; }

    public IReadOnlyList<string> Keys { get; init; } = [];

    public IReadOnlyList<string>? Include { get; init; }

    public string? Predicate { get; init; }
}

internal sealed record DocTable : DocItem
{
    public required string Schema { get; init; }

    /// <summary><c>table</c>, <c>partitioned table</c>, <c>partition</c>, or <c>typed table</c>.</summary>
    public string Kind { get; init; } = "table";

    public string? PartitionedBy { get; init; }

    public string? PartitionOf { get; init; }

    public IReadOnlyList<DocColumn> Columns { get; init; } = [];

    public IReadOnlyList<DocConstraint> Constraints { get; init; } = [];

    public IReadOnlyList<DocIndex> Indexes { get; init; } = [];

    public IReadOnlyList<DocTrigger> Triggers { get; init; } = [];

    public IReadOnlyList<DocPolicy> Policies { get; init; } = [];

    /// <summary>True when row-level security is enabled on the table.</summary>
    public bool RowSecurity { get; init; }
}

/// <summary>A view's column or a composite type's field; <paramref name="Description"/> is its comment (G-216).</summary>
internal sealed record DocViewColumn(string Name, string? Type, string? Description = null);

internal sealed record DocView : DocItem
{
    public required string Schema { get; init; }

    public bool Materialized { get; init; }

    public IReadOnlyList<DocViewColumn> Columns { get; init; } = [];

    public string? Definition { get; init; }
}

internal sealed record DocRoutine : DocItem
{
    public required string Schema { get; init; }

    /// <summary><c>function</c> or <c>procedure</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>The name with its argument types, which tells overloads apart.</summary>
    public required string Signature { get; init; }

    public string? Returns { get; init; }

    public string? Language { get; init; }

    public string? Volatility { get; init; }

    public bool SecurityDefiner { get; init; }
}

internal sealed record DocType : DocItem
{
    public required string Schema { get; init; }

    /// <summary><c>enum</c>, <c>composite</c>, <c>domain</c>, or <c>range</c> (as the model names it).</summary>
    public string? Kind { get; init; }

    public IReadOnlyList<string>? Labels { get; init; }

    public IReadOnlyList<DocViewColumn>? Fields { get; init; }

    public string? BaseType { get; init; }
}

internal sealed record DocSequence : DocItem
{
    public required string Schema { get; init; }

    public string? DataType { get; init; }

    public string? Increment { get; init; }

    public string? Minimum { get; init; }

    public string? Maximum { get; init; }

    public string? Start { get; init; }

    public bool? Cycle { get; init; }

    public string? OwnedBy { get; init; }
}

/// <summary>A schema: what its <c>COMMENT ON SCHEMA</c> says (G-160), and the objects in it.</summary>
internal sealed record DocSchema : DocItem
{
    public IReadOnlyList<DocTable> Tables { get; init; } = [];

    public IReadOnlyList<DocView> Views { get; init; } = [];

    public IReadOnlyList<DocRoutine> Routines { get; init; } = [];

    public IReadOnlyList<DocType> Types { get; init; } = [];

    public IReadOnlyList<DocSequence> Sequences { get; init; } = [];
}

/// <summary>A documentation problem with the object it is about.</summary>
public sealed record DocProblem(string Object, DocSeverity Severity, string Tag, string Message);

/// <param name="Kind"><c>project</c> or <c>database</c>.</param>
/// <param name="Name">The project's name, or the database's.</param>
internal sealed record DocSource(string Kind, string Name);

/// <summary>How many of each kind of object the documentation covers, and how many documentation problems it found.</summary>
/// <param name="Schemas">The schemas documented.</param>
/// <param name="Tables">The tables.</param>
/// <param name="Columns">The tables' columns.</param>
/// <param name="Views">The views and materialized views.</param>
/// <param name="Routines">The functions and procedures.</param>
/// <param name="Types">The types.</param>
/// <param name="Sequences">The sequences.</param>
/// <param name="Indexes">The tables' indexes.</param>
/// <param name="Constraints">The tables' constraints.</param>
/// <param name="Triggers">The tables' triggers.</param>
/// <param name="Policies">The tables' row-level security policies.</param>
/// <param name="Errors">The documentation problems that are errors (a tag whose value cannot be read).</param>
/// <param name="Warnings">The documentation problems that are warnings.</param>
public sealed record DocCounts(int Schemas, int Tables, int Columns, int Views, int Routines, int Types, int Sequences, int Indexes, int Constraints, int Triggers, int Policies, int Errors, int Warnings);

/// <summary>The documentation <c>pgproj document</c> writes (G-159): the data dictionary and the entity relationship diagram. The JSON output is this record as it stands.</summary>
internal sealed record DatabaseDocumentation
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>The line that marks the file as written by <c>pgproj document</c>, which is how a later run tells it from a file a person wrote.</summary>
    public string Generator { get; init; } = DocumentationFiles.Marker;

    public required DateTime GeneratedUtc { get; init; }

    public required DocSource Source { get; init; }

    /// <summary>The schemas the document is limited to by <c>--schemas</c> or <c>--exclude-schemas</c>; empty when it is not limited.</summary>
    public IReadOnlyList<string> SchemasOnly { get; init; } = [];

    public IReadOnlyList<string> SchemasExcluded { get; init; } = [];

    public required DocCounts Counts { get; init; }

    public required IReadOnlyList<DocSchema> Schemas { get; init; }

    /// <summary>The entity relationship diagram, as Mermaid text.</summary>
    public required string Erd { get; init; }

    public required IReadOnlyList<DocProblem> Problems { get; init; }
}
