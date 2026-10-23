using PgProj.Parser;

namespace PgProj.Semantics;

public sealed class SemanticPipeline
{
    private readonly DeclarationPass _declarationPass = new();
    private readonly ColumnPass _columnPass = new();
    private readonly PartitionColumnInheritancePass _partitionColumnInheritancePass = new();
    private readonly TableInheritancePass _tableInheritancePass = new();
    private readonly InheritedColumnDefaultPass _inheritedColumnDefaultPass = new();
    private readonly ConstraintPass _constraintPass = new();
    private readonly ExpressionMappingPass _expressionMappingPass = new();
    private readonly DeepDependencyPass _deepDependencyPass = new();
    private readonly DeepColumnDefaultPass _deepColumnDefaultPass = new();
    private readonly DeepIndexExpressionPass _deepIndexExpressionPass = new();
    private readonly DeepTypeStructurePass _deepTypeStructurePass = new();
    private readonly DeepConstraintMappingPass _deepConstraintMappingPass = new();
    private readonly QueryBodyBindingPass _queryBodyBindingPass = new();
    private readonly PlpgsqlBodyReferencePass _plpgsqlBodyReferencePass = new();
    private readonly SearchPathValidationPass _searchPathValidationPass = new();
    private readonly ExpressionNormalizationPass _expressionNormalizationPass = new();
    private readonly ExpressionSemanticBindingPass _expressionSemanticBindingPass = new();
    private readonly ViewSemanticPass _viewSemanticPass = new();
    private readonly ProcedureIdentityPass _procedureIdentityPass = new();
    private readonly SemanticDiffNormalizationPass _semanticDiffNormalizationPass = new();
    private readonly DependencyPass _dependencyPass = new();
    private readonly SemanticBinder _semanticBinder = new();
    private readonly TypeResolutionPass _typeResolutionPass = new();
    private readonly FunctionSignatureValidationPass _functionSignatureValidationPass = new();
    private readonly SemanticCyclePass _semanticCyclePass = new();
    private readonly SemanticGraphPass _semanticGraphPass = new();
    private readonly SemanticCycleExplanationPass _semanticCycleExplanationPass = new();
    private readonly ValidationPass _validationPass = new();
    private readonly ErrorRecoveryPass _errorRecoveryPass = new();

    public DatabaseModel Execute(
        IEnumerable<SqlSyntaxTree> syntaxTrees,
        IReadOnlyList<string>? schemas = null,
        bool excludeGrants = false,
        bool excludeRoleMemberships = false,
        IReadOnlyList<string>? searchPath = null,
        bool excludeOwners = false,
        IReadOnlyList<SqlSyntaxTree>? likeSourceTrees = null)
    {
        ArgumentNullException.ThrowIfNull(syntaxTrees);
        var trees = syntaxTrees as IReadOnlyList<SqlSyntaxTree> ?? syntaxTrees.ToArray();

        // The passes walk expression trees recursively, so they run on a large stack (G-272).
        return LargeStack.Run(() => ExecuteOnLargeStack(trees, schemas, excludeGrants, excludeRoleMemberships, searchPath, excludeOwners, likeSourceTrees));
    }

    private DatabaseModel ExecuteOnLargeStack(
        IReadOnlyList<SqlSyntaxTree> syntaxTrees,
        IReadOnlyList<string>? schemas,
        bool excludeGrants,
        bool excludeRoleMemberships,
        IReadOnlyList<string>? searchPath,
        bool excludeOwners,
        IReadOnlyList<SqlSyntaxTree>? likeSourceTrees)
    {

        var model = DatabaseModel.Empty;
        model.Schemas = schemas ?? [];
        model.SearchPath = searchPath is { Count: > 0 } ? searchPath : ["public"];

        // A tenant template's files carry their own path, its first schema the one they were read for (G-290); every object in that schema resolves
        // its unqualified names along it.
        model.SchemaSearchPaths = syntaxTrees
            .Where(static tree => tree.SearchPath is { Count: > 0 })
            .GroupBy(static tree => tree.SearchPath![0], PgProj.Parser.SqlIdentifiers.NameComparer)
            .ToDictionary(static group => group.Key, static group => group.First().SearchPath!, PgProj.Parser.SqlIdentifiers.NameComparer);

        // An unqualified reference (a table an index or ALTER TABLE names, a type, a trigger's function) takes the schema the search path
        // finds it in, before ALTER TABLE columns join their tables (G-47) and before anything is keyed (G-93). Then unnamed constraints
        // receive PostgreSQL's default names across all files.
        // A typed table is given the columns of its composite type before its columns are read or an ALTER TABLE joins them (G-136).
        // A base type is not modeled, and the functions it names are left out with it (G-241).
        var qualifiedTrees = BaseTypeApplier.Apply(SearchPathQualifier.Apply(syntaxTrees, model.SearchPath, model.SchemaSearchPaths));
        var (expandedTrees, typedTableProblems) = TypedTableExpansion.Apply(qualifiedTrees as IReadOnlyList<SqlSyntaxTree> ?? qualifiedTrees.ToArray());
        // A table written with LIKE is given what it copies before an ALTER TABLE can name a copied column (G-221); a source the project does not declare
        // is read from likeSourceTrees, which diff and publish read from the database.
        var (likeTrees, externalLikeSources) = TableLikeExpander.Apply(expandedTrees, likeSourceTrees);
        model.ExternalLikeSources = externalLikeSources;
        var trees = ConstraintNameAssigner.Assign(AlterPublicationApplier.Apply(AlterDomainCheckApplier.Apply(AlterTableColumnApplier.Apply(likeTrees))));

        _declarationPass.Execute(model, trees, excludeGrants, excludeRoleMemberships, excludeOwners);
        if (typedTableProblems.Count > 0)
        {
            model.Diagnostics = [.. model.Diagnostics, .. typedTableProblems.Select(static problem => SemanticDiagnostics.Error(problem.Message, problem.Location, nameof(TypedTableExpansion)))];
        }

        model = _columnPass.Execute(model, trees);
        model = _partitionColumnInheritancePass.Execute(model);
        model = _tableInheritancePass.Execute(model);
        model = _inheritedColumnDefaultPass.Execute(model);
        model = _constraintPass.Execute(model, trees);
        model = _dependencyPass.Execute(model, trees);
        model = _validationPass.Execute(model);
        model = _expressionMappingPass.Execute(model);
        model = _deepDependencyPass.Execute(model);
        model = _deepColumnDefaultPass.Execute(model, trees);
        model = _deepIndexExpressionPass.Execute(model, trees);
        model = _deepTypeStructurePass.Execute(model, trees);
        model = _deepConstraintMappingPass.Execute(model, trees);
        model = _queryBodyBindingPass.Execute(model, trees);
        model = _typeResolutionPass.Execute(model);
        model = _semanticBinder.Execute(model);
        model = _expressionNormalizationPass.Execute(model);
        model = _expressionSemanticBindingPass.Execute(model);
        model = _viewSemanticPass.Execute(model);
        model = _searchPathValidationPass.Execute(model, trees);
        model = _plpgsqlBodyReferencePass.Execute(model, trees);
        model = _procedureIdentityPass.Execute(model);
        _semanticDiffNormalizationPass.Execute(model);
        _functionSignatureValidationPass.Execute(model);
        _semanticCyclePass.Execute(model);
        _semanticGraphPass.Execute(model);
        _semanticCycleExplanationPass.Execute(model);
        _errorRecoveryPass.Execute(model);

        return model;
    }
}
