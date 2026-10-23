namespace PgProj.Parser;

/// <summary>
/// Leaves a base type's support functions out of the project with the type (G-241). A base type, <c>CREATE TYPE name (INPUT = ..., OUTPUT = ...)</c>, is not modeled
/// (<see cref="UnmodeledStatementReader"/> reports it), and the functions it names exist only to define it: they are C or internal functions, a non-superuser cannot
/// create them, and without the type they are useless. The live read leaves them out the same way, so a project that keeps them would never converge. Runs over every
/// tree before any semantic pass, like <see cref="AlterPublicationApplier"/>, because the type and its functions may be in different files. A function the type names is
/// matched by name in the schema written; an unqualified one in the type's own schema, or, failing that, in any schema where exactly one function has the name. The
/// owner, comment, and grants of a function left out, and of the type itself, are left out too. One warning per base type names what was left out. The result contains
/// none of the marker nodes.
/// </summary>
public static class BaseTypeApplier
{
    /// <summary>The <see cref="SqlDiagnostic.Source"/> of the diagnostics this class adds to a syntax tree.</summary>
    public const string DiagnosticSource = "BaseType";

    public static IReadOnlyList<SqlSyntaxTree> Apply(IReadOnlyList<SqlSyntaxTree> trees)
    {
        ArgumentNullException.ThrowIfNull(trees);

        if (!trees.Any(static tree => tree.Nodes.Any(static node => node is BaseTypeNode)))
        {
            return trees;
        }

        var functionSchemasByName = new Dictionary<string, HashSet<string>>(SqlIdentifiers.NameComparer);
        foreach (var function in trees.SelectMany(static tree => tree.Nodes).OfType<FunctionNode>())
        {
            if (function.Name is { } name && function.SchemaName is { } schema)
            {
                if (!functionSchemasByName.TryGetValue(name, out var schemas))
                {
                    functionSchemasByName[name] = schemas = new HashSet<string>(SqlIdentifiers.NameComparer);
                }

                schemas.Add(schema);
            }
        }

        // The types (shell or base) and the functions that go with them, as (schema, name).
        var types = new HashSet<(string Schema, string Name)>();
        var functions = new HashSet<(string Schema, string Name)>();
        var diagnosticsByTree = trees.Select(static tree => tree.Diagnostics.ToList()).ToList();
        for (var treeIndex = 0; treeIndex < trees.Count; treeIndex++)
        {
            foreach (var baseType in trees[treeIndex].Nodes.OfType<BaseTypeNode>())
            {
                if (baseType.Name is null || baseType.SchemaName is null)
                {
                    continue;
                }

                types.Add(Key(baseType.SchemaName, baseType.Name));
                var leftOut = new List<string>();
                foreach (var support in baseType.SupportFunctions)
                {
                    if (Resolve(support, baseType.SchemaName, functionSchemasByName) is { } schema && functions.Add(Key(schema, support.Name)))
                    {
                        leftOut.Add($"{schema}.{support.Name}");
                    }
                }

                if (leftOut.Count > 0)
                {
                    diagnosticsByTree[treeIndex].Add(new SqlDiagnostic(
                        SqlDiagnosticSeverity.Warning,
                        $"Base type {baseType.SchemaName}.{baseType.Name} is not modeled, so the functions it names are left out with it: {string.Join(", ", leftOut)}.",
                        DiagnosticSource));
                }
            }
        }

        var result = new List<SqlSyntaxTree>(trees.Count);
        for (var treeIndex = 0; treeIndex < trees.Count; treeIndex++)
        {
            var nodes = new List<SqlNode>();
            foreach (var node in trees[treeIndex].Nodes)
            {
                if (Keep(node, types, functions) is { } kept)
                {
                    nodes.Add(kept);
                }
            }

            result.Add(trees[treeIndex] with { Nodes = nodes, Diagnostics = diagnosticsByTree[treeIndex] });
        }

        return result;
    }

    /// <summary>The node as it stays in the project, or null when it belongs to a base type that is left out.</summary>
    private static SqlNode? Keep(SqlNode node, HashSet<(string Schema, string Name)> types, HashSet<(string Schema, string Name)> functions)
    {
        bool IsFunction(string? schema, string? name) => schema is not null && name is not null && functions.Contains(Key(schema, name));
        bool IsType(string? schema, string? name) => schema is not null && name is not null && types.Contains(Key(schema, name));

        switch (node)
        {
            case BaseTypeNode:
                return null;
            case FunctionNode function when IsFunction(function.SchemaName, function.Name):
                return null;
            case OwnerNode { ObjectKind: "Function" } owner when IsFunction(owner.SchemaName, owner.ObjectName):
                return null;
            case OwnerNode { ObjectKind: "Type" } owner when IsType(owner.SchemaName, owner.ObjectName):
                return null;
            case ObjectCommentNode { ObjectKind: "Function" } comment when IsFunction(comment.SchemaName, comment.TableName):
                return null;
            case ObjectCommentNode { ObjectKind: "Type" } comment when IsType(comment.SchemaName, comment.TableName):
                return null;
            case GrantNode { ObjectKind: "function" } grant when grant.Targets.Any(target => IsFunction(target.SchemaName, target.Name)):
                var targets = grant.Targets.Where(target => !IsFunction(target.SchemaName, target.Name)).ToArray();
                return targets.Length == 0 ? null : grant with { Targets = targets };
            default:
                return node;
        }
    }

    /// <summary>The schema of the project function a type's definition names, or null when the project declares none of that name.</summary>
    private static string? Resolve(BaseTypeSupportFunction support, string typeSchema, Dictionary<string, HashSet<string>> functionSchemasByName)
    {
        if (!functionSchemasByName.TryGetValue(support.Name, out var schemas))
        {
            return null;
        }

        if (support.SchemaName is { } written)
        {
            return schemas.Contains(written) ? written : null;
        }

        return schemas.Contains(typeSchema) ? typeSchema : schemas.Count == 1 ? schemas.Single() : null;
    }

    private static (string Schema, string Name) Key(string schema, string name) => (schema, name);
}
