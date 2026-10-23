using PgProj.Parser;

namespace PgProj.Semantics;

internal class SqlDependencyVisitor
{
    public DependencyInfo Info { get; } = new();

    public void Visit(SqlNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        switch (node)
        {
            case TableNode tableNode:
                VisitTable(tableNode);
                break;
            case ColumnNode columnNode:
                VisitColumn(columnNode);
                break;
            case ViewNode viewNode:
                VisitView(viewNode);
                break;
            case FunctionNode functionNode:
                VisitFunction(functionNode);
                break;
            case ProcedureNode procedureNode:
                VisitProcedure(procedureNode);
                break;
            case IndexNode indexNode:
                VisitIndex(indexNode);
                break;
            case ConstraintNode constraintNode:
                VisitConstraint(constraintNode);
                break;
            default:
                VisitUnknown(node);
                break;
        }
    }

    public virtual void VisitTable(TableNode node)
    {
        VisitChildren(node);

        // TODO: Walk table-level constraints, defaults, generated expressions, and storage expressions when richer parser nodes are available.
    }

    public virtual void VisitColumn(ColumnNode node)
    {
        Info.AddTable(node.TableName);
        Info.AddType(node.DataType);

        if (!string.IsNullOrWhiteSpace(node.Name))
        {
            var qualifiedColumnName = string.IsNullOrWhiteSpace(node.TableName)
                ? node.Name.Trim()
                : $"{node.TableName.Trim()}.{node.Name.Trim()}";

            Info.AddColumn(qualifiedColumnName);
        }

        // TODO: Extract dependencies from column default expressions, generated expressions, and collations when the parser exposes them.
    }

    public virtual void VisitView(ViewNode node)
    {
        _ = node;

        // TODO: Walk parsed SELECT targets, FROM clauses, JOIN conditions, WHERE predicates, and function call expressions for view dependencies.
        VisitChildren(node);
    }

    public virtual void VisitFunction(FunctionNode node)
    {
        AddSignatureTypes(node.Signature);

        // TODO: Walk parsed function bodies and nested SQL expressions for deeper function dependencies.
        VisitChildren(node);
    }

    public virtual void VisitProcedure(ProcedureNode node)
    {
        AddSignatureTypes(node.Signature);
    }

    /// <summary>
    /// A routine cannot be created before the types in its parameter list and result exist, so each is a
    /// dependency (G-50). An array type depends on its element type.
    /// </summary>
    private void AddSignatureTypes(RoutineSignature? signature)
    {
        if (signature is null)
        {
            return;
        }

        foreach (var typeName in signature.Parameters.Select(static parameter => parameter.DataType).Append(signature.ReturnType))
        {
            if (string.IsNullOrWhiteSpace(typeName))
            {
                continue;
            }

            // record is a pseudo-type, not something to create first; a live function with OUT parameters is read back as RETURNS record where the project states
            // no result (G-187), so it is not a dependency. RETURNS TABLE with more than one column is parsed as pg_catalog.record (G-193).
            if (ReferenceEquals(typeName, signature.ReturnType) && IsRecordPseudoType(typeName))
            {
                continue;
            }

            var element = typeName.Trim();
            while (element.EndsWith("[]", StringComparison.Ordinal))
            {
                element = element[..^2].TrimEnd();
            }

            Info.AddType(element);
        }
    }

    private static bool IsRecordPseudoType(string typeName)
    {
        var name = typeName.Trim();
        return string.Equals(name, "record", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "pg_catalog.record", StringComparison.OrdinalIgnoreCase);
    }

    public virtual void VisitIndex(IndexNode node)
    {
        Info.AddTable(node.TableName);

        // TODO: Walk index expressions, predicates, includes, operator classes, and function call expressions when surfaced by the parser.
        VisitChildren(node);
    }

    public virtual void VisitConstraint(ConstraintNode node)
    {
        Info.AddTable(node.TableName);

        // TODO: Walk foreign key references, check expressions, exclusion expressions, and constraint predicates when surfaced by the parser.
        VisitChildren(node);
    }

    protected virtual void VisitUnknown(SqlNode node)
    {
        VisitChildren(node);

        // TODO: Add typed Visit overloads for deeper SQL expression nodes as PgProj.Parser exposes them.
    }

    protected virtual void VisitChildren(SqlNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        switch (node)
        {
            case TableNode tableNode:
                foreach (var column in tableNode.Columns)
                {
                    Visit(column);
                }
                break;
            case ViewNode:
                // TODO: Visit parsed SELECT child nodes when the parser exposes a view body AST.
                break;
            case FunctionNode:
                // TODO: Visit parsed function-body child nodes when the parser exposes them.
                break;
            case IndexNode:
                // TODO: Visit parsed index expression child nodes when the parser exposes them.
                break;
            case ConstraintNode:
                // TODO: Visit parsed constraint expression child nodes when the parser exposes them.
                break;
            case ColumnNode:
                // TODO: Visit parsed column expression child nodes when the parser exposes them.
                break;
        }
    }
}

internal sealed class DependencyInfo
{
    private readonly HashSet<string> _referencedTables = new(PgProj.Parser.SqlIdentifiers.NameComparer);
    private readonly HashSet<string> _referencedColumns = new(PgProj.Parser.SqlIdentifiers.NameComparer);
    private readonly HashSet<string> _referencedTypes = new(PgProj.Parser.SqlIdentifiers.NameComparer);
    private readonly HashSet<string> _referencedFunctions = new(PgProj.Parser.SqlIdentifiers.NameComparer);

    public IReadOnlyList<string> ReferencedTables => _referencedTables.ToArray();
    public IReadOnlyList<string> ReferencedColumns => _referencedColumns.ToArray();
    public IReadOnlyList<string> ReferencedTypes => _referencedTypes.ToArray();
    public IReadOnlyList<string> ReferencedFunctions => _referencedFunctions.ToArray();

    public void AddTable(string? reference) => Add(_referencedTables, reference);
    public void AddColumn(string? reference) => Add(_referencedColumns, reference);
    public void AddType(string? reference) => Add(_referencedTypes, reference);
    public void AddFunction(string? reference) => Add(_referencedFunctions, reference);

    public void AddTables(IEnumerable<string> references) => AddRange(_referencedTables, references);
    public void AddColumns(IEnumerable<string> references) => AddRange(_referencedColumns, references);
    public void AddTypes(IEnumerable<string> references) => AddRange(_referencedTypes, references);
    public void AddFunctions(IEnumerable<string> references) => AddRange(_referencedFunctions, references);

    private static void AddRange(ISet<string> target, IEnumerable<string> references)
    {
        foreach (var reference in references)
        {
            Add(target, reference);
        }
    }

    private static void Add(ISet<string> target, string? reference)
    {
        if (!string.IsNullOrWhiteSpace(reference))
        {
            target.Add(reference.Trim());
        }
    }
}
