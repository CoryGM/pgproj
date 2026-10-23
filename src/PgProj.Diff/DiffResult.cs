using PgProj.Semantics;

namespace PgProj.Diff;

public sealed record class DiffResult
{
    public required IReadOnlyList<TableDefinition> AddedTables { get; init; }

    public required IReadOnlyList<TableDefinition> RemovedTables { get; init; }

    public required IReadOnlyList<SemanticObjectModification<TableDefinition>> ModifiedTables { get; init; }

    public required IReadOnlyList<ColumnDefinition> AddedColumns { get; init; }

    public required IReadOnlyList<ColumnDefinition> RemovedColumns { get; init; }

    public required IReadOnlyList<SemanticObjectModification<ColumnDefinition>> ModifiedColumns { get; init; }

    public required IReadOnlyList<ConstraintDefinition> AddedConstraints { get; init; }

    public required IReadOnlyList<ConstraintDefinition> RemovedConstraints { get; init; }

    public required IReadOnlyList<SemanticObjectModification<ConstraintDefinition>> ModifiedConstraints { get; init; }

    public required IReadOnlyList<IndexDefinition> AddedIndexes { get; init; }

    public required IReadOnlyList<IndexDefinition> RemovedIndexes { get; init; }

    public required IReadOnlyList<SemanticObjectModification<IndexDefinition>> ModifiedIndexes { get; init; }

    public required IReadOnlyList<FunctionDefinition> AddedFunctions { get; init; }

    public required IReadOnlyList<FunctionDefinition> RemovedFunctions { get; init; }

    public required IReadOnlyList<SemanticObjectModification<FunctionDefinition>> ModifiedFunctions { get; init; }

    public required IReadOnlyList<ViewDefinition> AddedViews { get; init; }

    public required IReadOnlyList<ViewDefinition> RemovedViews { get; init; }

    public required IReadOnlyList<SemanticObjectModification<ViewDefinition>> ModifiedViews { get; init; }

    public required IReadOnlyList<TypeDefinition> AddedTypes { get; init; }

    public required IReadOnlyList<TypeDefinition> RemovedTypes { get; init; }

    public required IReadOnlyList<SemanticObjectModification<TypeDefinition>> ModifiedTypes { get; init; }

    public required IReadOnlyList<SequenceDefinition> AddedSequences { get; init; }

    public required IReadOnlyList<SequenceDefinition> RemovedSequences { get; init; }

    public required IReadOnlyList<SemanticObjectModification<SequenceDefinition>> ModifiedSequences { get; init; }

    public IReadOnlyList<ProcedureDefinition> AddedProcedures { get; init; } = [];

    public IReadOnlyList<ProcedureDefinition> RemovedProcedures { get; init; } = [];

    public IReadOnlyList<SemanticObjectModification<ProcedureDefinition>> ModifiedProcedures { get; init; } = [];

    public IReadOnlyList<TriggerDefinition> AddedTriggers { get; init; } = [];

    public IReadOnlyList<TriggerDefinition> RemovedTriggers { get; init; } = [];

    public IReadOnlyList<SemanticObjectModification<TriggerDefinition>> ModifiedTriggers { get; init; } = [];

    public IReadOnlyList<PolicyDefinition> AddedPolicies { get; init; } = [];

    public IReadOnlyList<PolicyDefinition> RemovedPolicies { get; init; } = [];

    public IReadOnlyList<SemanticObjectModification<PolicyDefinition>> ModifiedPolicies { get; init; } = [];

    public IReadOnlyList<EventTriggerDefinition> AddedEventTriggers { get; init; } = [];

    public IReadOnlyList<EventTriggerDefinition> RemovedEventTriggers { get; init; } = [];

    public IReadOnlyList<SemanticObjectModification<EventTriggerDefinition>> ModifiedEventTriggers { get; init; } = [];

    public IReadOnlyList<CastDefinition> AddedCasts { get; init; } = [];

    public IReadOnlyList<CastDefinition> RemovedCasts { get; init; } = [];

    public IReadOnlyList<SemanticObjectModification<CastDefinition>> ModifiedCasts { get; init; } = [];

    public IReadOnlyList<OperatorDefinition> AddedOperators { get; init; } = [];

    public IReadOnlyList<OperatorDefinition> RemovedOperators { get; init; } = [];

    public IReadOnlyList<SemanticObjectModification<OperatorDefinition>> ModifiedOperators { get; init; } = [];

    public IReadOnlyList<RuleDefinition> AddedRules { get; init; } = [];

    public IReadOnlyList<RuleDefinition> RemovedRules { get; init; } = [];

