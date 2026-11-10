namespace PgProj.Parser;

/// <summary>
/// A shell type, <c>CREATE TYPE name;</c>: a placeholder PostgreSQL needs so that a base type's input and output functions can name the type before it is defined.
/// A base type with its definition is a <see cref="TypeNode"/> of form <c>base</c> (G-267). A transient marker: <see cref="BaseTypeApplier"/> takes it out before any
/// semantic pass, and warns when the project has no definition for it, since a shell alone is not modeled.
/// </summary>
/// <param name="IsShell">Always true: only a shell is a marker since G-267.</param>
public sealed record BaseTypeNode(string? Name, string? SchemaName, bool IsShell) : SqlNode("BaseType", Name, SchemaName);
