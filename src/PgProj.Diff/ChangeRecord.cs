using PgProj.Semantics;

namespace PgProj.Diff;

public enum ChangeNodeKind
{
    Unknown,
    Table,
    View,
    Function,
    Type,
    Index,
    Constraint,
    Grant,
    Sequence,
    Procedure,
    Trigger,
    Schema,
    Policy,
    EventTrigger,
    Cast,
    Operator,
    Rule,
    Aggregate,
    Publication,
    Owner,
    Collation,
    Statistics,
    TextSearchDictionary,
    TextSearchConfiguration,
    SchemaComment,
}

public abstract record class ChangeRecord
{
    public required string NodeId { get; init; }

    public required ChangeNodeKind NodeKind { get; init; }

    public static ChangeNodeKind MapKind(ISemanticNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        return node switch
        {
            TableDefinition => ChangeNodeKind.Table,
            ViewDefinition => ChangeNodeKind.View,
            FunctionDefinition => ChangeNodeKind.Function,
            TypeDefinition => ChangeNodeKind.Type,
            IndexDefinition => ChangeNodeKind.Index,
            ConstraintDefinition => ChangeNodeKind.Constraint,
            GrantDefinition => ChangeNodeKind.Grant,
            SequenceDefinition => ChangeNodeKind.Sequence,
            ProcedureDefinition => ChangeNodeKind.Procedure,
            TriggerDefinition => ChangeNodeKind.Trigger,
            PolicyDefinition => ChangeNodeKind.Policy,
            EventTriggerDefinition => ChangeNodeKind.EventTrigger,
            CastDefinition => ChangeNodeKind.Cast,
            OperatorDefinition => ChangeNodeKind.Operator,
            RuleDefinition => ChangeNodeKind.Rule,
            AggregateDefinition => ChangeNodeKind.Aggregate,
            PublicationDefinition => ChangeNodeKind.Publication,
            OwnerDefinition => ChangeNodeKind.Owner,
            SchemaDefinition => ChangeNodeKind.SchemaComment,
            CollationDefinition => ChangeNodeKind.Collation,
            StatisticsDefinition => ChangeNodeKind.Statistics,
            TextSearchDictionaryDefinition => ChangeNodeKind.TextSearchDictionary,
            TextSearchConfigurationDefinition => ChangeNodeKind.TextSearchConfiguration,
            _ => ChangeNodeKind.Unknown,
        };
    }
}
