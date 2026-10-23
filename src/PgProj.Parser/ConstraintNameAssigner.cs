namespace PgProj.Parser;

/// <summary>
/// Gives every constraint declared without a name the default name PostgreSQL would assign, across all
/// parsed files at once (G-42).
/// </summary>
/// <remarks>
/// Names are chosen as if the project were deployed from scratch, statement by statement, in the order the
/// syntax trees (files) and nodes are supplied. PostgreSQL avoids a name already used by any constraint in
/// the schema, and, for constraints backed by an index (primary key, unique, exclusion), any table, view,
/// sequence, or index name too. Explicit names anywhere in the project are reserved first, so an unnamed
/// constraint never takes a name an explicit one uses.
/// <para>
/// PostgreSQL picks a numeric suffix (<c>t_a_check1</c>) from the order statements actually run, which is
/// unknowable across files and against an already-deployed database. Whenever a suffix is needed because of
/// something other than an earlier generated name in the same file, a warning recommends naming the
/// constraint explicitly. A suffix caused only by an earlier unnamed constraint in the same file is
/// deterministic source order and is not reported.
/// </para>
/// </remarks>
public static class ConstraintNameAssigner
{
    /// <summary>The <see cref="SqlDiagnostic.Source"/> of the warnings this class adds to a syntax tree.</summary>
    public const string DiagnosticSource = "ConstraintNaming";

    private const string DefaultSchema = "public";

