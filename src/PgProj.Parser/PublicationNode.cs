namespace PgProj.Parser;

/// <summary>
/// A table of a publication, with its optional column list and row filter (<c>FOR TABLE s.t (a, b) WHERE (a &gt; 0)</c>, PostgreSQL 15 and later) (G-134).
/// </summary>
public sealed record PublicationTableNode(string? SchemaName, string TableName)
{
    /// <summary>The columns of the column list, in the order written; empty when every column is published.</summary>
    public IReadOnlyList<string> Columns { get; init; } = [];

    /// <summary>The row filter as text (rendered from its tree, without the outer parentheses); null when there is none.</summary>
    public string? RawFilter { get; init; }

    /// <summary>The pg_query JSON of the row filter, for the comparison; null when there is none.</summary>
    public string? FilterAst { get; init; }
}

/// <summary>
/// A <c>CREATE PUBLICATION name [FOR ALL TABLES | FOR TABLE ... | FOR TABLES IN SCHEMA ...] [WITH (...)]</c> statement (G-134): a set of tables whose changes are sent to
/// subscribers. A publication belongs to no schema; its name is its identity. It names the tables, or whole schemas, or all tables, and the operations it publishes.
/// </summary>
public sealed record PublicationNode(string? Name) : SqlNode("Publication", Name, null)
{
    /// <summary>True for <c>FOR ALL TABLES</c>.</summary>
    public bool IsAllTables { get; init; }

    /// <summary>The tables named by <c>FOR TABLE</c>.</summary>
    public IReadOnlyList<PublicationTableNode> Tables { get; init; } = [];

    /// <summary>The schemas named by <c>FOR TABLES IN SCHEMA</c>; <c>CURRENT_SCHEMA</c> is given the default schema when the tree is defaulted.</summary>
    public IReadOnlyList<string> Schemas { get; init; } = [];

    /// <summary>The <c>WITH (...)</c> options by lower-case name, their values as text (<c>publish</c> as <c>insert, update</c>, the booleans as <c>true</c> or <c>false</c>).</summary>
    public IReadOnlyDictionary<string, string> Options { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// <c>ALTER PUBLICATION name { ADD | SET | DROP } { TABLE ... | TABLES IN SCHEMA ... }</c> or <c>ALTER PUBLICATION name SET (...)</c> (G-258). A transient marker:
/// <see cref="AlterPublicationApplier"/> folds it into the publication of that name, in any file, before the semantic passes run. <see cref="Action"/> is <c>ADD</c>, <c>SET</c>, or <c>DROP</c>;
/// for the options form it is <c>ADD</c> with no tables or schemas and <see cref="Options"/> set.
/// </summary>
public sealed record AlterPublicationNode(string? Name, string Action) : SqlNode("AlterPublication", Name, null)
{
    public IReadOnlyList<PublicationTableNode> Tables { get; init; } = [];

    public IReadOnlyList<string> Schemas { get; init; } = [];

    public IReadOnlyDictionary<string, string> Options { get; init; } = new Dictionary<string, string>();
}
