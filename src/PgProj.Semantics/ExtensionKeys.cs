namespace PgProj.Semantics;

/// <summary>
/// An extension's key (G-213): <c>extension:name</c>. PostgreSQL names an extension by its name alone, so its schema is not part of its identity; the
/// prefix keeps the key apart from every schema-qualified one, as for the other kinds that have no schema.
/// </summary>
public static class ExtensionKeys
{
    public const string Prefix = "extension:";

    public static string Create(string name) => Prefix + name;
}