    /// <summary>
    /// Returns copies of <paramref name="trees"/> in which unnamed constraints carry their generated names
    /// (<see cref="ConstraintNode.IsNameGenerated"/> is true) and ambiguity warnings are added to the owning
    /// tree's diagnostics. Calling it again on its own output yields the same result.
    /// </summary>
    public static IReadOnlyList<SqlSyntaxTree> Assign(IReadOnlyList<SqlSyntaxTree> trees)
    {
        ArgumentNullException.ThrowIfNull(trees);

        // Recompute from scratch so the operation is idempotent.
        var nodesByTree = trees
            .Select(static tree => tree.Nodes
                .Select(static node => node switch
                {
                    ConstraintNode { IsNameGenerated: true } generated => generated with { Name = null, IsNameGenerated = false },
                    IndexNode { IsNameGenerated: true } generatedIndex => generatedIndex with { Name = null, IsNameGenerated = false },
                    _ => node,
                })
                .ToList())
            .ToList();

        var constraintNames = new Dictionary<string, Dictionary<string, NameOwner>>(PgProj.Parser.SqlIdentifiers.NameComparer);
        var relationNames = new Dictionary<string, Dictionary<string, NameOwner>>(PgProj.Parser.SqlIdentifiers.NameComparer);

        for (var treeIndex = 0; treeIndex < nodesByTree.Count; treeIndex++)
        {
            foreach (var node in nodesByTree[treeIndex])
            {
                ReserveExplicitName(node, treeIndex, constraintNames, relationNames);
            }
        }

        var warningsByTree = new List<SqlDiagnostic>?[trees.Count];
        for (var treeIndex = 0; treeIndex < nodesByTree.Count; treeIndex++)
        {
            var nodes = nodesByTree[treeIndex];
            for (var nodeIndex = 0; nodeIndex < nodes.Count; nodeIndex++)
            {
                // An index written without a name is named as PostgreSQL's ChooseIndexName names it (G-225): its table, its columns, and idx, with a number
                // while a relation in the schema has the name.
                if (nodes[nodeIndex] is IndexNode { Name: null or "", TableName: { Length: > 0 } indexTable } index)
                {
                    var indexSchema = NormalizeSchema(index.SchemaName);
                    var indexRelations = GetScope(relationNames, indexSchema);
                    var indexAddition = DefaultConstraintNamer.BuildAddition(TableLikeExpander.ChooseIndexColumnNames(
                        [.. index.KeyElements.Select(static element => element.Name ?? TableLikeExpander.FigureIndexColumnName(element.ExpressionAst)), .. index.IncludeColumns]));
                    var indexBaseName = DefaultConstraintNamer.MakeObjectName(indexTable, indexAddition, "idx");
                    var indexName = indexBaseName;
                    var indexOrderDependent = false;
                    for (var pass = 1; indexRelations.TryGetValue(indexName, out var indexOwner); pass++)
                    {
                        indexOrderDependent |= !(indexOwner.TreeIndex == treeIndex && indexOwner.IsGenerated);
                        indexName = DefaultConstraintNamer.MakeObjectName(indexTable, indexAddition, "idx" + pass.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    }

                    indexRelations[indexName] = new NameOwner(treeIndex, IsGenerated: true);
                    nodes[nodeIndex] = index with { Name = indexName, IsNameGenerated = true };
                    if (indexOrderDependent)
                    {
                        (warningsByTree[treeIndex] ??= []).Add(new SqlDiagnostic(
                            SqlDiagnosticSeverity.Warning,
                            $"Unnamed index on '{indexSchema}.{indexTable}' was given the generated name '{indexName}' because '{indexBaseName}' is already used by another relation in the schema. "
                            + "PostgreSQL chooses this name from statement order, which pgproj cannot know across files or against a deployed database; give the index an explicit name.",
                            DiagnosticSource));
                    }

                    continue;
                }

                if (nodes[nodeIndex] is not ConstraintNode { Name: null or "" } constraint
                    || !DefaultConstraintNamer.TryGetNameParts(constraint, out var table, out var addition, out var label))
                {
                    continue;
                }

                var schema = NormalizeSchema(constraint.SchemaName);
                var indexBacked = DefaultConstraintNamer.IsIndexBacked(constraint.ConstraintType);
                var constraintScope = GetScope(constraintNames, schema);
                var relationScope = GetScope(relationNames, schema);

                var baseName = DefaultConstraintNamer.MakeObjectName(table, addition, label);
                var name = baseName;
                var orderDependent = false;
                for (var pass = 1; TryFindOwner(name, indexBacked, constraintScope, relationScope, out var owner); pass++)
                {
                    // An earlier unnamed constraint in the same file is resolved by source order alone.
                    orderDependent |= !(owner.TreeIndex == treeIndex && owner.IsGenerated);
                    name = DefaultConstraintNamer.MakeObjectName(table, addition, label + pass.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }

                var generated = new NameOwner(treeIndex, IsGenerated: true);
                constraintScope[name] = generated;
                if (indexBacked)
                {
                    relationScope[name] = generated;
                }

                nodes[nodeIndex] = constraint with { Name = name, IsNameGenerated = true };

                if (orderDependent)
                {
                    (warningsByTree[treeIndex] ??= []).Add(new SqlDiagnostic(
                        SqlDiagnosticSeverity.Warning,
                        $"Unnamed {Describe(constraint.ConstraintType)} constraint on '{schema}.{table}' was given the generated name '{name}' "
                        + $"because '{baseName}' is already used by another constraint or relation in the schema. "
                        + "PostgreSQL chooses this name from statement order, which pgproj cannot know across files or against a deployed "
                        + "database; give the constraint an explicit name.",
                        DiagnosticSource));
                }
            }
        }

        var result = new List<SqlSyntaxTree>(trees.Count);
        for (var treeIndex = 0; treeIndex < trees.Count; treeIndex++)
        {
            var diagnostics = trees[treeIndex].Diagnostics
                .Where(static diagnostic => diagnostic.Source != DiagnosticSource)
                .Concat(warningsByTree[treeIndex] ?? [])
                .ToArray();

            result.Add(trees[treeIndex] with { Nodes = nodesByTree[treeIndex], Diagnostics = diagnostics });
        }

        return result;
    }

    private static void ReserveExplicitName(
        SqlNode node,
        int treeIndex,
        Dictionary<string, Dictionary<string, NameOwner>> constraintNames,
        Dictionary<string, Dictionary<string, NameOwner>> relationNames)
    {
        if (string.IsNullOrWhiteSpace(node.Name))
        {
            return;
        }

        var explicitOwner = new NameOwner(treeIndex, IsGenerated: false);
        var schema = NormalizeSchema(node.SchemaName);

        switch (node)
        {
            case ConstraintNode { CopiedByLike: not null } copied when !DefaultConstraintNamer.IsIndexBacked(copied.ConstraintType):
                // A CHECK that LIKE copies keeps its source's name (G-221), which the source itself already holds.
                break;
            case ConstraintNode constraint:
                GetScope(constraintNames, schema)[node.Name] = explicitOwner;
                if (DefaultConstraintNamer.IsIndexBacked(constraint.ConstraintType))
                {
                    GetScope(relationNames, schema)[node.Name] = explicitOwner;
                }

                break;
            case TableNode or ViewNode or SequenceNode or IndexNode:
                GetScope(relationNames, schema)[node.Name] = explicitOwner;
                break;
        }
    }

    private static bool TryFindOwner(
        string name,
        bool indexBacked,
        Dictionary<string, NameOwner> constraintScope,
        Dictionary<string, NameOwner> relationScope,
        out NameOwner owner)
    {
        if (constraintScope.TryGetValue(name, out owner!))
        {
            return true;
        }

        // Only index-backed constraints must also avoid relation names.
        return indexBacked && relationScope.TryGetValue(name, out owner!);
    }

    private static Dictionary<string, NameOwner> GetScope(Dictionary<string, Dictionary<string, NameOwner>> scopes, string schema)
    {
        if (!scopes.TryGetValue(schema, out var scope))
        {
            scope = new Dictionary<string, NameOwner>(StringComparer.Ordinal);
            scopes[schema] = scope;
        }

        return scope;
    }

    private static string NormalizeSchema(string? schemaName)
        => string.IsNullOrWhiteSpace(schemaName) ? DefaultSchema : schemaName.Trim();

    private static string Describe(string? constraintType)
        => constraintType?.Trim().ToUpperInvariant() switch
        {
            DefaultConstraintNamer.PrimaryKeyConstraintType => "primary key",
            DefaultConstraintNamer.UniqueConstraintType => "unique",
            DefaultConstraintNamer.ForeignKeyConstraintType => "foreign key",
            DefaultConstraintNamer.CheckConstraintType => "check",
            DefaultConstraintNamer.ExclusionConstraintType => "exclusion",
            _ => "table",
        };

    private sealed record NameOwner(int TreeIndex, bool IsGenerated);
}
