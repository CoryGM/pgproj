namespace PgProj.Parser;

/// <summary>
/// A <c>CREATE EXTENSION</c> statement. <see cref="SqlNode.SchemaName"/> is the schema from an explicit <c>[WITH] SCHEMA ...</c> clause, or null
/// when the statement names none, and PostgreSQL chooses (G-213).
/// </summary>
public sealed record ExtensionNode(string? Name, string? SchemaName) : SqlNode("Extension", Name, SchemaName)
{
    /// <summary>The statement exactly as written, without its closing semicolon, which is what is deployed (G-213); null when it could not be read.</summary>
    public string? WrittenText { get; init; }

    /// <summary>The <c>VERSION</c> written, or null when none is.</summary>
    public string? Version { get; init; }

    /// <summary>True for <c>CASCADE</c>: the extensions this one requires are created with it when missing.</summary>
    public bool IsCascade { get; init; }
}
