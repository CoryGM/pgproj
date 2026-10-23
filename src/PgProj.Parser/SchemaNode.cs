namespace PgProj.Parser;

/// <summary>
/// A schema a file declares with <c>CREATE SCHEMA</c> (G-284), so the schema is deployed whether or not anything is in it. Its name is the one written, or for
/// <c>CREATE SCHEMA AUTHORIZATION role</c> the role's. A schema is never anything's object, so <see cref="SqlNode.SchemaName"/> is null.
/// </summary>
public sealed record SchemaNode(string Name) : SqlNode("Schema", Name, null);
