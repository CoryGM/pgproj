namespace PgProj.Parser;

public sealed record SqlSyntaxTree(
    string Sql,
    string? RawJsonAst,
    byte[] RawProtobufAst,
    IReadOnlyList<SqlNode> Nodes,
    IReadOnlyList<SqlDiagnostic> Diagnostics)
{
    /// <summary>The file <see cref="Sql"/> was read from, when it was read from one (G-123): diagnostics carry it so they name the file.</summary>
    public string? FilePath { get; init; }

    /// <summary>
    /// The search path the file's unqualified names resolve along when it is not the project's (G-290): a tenant template file read for one schema
    /// has that schema first and then the project's <c>searchPath</c>. Its first schema is the one the file was parsed with as the default. Null for an
    /// ordinary file.
    /// </summary>
    public IReadOnlyList<string>? SearchPath { get; init; }

    public bool HasErrors => Diagnostics.Any(static diagnostic => diagnostic.Severity == SqlDiagnosticSeverity.Error);

    public IEnumerable<TableNode> Tables => Nodes.OfType<TableNode>();

    public IEnumerable<ColumnNode> Columns => Nodes.OfType<ColumnNode>();

    public IEnumerable<ViewNode> Views => Nodes.OfType<ViewNode>();

    public IEnumerable<FunctionNode> Functions => Nodes.OfType<FunctionNode>();

    public IEnumerable<ProcedureNode> Procedures => Nodes.OfType<ProcedureNode>();

    public IEnumerable<TriggerNode> Triggers => Nodes.OfType<TriggerNode>();

    public IEnumerable<IndexNode> Indexes => Nodes.OfType<IndexNode>();

    public IEnumerable<ConstraintNode> Constraints => Nodes.OfType<ConstraintNode>();

    public IEnumerable<SequenceNode> Sequences => Nodes.OfType<SequenceNode>();

    public IEnumerable<ObjectCommentNode> ObjectComments => Nodes.OfType<ObjectCommentNode>();

    /// <summary>The schemas the file declares with <c>CREATE SCHEMA</c> (G-284).</summary>
    public IEnumerable<SchemaNode> Schemas => Nodes.OfType<SchemaNode>();

    public IEnumerable<GrantNode> Grants => Nodes.OfType<GrantNode>();

    /// <summary>The owners the file states for objects (G-146).</summary>
    public IEnumerable<OwnerNode> Owners => Nodes.OfType<OwnerNode>();

    public IEnumerable<PolicyNode> Policies => Nodes.OfType<PolicyNode>();

    public IEnumerable<EventTriggerNode> EventTriggers => Nodes.OfType<EventTriggerNode>();

    public IEnumerable<CastNode> Casts => Nodes.OfType<CastNode>();

    public IEnumerable<OperatorNode> Operators => Nodes.OfType<OperatorNode>();

    public IEnumerable<RuleNode> Rules => Nodes.OfType<RuleNode>();

    public IEnumerable<AggregateNode> Aggregates => Nodes.OfType<AggregateNode>();

    public IEnumerable<CollationNode> Collations => Nodes.OfType<CollationNode>();

    public IEnumerable<StatisticsNode> Statistics => Nodes.OfType<StatisticsNode>();

    public IEnumerable<TextSearchDictionaryNode> TextSearchDictionaries => Nodes.OfType<TextSearchDictionaryNode>();

    public IEnumerable<TextSearchConfigurationNode> TextSearchConfigurations => Nodes.OfType<TextSearchConfigurationNode>();

    public IEnumerable<PublicationNode> Publications => Nodes.OfType<PublicationNode>();

    public IEnumerable<AlterEventTriggerNode> EventTriggerStates => Nodes.OfType<AlterEventTriggerNode>();

    public IEnumerable<TypeNode> Types => Nodes.OfType<TypeNode>();

    public IEnumerable<DomainNode> Domains => Nodes.OfType<DomainNode>();

    public IEnumerable<ExtensionNode> Extensions => Nodes.OfType<ExtensionNode>();
}
