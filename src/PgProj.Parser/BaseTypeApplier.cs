namespace PgProj.Parser;

/// <summary>
/// Takes a project's shell types out before the semantic passes (G-267). A shell, <c>CREATE TYPE name;</c>, is how <c>pg_dump</c> lets a base type's input and output
/// functions name the type before it is defined, and with its definition, <c>CREATE TYPE name (INPUT = ..., OUTPUT = ...)</c>, in any file of the project, it is one
/// type, not a duplicate: <c>publish</c> writes the shell itself where it needs one. A shell the project never defines is not modeled, and is a warning. Runs over every
/// tree before any semantic pass, like <see cref="AlterPublicationApplier"/>, because the shell and the definition may be in different files. Before G-267 a base type
/// was not modeled at all (G-241), and this class left it and the functions it names out of the project. The result contains none of the marker nodes.
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

        var defined = trees
            .SelectMany(static tree => tree.Types)
            .Where(static type => type.Form == "base" && type.Name is not null && type.SchemaName is not null)
            .Select(static type => (type.SchemaName!, type.Name!))
            .ToHashSet();

        var result = new List<SqlSyntaxTree>(trees.Count);
        foreach (var tree in trees)
        {
            var diagnostics = tree.Diagnostics.ToList();
            foreach (var shell in tree.Nodes.OfType<BaseTypeNode>().Where(shell => shell.Name is not null && shell.SchemaName is not null && !defined.Contains((shell.SchemaName, shell.Name))))
            {
                diagnostics.Add(new SqlDiagnostic(
                    SqlDiagnosticSeverity.Warning,
                    $"CREATE TYPE (shell type) {shell.SchemaName}.{shell.Name} is not modeled and is ignored: the project has no definition for it (CREATE TYPE {shell.SchemaName}.{shell.Name} (INPUT = ..., OUTPUT = ...)).",
                    DiagnosticSource));
            }

            result.Add(tree with { Nodes = tree.Nodes.Where(static node => node is not BaseTypeNode).ToList(), Diagnostics = diagnostics });
        }

        return result;
    }
}
