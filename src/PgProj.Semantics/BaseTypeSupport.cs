namespace PgProj.Semantics;

/// <summary>
/// A base type and its support functions (G-267): the type names its input, output, and other functions, and they take or return the type, so each refers to the
/// other. PostgreSQL resolves that with a shell, <c>CREATE TYPE name;</c>, which stands in for the type until the functions exist, so the reference from the function
/// to the type is not a cycle: the type is created after its functions, and the functions after its shell.
/// </summary>
internal static class BaseTypeSupport
{
    /// <summary>True when <paramref name="reference"/> is a base type of <paramref name="model"/> that names <paramref name="node"/>, a function, as one of its support functions.</summary>
    public static bool IsShellEdge(DatabaseModel model, ISemanticNode node, string reference)
        => node is FunctionDefinition function
            && model.Types.TryGetValue(reference, out var type)
            && type.BaseTypeOptions?.Functions.Values.Contains($"{function.SchemaName ?? "public"}.{function.Name}", StringComparer.Ordinal) == true;
}