    public IReadOnlyList<SemanticObjectModification<RuleDefinition>> ModifiedRules { get; init; } = [];

    public IReadOnlyList<AggregateDefinition> AddedAggregates { get; init; } = [];

    public IReadOnlyList<AggregateDefinition> RemovedAggregates { get; init; } = [];

    public IReadOnlyList<SemanticObjectModification<AggregateDefinition>> ModifiedAggregates { get; init; } = [];

    public IReadOnlyList<CollationDefinition> AddedCollations { get; init; } = [];

    public IReadOnlyList<CollationDefinition> RemovedCollations { get; init; } = [];

    public IReadOnlyList<SemanticObjectModification<CollationDefinition>> ModifiedCollations { get; init; } = [];

    public IReadOnlyList<StatisticsDefinition> AddedStatistics { get; init; } = [];

    public IReadOnlyList<StatisticsDefinition> RemovedStatistics { get; init; } = [];

    public IReadOnlyList<SemanticObjectModification<StatisticsDefinition>> ModifiedStatistics { get; init; } = [];

    public IReadOnlyList<TextSearchDictionaryDefinition> AddedTextSearchDictionaries { get; init; } = [];

    public IReadOnlyList<TextSearchDictionaryDefinition> RemovedTextSearchDictionaries { get; init; } = [];

    public IReadOnlyList<SemanticObjectModification<TextSearchDictionaryDefinition>> ModifiedTextSearchDictionaries { get; init; } = [];

    public IReadOnlyList<TextSearchConfigurationDefinition> AddedTextSearchConfigurations { get; init; } = [];

    public IReadOnlyList<TextSearchConfigurationDefinition> RemovedTextSearchConfigurations { get; init; } = [];

    public IReadOnlyList<SemanticObjectModification<TextSearchConfigurationDefinition>> ModifiedTextSearchConfigurations { get; init; } = [];

    public IReadOnlyList<PublicationDefinition> AddedPublications { get; init; } = [];

    public IReadOnlyList<PublicationDefinition> RemovedPublications { get; init; } = [];

    public IReadOnlyList<SemanticObjectModification<PublicationDefinition>> ModifiedPublications { get; init; } = [];

    /// <summary>Owners the project states for objects that have none in the old model (a new object, or one the old model did not read an owner for) (G-146).</summary>
    public IReadOnlyList<OwnerDefinition> AddedOwners { get; init; } = [];

    /// <summary>Always empty: an owner only the old model has is one the project does not state, which is not compared (G-146).</summary>
    public IReadOnlyList<OwnerDefinition> RemovedOwners { get; init; } = [];

    /// <summary>Objects whose stated owner differs from the old model's (G-146).</summary>
    public IReadOnlyList<SemanticObjectModification<OwnerDefinition>> ModifiedOwners { get; init; } = [];

    /// <summary>Schema comments the project states for schemas that have none in the old model (G-160).</summary>
    public IReadOnlyList<SchemaDefinition> AddedSchemaDefinitions { get; init; } = [];

    /// <summary>Always empty: a schema comment only the old model has is one the project does not state, which is not compared, and pgproj never drops a schema (G-160).</summary>
    public IReadOnlyList<SchemaDefinition> RemovedSchemaDefinitions { get; init; } = [];

    /// <summary>Schemas whose stated comment differs from the old model's (G-160).</summary>
    public IReadOnlyList<SemanticObjectModification<SchemaDefinition>> ModifiedSchemaDefinitions { get; init; } = [];

    public required IReadOnlyList<SemanticDiagnostic> Diagnostics { get; init; }

    public static DiffResult Empty { get; } = new()
    {
        AddedTables = [],
        RemovedTables = [],
        ModifiedTables = [],
        AddedColumns = [],
        RemovedColumns = [],
        ModifiedColumns = [],
        AddedConstraints = [],
        RemovedConstraints = [],
        ModifiedConstraints = [],
        AddedIndexes = [],
        RemovedIndexes = [],
        ModifiedIndexes = [],
        AddedFunctions = [],
        RemovedFunctions = [],
        ModifiedFunctions = [],
        AddedViews = [],
        RemovedViews = [],
        ModifiedViews = [],
        AddedTypes = [],
        RemovedTypes = [],
        ModifiedTypes = [],
        AddedSequences = [],
        RemovedSequences = [],
        ModifiedSequences = [],
        Diagnostics = [],
    };
}

public sealed record class SemanticObjectModification<TNode>
    where TNode : class, ISemanticNode
{
    public required string NodeId { get; init; }

    public required TNode OldNode { get; init; }

    public required TNode NewNode { get; init; }

    public required IReadOnlyList<ModificationDetail> Details { get; init; }
}
