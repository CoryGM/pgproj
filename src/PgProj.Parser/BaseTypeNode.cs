namespace PgProj.Parser;

/// <summary>
/// A base type, <c>CREATE TYPE name (INPUT = ..., OUTPUT = ...)</c>, or a shell type, <c>CREATE TYPE name;</c> (G-241). Neither is modeled: a base type needs C or
/// internal functions and a superuser to create, so it is reported as not modeled, like any other statement pgproj does not deploy. A transient marker:
/// <see cref="BaseTypeApplier"/> uses it to leave the type's support functions (and their owners, comments, and grants) out of the project too, and then
/// removes it, so no semantic pass sees it. Modeling base types is G-267.
/// </summary>
/// <param name="IsShell">True for <c>CREATE TYPE name;</c>, which has no definition and names no function.</param>
public sealed record BaseTypeNode(string? Name, string? SchemaName, bool IsShell) : SqlNode("BaseType", Name, SchemaName)
{
    /// <summary>
    /// The functions the definition names (<c>INPUT</c>, <c>OUTPUT</c>, <c>RECEIVE</c>, <c>SEND</c>, <c>TYPMOD_IN</c>, <c>TYPMOD_OUT</c>, <c>ANALYZE</c>, <c>SUBSCRIPT</c>),
    /// each with its schema as written (null when unqualified), in the order written.
    /// </summary>
    public IReadOnlyList<BaseTypeSupportFunction> SupportFunctions { get; init; } = [];
}

/// <summary>One function a base type's definition names, with its schema as written (null when unqualified).</summary>
public sealed record BaseTypeSupportFunction(string? SchemaName, string Name);
