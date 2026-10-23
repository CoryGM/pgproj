using PgProj.Semantics;

namespace PgProj.Dependencies;

public sealed class DependencyAnalyzer
{
    public DependencyGraph Analyze(DatabaseModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        return AnalyzeCore([model]);
    }

    /// <summary>
    /// Builds a single dependency graph covering the union of nodes from both the current
    /// (old/live) and desired (new/source) models. A node present in both models keeps the
    /// dependencies extracted from the new model (its future definition); a node present only
    /// in the old model (i.e. a removed object) keeps the dependencies extracted from the old
    /// model. This lets callers order removed objects dependent-first (via a reversed
    /// <see cref="DependencyGraph.TopologicalSort"/>) alongside added/modified objects, instead
    /// of falling back to an unordered drop pass (G-27).
    /// </summary>
    public DependencyGraph Analyze(DatabaseModel oldModel, DatabaseModel newModel)
    {
        ArgumentNullException.ThrowIfNull(oldModel);
        ArgumentNullException.ThrowIfNull(newModel);

        var graph = AnalyzeCore([newModel, oldModel]);
        graph.TargetNodeIds = newModel.GetAllNodes().Select(static node => node.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        graph.ReplacedUnmodeledPrivileges = oldModel.UnmodeledPrivileges;
        graph.ReplacedDependencies = oldModel.LiveDependencies;
        graph.ReplacedOwners = oldModel.LiveOwners;
        graph.ReplacedConnectedRole = oldModel.ConnectedRole;
        graph.ReplacedComments = oldModel.Constraints.Values
            .Where(static constraint => constraint.Comment is not null)
            .Select(static constraint => (Key: DependencyGraph.CommentKey(constraint), Comment: constraint.Comment!))
            .Concat(oldModel.Triggers.Values
                .Where(static trigger => trigger.Comment is not null)
                .Select(static trigger => (Key: DependencyGraph.CommentKey(trigger), Comment: trigger.Comment!)))
            .Concat(oldModel.Policies.Values
                .Where(static policy => policy.Comment is not null)
                .Select(static policy => (Key: DependencyGraph.CommentKey(policy), Comment: policy.Comment!)))
            .ToDictionary(static entry => entry.Key, static entry => entry.Comment, PgProj.Parser.SqlIdentifiers.NameComparer);
        AddRetiredDependencies(graph, oldModel, newModel);
        return graph;
    }

    /// <summary>
    /// For every object present in both models, records the dependencies its old definition had and its new one does not
    /// (G-52). The merged graph keeps only the new definition's dependencies, so a removed object that a surviving object is
    /// still using in the live database would otherwise look free to drop.
    /// </summary>
    private static void AddRetiredDependencies(DependencyGraph graph, DatabaseModel oldModel, DatabaseModel newModel)
    {
        var newNodesByKey = new Dictionary<string, ISemanticNode>(PgProj.Parser.SqlIdentifiers.NameComparer);
        foreach (var node in newModel.GetAllNodes())
        {
            newNodesByKey.TryAdd(node.Key, node);
        }

        var oldExtensionIds = ExtensionIds(oldModel);
        var newExtensionIds = ExtensionIds(newModel);

        foreach (var oldNode in oldModel.GetAllNodes())
        {
            if (!newNodesByKey.TryGetValue(oldNode.Key, out var newNode))
            {
                continue;
            }

            // Every object depends on every extension (G-46), so an extension the new model no longer has is retired by all.
            var oldDependencies = ExtractDependencies(oldModel, oldNode).Concat(oldExtensionIds.Where(id => !string.Equals(id, oldNode.Key, StringComparison.Ordinal)));
            var newDependencies = ExtractDependencies(newModel, newNode)
                .Concat(newExtensionIds.Where(id => !string.Equals(id, newNode.Key, StringComparison.Ordinal)))
                .ToHashSet(PgProj.Parser.SqlIdentifiers.NameComparer);

            foreach (var dependencyId in oldDependencies.Distinct(PgProj.Parser.SqlIdentifiers.NameComparer))
            {
                if (!newDependencies.Contains(dependencyId)
                    && !string.Equals(dependencyId, oldNode.Key, StringComparison.Ordinal)
                    && graph.Nodes.ContainsKey(oldNode.Key)
                    && graph.Nodes.ContainsKey(dependencyId))
                {
                    graph.AddRetiredDependency(oldNode.Key, dependencyId);
                }
            }
        }
    }

    private static string[] ExtensionIds(DatabaseModel model)
        => model.Types.Values
            .Where(static type => string.Equals(type.TypeKind, "extension", StringComparison.OrdinalIgnoreCase))
            .Select(static type => type.Key)
            .ToArray();

    private static DependencyGraph AnalyzeCore(IReadOnlyList<DatabaseModel> models)
    {
        var graph = new DependencyGraph();
        var referencedNodeIdsByNode = new Dictionary<string, IReadOnlyList<string>>(PgProj.Parser.SqlIdentifiers.NameComparer);

        foreach (var model in models)
        {
            foreach (var semanticNode in model.GetAllNodes())
            {
                if (referencedNodeIdsByNode.ContainsKey(semanticNode.Key))
                {
                    continue;
                }

                var referencedNodeIds = ExtractDependencies(model, semanticNode);
                referencedNodeIdsByNode[semanticNode.Key] = referencedNodeIds;
                graph.AddNode(DependencyNode.FromSemanticNode(semanticNode, referencedNodeIds));
            }
        }

        // Extensions install the types, functions, and operators other objects use, and a function an extension
        // provides is not a node the graph can point at (for example DEFAULT uuid_generate_v4()). So every other
        // object depends on every extension: extensions are created first and dropped last (G-46).
        var extensionIds = models
            .SelectMany(static model => model.Types.Values)
            .Where(static type => string.Equals(type.TypeKind, "extension", StringComparison.OrdinalIgnoreCase))
            .Select(static type => type.Key)
            .Where(referencedNodeIdsByNode.ContainsKey)
            .ToHashSet(PgProj.Parser.SqlIdentifiers.NameComparer);

        // An extension is created after the ones it requires and dropped before them (G-213): in a project those declared before it, in a live
        // database the ones it requires. Each model's view is added, so a create follows the project's order and a drop the database's.
        foreach (var extension in models.SelectMany(static model => model.Types.Values).Where(type => extensionIds.Contains(type.Key)))
        {
            referencedNodeIdsByNode[extension.Key] = referencedNodeIdsByNode[extension.Key]
                .Concat(extension.ExtensionRequires.Where(required => extensionIds.Contains(required) && !string.Equals(required, extension.Key, StringComparison.Ordinal)))
                .Distinct(PgProj.Parser.SqlIdentifiers.NameComparer)
                .ToArray();
        }

        if (extensionIds.Count > 0)
        {
            foreach (var nodeId in referencedNodeIdsByNode.Keys.Where(nodeId => !extensionIds.Contains(nodeId)).ToArray())
            {
                referencedNodeIdsByNode[nodeId] = referencedNodeIdsByNode[nodeId]
                    .Concat(extensionIds)
                    .Distinct(PgProj.Parser.SqlIdentifiers.NameComparer)
                    .ToArray();
            }
        }

        foreach (var (nodeId, referencedNodeIds) in referencedNodeIdsByNode)
        {
            foreach (var referencedNodeId in referencedNodeIds)
            {
                if (graph.Nodes.ContainsKey(referencedNodeId))
                {
                    graph.AddEdge(nodeId, referencedNodeId);
                }
            }
        }

        var cycles = graph.DetectCycles();
        var split = new Dictionary<string, List<string>>(PgProj.Parser.SqlIdentifiers.NameComparer);
        while (cycles.Count > 0 && TryBreakViewCycle(graph, cycles[0], split))
        {
            cycles = graph.DetectCycles();
        }

        if (cycles.Count > 0)
        {
            throw new GraphCycleException(cycles[0]);
        }

        graph.SplitViews = split.ToDictionary(static pair => pair.Key, static pair => (IReadOnlyList<string>)pair.Value, PgProj.Parser.SqlIdentifiers.NameComparer);
        return graph;
    }

    /// <summary>
    /// A cycle of views, which <c>CREATE OR REPLACE VIEW</c> makes possible (G-254), is broken at a view the project replaced: it is created from its first definition, which does not refer to the
    /// others, and replaced by its real body once they exist. The edges from that view to the other views of the cycle are taken out of the graph and kept in <paramref name="split"/>.
    /// </summary>
    private static bool TryBreakViewCycle(DependencyGraph graph, IReadOnlyList<string> cycle, Dictionary<string, List<string>> split)
    {
        var members = cycle.Distinct(PgProj.Parser.SqlIdentifiers.NameComparer).ToArray();
        if (!members.All(member => graph.Nodes.TryGetValue(member, out var node) && node.SemanticNode is ViewDefinition))
        {
            return false;
        }

        var breaker = members
            .Where(member => graph.Nodes[member].SemanticNode is ViewDefinition { InitialDefinition: not null, IsMaterialized: false })
            .OrderBy(static member => member, StringComparer.Ordinal)
            .FirstOrDefault();
        if (breaker is null)
        {
            return false;
        }

        var removed = split.TryGetValue(breaker, out var existing) ? existing : split[breaker] = [];
        var progressed = false;
        foreach (var member in members.Where(member => !string.Equals(member, breaker, StringComparison.Ordinal)))
        {
            if (graph.RemoveEdge(breaker, member))
            {
                removed.Add(member);
                progressed = true;
            }
        }

        return progressed;
    }

    private static IReadOnlyList<string> ExtractDependencies(DatabaseModel model, ISemanticNode semanticNode)
    {
        ArgumentNullException.ThrowIfNull(semanticNode);

        var dependencyIds = new HashSet<string>(PgProj.Parser.SqlIdentifiers.NameComparer);

        switch (semanticNode)
        {
            case TableDefinition table:
                AddTypeDependencies(model, table.SchemaName, table.ReferencedTypes, dependencyIds);

                foreach (var column in table.Columns)
                {
                    if (!string.IsNullOrWhiteSpace(column.DataType))
                    {
                        AddTypeDependency(model, table.SchemaName, column.DataType, dependencyIds);
                    }
                }

                AddOwnedSequenceDependencies(model, table, dependencyIds);
                AddDefaultSequenceDependencies(model, table, dependencyIds);

                // Column DEFAULT/GENERATED expressions may reference other tables (e.g. via a
                // function call that queries another table) or functions; DeepDependencyPass
                // already populates these on the table, so consume them here (G-08).
                AddTableDependencies(model, table.SchemaName, table.ReferencedTables, dependencyIds);
                AddFunctionDependencies(model, table.SchemaName, table.ReferencedFunctions, dependencyIds);
                AddOperatorDependencies(model, table.Columns.SelectMany(static column => column.ReferencedOperators), dependencyIds);
                AddCollationDependencies(model, table.SchemaName, table.Columns.Select(static column => column.CollationName), dependencyIds);
                AddLikeDependencies(model, table, dependencyIds);

                // A partition key expression is resolved when the table is created, so the functions it calls come first (G-248).
                foreach (var element in table.PartitionKeyElements)
                {
                    AddCalledFunctionDependencies(model, table.SchemaName, element.RawExpression, dependencyIds);
                }

                break;

            case ViewDefinition view:
                AddTableDependencies(model, view.SchemaName, view.ReferencedTables, dependencyIds);
                // The types the body names (a cast, a JSON_TABLE column) must exist when the view is created (G-231).
                AddTypeDependencies(model, view.SchemaName, view.ReferencedTypes, dependencyIds);
                AddPrimaryKeyDependencies(model, view, dependencyIds);
                AddFunctionDependencies(model, view.SchemaName, view.ReferencedFunctions, dependencyIds);
                AddOperatorDependencies(model, view.ReferencedOperators, dependencyIds);
                // TODO: Add deeper view dependency extraction from parsed query expressions.
                break;

            case FunctionDefinition function:
                AddTypeDependencies(model, function.SchemaName, function.ReferencedTypes, dependencyIds);
                AddFunctionDependencies(model, function.SchemaName, function.ReferencedFunctions, dependencyIds);
                AddOperatorDependencies(model, function.ReferencedOperators, dependencyIds);

                // A SQL-language body's tables, calls, and casts are read from its tree by QueryBodyBindingPass (G-90);
                // consume them here (G-08).
                AddTableDependencies(model, function.SchemaName, function.ReferencedTables, dependencyIds);
                AddBodySequenceDependencies(model, function.SchemaName, function.Language, function.RawBody, dependencyIds);
                AddDeclaredVariableDependencies(model, function.SchemaName, function.Language, function.RawBody, dependencyIds);

                // TODO: Add deeper function dependency extraction for argument types.
                break;

            case ProcedureDefinition procedure:
                AddBodySequenceDependencies(model, procedure.SchemaName, procedure.Language, procedure.RawBody, dependencyIds);
                AddDeclaredVariableDependencies(model, procedure.SchemaName, procedure.Language, procedure.RawBody, dependencyIds);
                AddCalledProcedureDependencies(model, procedure, dependencyIds);
                break;

            case IndexDefinition index:
                AddTableDependency(model, index.SchemaName, index.TableName, dependencyIds);

                // An index on a materialized view is created after the view (G-171).
                if (model.FindMaterializedView(index.SchemaName, index.TableName) is { } indexedView)
                {
                    dependencyIds.Add(indexedView.Key);
                }

                AddTableDependencies(model, index.SchemaName, index.ReferencedTables, dependencyIds);
                AddPartitionIndexDependencies(model, index, dependencyIds);

                // Partial-index predicates may call functions; DeepDependencyPass already
                // populates this on the index, so consume it here (G-08).
                AddFunctionDependencies(model, index.SchemaName, index.ReferencedFunctions, dependencyIds);
                AddOperatorDependencies(model, index.ReferencedOperators, dependencyIds);
                AddCollationDependencies(model, index.SchemaName, index.KeyElements.Select(static element => element.CollationName), dependencyIds);

                // TODO: Add deeper index dependency extraction for expression-index key trees and operator classes.
                break;

            case ConstraintDefinition constraint:
                AddTableDependency(model, constraint.SchemaName, constraint.TableName, dependencyIds);
                AddTableDependencies(model, constraint.SchemaName, constraint.ReferencedTables, dependencyIds);

                // CHECK expressions may call functions; DeepDependencyPass already populates
                // this on the constraint, so consume it here (G-08). Foreign key targets are
                // already covered by ReferencedTables above.
                AddFunctionDependencies(model, constraint.SchemaName, constraint.ReferencedFunctions, dependencyIds);
                AddOperatorDependencies(model, constraint.ReferencedOperators, dependencyIds);

                // A foreign key also needs the PRIMARY KEY/UNIQUE constraint (or unique index) it
                // references to exist first: constraints deploy as separate ALTER TABLE statements,
                // and PostgreSQL rejects the foreign key otherwise (G-41).
                AddReferencedKeyDependencies(model, constraint, dependencyIds);
                AddInheritedMergeDependencies(model, constraint, dependencyIds);
                AddForeignKeyNumberOrderDependencies(model, constraint, dependencyIds);
                break;

            case TypeDefinition type:
                AddTypeDependencies(model, type.SchemaName, type.ReferencedTypes, dependencyIds);
                AddFunctionDependencies(model, type.SchemaName, type.ReferencedFunctions, dependencyIds);
                AddOperatorDependencies(model, type.ReferencedOperators, dependencyIds);
                AddCollationDependencies(model, type.SchemaName, type.CompositeFields.Select(static field => field.CollationName).Append(type.RangeOptions?.CollationName).Append(type.DomainCollationName), dependencyIds);

                // A domain's default is evaluated when a value is inserted, but PostgreSQL resolves what it names when the domain is created (G-231).
                AddSequencesNamedIn(model, type.SchemaName, type.DomainRawDefaultExpression, dependencyIds);
                AddCalledFunctionDependencies(model, type.SchemaName, type.DomainRawDefaultExpression, dependencyIds);
                break;

            case GrantDefinition grant:
                AddGrantedObjectDependencies(model, grant, dependencyIds);
                break;

            case TextSearchConfigurationDefinition configuration:
                // A configuration is created after the dictionaries its mappings name and the configuration it copies (G-149); a built-in one is not the project's.
                foreach (var name in configuration.NormalizedMappings.Values.SelectMany(static names => names).Append(configuration.NormalizedCopyFrom ?? string.Empty))
                {
                    // The mappings name them as schema.name; their keys carry their kind (G-228).
                    if (model.TextSearchDictionaries.TryGetValue("tsdictionary:" + name, out var dictionary))
                    {
                        dependencyIds.Add(dictionary.Key);
                    }
                    else if (model.TextSearchConfigurations.TryGetValue("tsconfiguration:" + name, out var copied))
                    {
                        dependencyIds.Add(copied.Key);
                    }
                }

                break;

            case OwnerDefinition owner:
                // The owner is set after the object exists (G-146). A schema is not a node of the graph, so its edge is simply not added.
                dependencyIds.Add(owner.ObjectKey);
                break;

            default:
                AddTableDependencies(model, semanticNode.SchemaName, semanticNode.ReferencedTables, dependencyIds);
                AddTypeDependencies(model, semanticNode.SchemaName, semanticNode.ReferencedTypes, dependencyIds);
                AddFunctionDependencies(model, semanticNode.SchemaName, semanticNode.ReferencedFunctions, dependencyIds);
                AddOperatorDependencies(model, semanticNode.ReferencedOperators, dependencyIds);
                break;
        }

        AddTextSearchConfigurationDependencies(model, semanticNode, dependencyIds);
        AddExpressionSequenceDependencies(model, semanticNode, dependencyIds);

        // A view, or a SQL-language routine body, that reads another view needs that view first; PostgreSQL resolves both at CREATE (G-90, G-91).
        var referencedViews = semanticNode switch
        {
            ViewDefinition view => view.ReferencedViews,
            FunctionDefinition function => function.ReferencedViews,
            ProcedureDefinition procedure => procedure.ReferencedViews,
            RuleDefinition rule => rule.ReferencedViews,
            _ => [],
        };
        foreach (var viewKey in referencedViews)
        {
            if (model.TryGetView(viewKey, out var view) && view is not null)
            {
                dependencyIds.Add(view.Key);
            }
        }

        dependencyIds.Remove(semanticNode.Key);
        return dependencyIds.ToArray();
    }

    /// <summary>
    /// <c>GRANT ... ON ALL TABLES|SEQUENCES|FUNCTIONS IN SCHEMA s</c> applies to the objects that exist when it runs, so it depends
    /// on every object of that kind in the schema: it is emitted after they are created, and revoked before they are dropped
    /// (G-61). Without the edges its position depended on the order the model enumerated objects in, and a sequence created in
    /// the same publish came after the grant and was never granted anything. Other grants (on the schema itself, or default
    /// privileges, which apply to objects created later) name no existing object and get no edges.
    /// </summary>
    private static void AddGrantedObjectDependencies(DatabaseModel model, GrantDefinition grant, ISet<string> dependencyIds)
    {
        AddGrantedObjectDependenciesCore(model, grant, dependencyIds);

        // A grant on an object comes after the object's owner is set: changing an owner moves the old owner's privileges to the new one, so a grant issued
        // before it could be moved with them (G-146).
        if (dependencyIds.Count > 0)
        {
            foreach (var owner in model.Owners.Values.Where(owner => dependencyIds.Contains(owner.ObjectKey)).ToArray())
            {
                dependencyIds.Add(owner.Key);
            }
        }
    }

    private static void AddGrantedObjectDependenciesCore(DatabaseModel model, GrantDefinition grant, ISet<string> dependencyIds)
    {
        var objectKey = grant.ObjectKey ?? string.Empty;
        bool InSchema(string? schemaName) => string.Equals(schemaName, grant.SchemaName, StringComparison.Ordinal);

        if (objectKey.StartsWith("all tables in schema:", StringComparison.OrdinalIgnoreCase))
        {
            // ALL TABLES covers tables, views, and materialized views alike.
            foreach (var table in model.Tables.Values.Where(table => InSchema(table.SchemaName)))
            {
                dependencyIds.Add(table.Key);
            }

            foreach (var view in model.Views.Values.Where(view => InSchema(view.SchemaName)))
            {
                dependencyIds.Add(view.Key);
            }
        }
        else if (objectKey.StartsWith("all sequences in schema:", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var sequence in model.Sequences.Values.Where(sequence => InSchema(sequence.SchemaName)))
            {
                dependencyIds.Add(sequence.Key);
            }
        }
        else if (objectKey.StartsWith("all functions in schema:", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var function in model.Functions.Values.Where(function => InSchema(function.SchemaName)))
            {
                dependencyIds.Add(function.Key);
            }
        }
        else if (grant.ObjectName is { } objectName)
        {
            // A grant on one named object (or on columns of one table) comes after that object (G-65).
            var schemaName = grant.SchemaName ?? "public";
            bool Named(string? candidateSchema, string candidateName)
                => string.Equals(candidateSchema ?? "public", schemaName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(candidateName, objectName, StringComparison.Ordinal);

            if (objectKey.StartsWith("table:", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var table in model.Tables.Values.Where(table => Named(table.SchemaName, table.Name)))
                {
                    dependencyIds.Add(table.Key);
                }

                foreach (var view in model.Views.Values.Where(view => Named(view.SchemaName, view.Name)))
                {
                    dependencyIds.Add(view.Key);
                }
            }
            else if (objectKey.StartsWith("sequence:", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var sequence in model.Sequences.Values.Where(sequence => Named(sequence.SchemaName, sequence.Name)))
                {
                    dependencyIds.Add(sequence.Key);
                }
            }
            else if (objectKey.StartsWith("function:", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var function in model.Functions.Values.Where(function => Named(function.SchemaName, GrantExpansion.RoutineObjectName(function))))
                {
                    dependencyIds.Add(function.Key);
                }
            }
            else if (objectKey.StartsWith("procedure:", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var procedure in model.Procedures.Values.Where(procedure => Named(procedure.SchemaName, GrantExpansion.RoutineObjectName(procedure))))
                {
                    dependencyIds.Add(procedure.Key);
                }
            }
        }
    }

    /// <summary>
    /// Adds edges from a foreign key to the key on the referenced table that PostgreSQL uses to enforce
    /// it: a PRIMARY KEY or UNIQUE constraint, or a plain unique index, over exactly the referenced columns
    /// (in any order). A foreign key with no column list targets the referenced table's primary key.
    /// </summary>
    /// <summary>
    /// A <c>NOT VALID</c> check on a parent is inherited by its children as <c>NOT VALID</c>, and PostgreSQL refuses a child's own valid constraint of the same name
    /// after that (<c>42P17</c>, "conflicts with NOT VALID constraint"). Added the other way round the two merge: the child keeps its valid constraint and the parent's is
    /// <c>NOT VALID</c>. So the parent's constraint is created after the same-named constraints of its descendants (G-234).
    /// </summary>
    private static void AddInheritedMergeDependencies(DatabaseModel model, ConstraintDefinition constraint, ISet<string> dependencyIds)
    {
        if (!TryResolveTableKey(model, constraint.SchemaName, constraint.TableName, out var tableKey))
        {
            return;
        }

        var descendants = new HashSet<string>(PgProj.Parser.SqlIdentifiers.NameComparer);
        var pending = new Queue<string>();
        pending.Enqueue(tableKey);
        while (pending.Count > 0)
        {
            var parent = pending.Dequeue();
            foreach (var table in model.Tables.Values)
            {
                var isChild = table.InheritsFrom.Contains(parent, PgProj.Parser.SqlIdentifiers.NameComparer)
                    || (table.PartitionOfTableName is not null && TryResolveTableKey(model, table.PartitionOfSchemaName ?? table.SchemaName, table.PartitionOfTableName, out var partitionParent)
                        && string.Equals(partitionParent, parent, StringComparison.Ordinal));
                if (isChild && descendants.Add(table.Key))
                {
                    pending.Enqueue(table.Key);
                }
            }
        }

        foreach (var other in model.Constraints.Values)
        {
            // A primary or unique key, or an exclusion constraint, a partition has under another name is adopted by the parent's equivalent one, so it must exist first (G-237, G-296).
            var adoptable = string.Equals(other.ConstraintType, constraint.ConstraintType, StringComparison.Ordinal)
                && constraint.ConstraintType is { } keyType && (keyType.Contains("PRIMARY", StringComparison.OrdinalIgnoreCase) || keyType.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) || keyType.Contains("EXCLU", StringComparison.OrdinalIgnoreCase));
            if ((!adoptable && !string.Equals(other.Name, constraint.Name, StringComparison.Ordinal))
                || string.Equals(other.Key, constraint.Key, StringComparison.Ordinal)
                || !TryResolveTableKey(model, other.SchemaName, other.TableName, out var otherTableKey)
                || !descendants.Contains(otherTableKey))
            {
                continue;
            }

            dependencyIds.Add(other.Key);
        }
    }

    /// <summary>
    /// An index on a partitioned table is made on every partition too, under the first free name PostgreSQL finds there. A partition's own index with such a name is created first,
    /// so the clone takes another and the two do not collide (G-250): the parent's index is created after the indexes of its partitions.
    /// </summary>
    private static void AddPartitionIndexDependencies(DatabaseModel model, IndexDefinition index, ISet<string> dependencyIds)
    {
        if (!TryResolveTableKey(model, index.SchemaName, index.TableName, out var tableKey)
            || !model.TryGetTable(tableKey, out var table) || table is null || table.PartitionStrategy is null)
        {
            return;
        }

        var descendants = new HashSet<string>(PgProj.Parser.SqlIdentifiers.NameComparer);
        var pending = new Queue<string>();
        pending.Enqueue(tableKey);
        while (pending.Count > 0)
        {
            var parent = pending.Dequeue();
            foreach (var candidate in model.Tables.Values)
            {
                if (candidate.PartitionOfTableName is not null
                    && TryResolveTableKey(model, candidate.PartitionOfSchemaName ?? candidate.SchemaName, candidate.PartitionOfTableName, out var parentKey)
                    && string.Equals(parentKey, parent, StringComparison.Ordinal)
                    && descendants.Add(candidate.Key))
                {
                    pending.Enqueue(candidate.Key);
                }
            }
        }

        foreach (var other in model.Indexes.Values)
        {
            if (!string.Equals(other.Key, index.Key, StringComparison.Ordinal)
                && TryResolveTableKey(model, other.SchemaName, other.TableName, out var otherTableKey)
                && descendants.Contains(otherTableKey))
            {
                dependencyIds.Add(other.Key);
            }
        }
    }

    /// <summary>
    /// A foreign key to a partitioned table also makes one constraint per partition of the referenced table on PostgreSQL 16 and 17, named after the table and column with the first free number
    /// (<c>fk_a_fkey1</c>, <c>fk_a_fkey2</c>, ...). The explicit foreign keys of a scaffolded project carry the numbers the source database ended up with (<c>fk_a_fkey</c>, <c>fk_a_fkey4</c>,
    /// <c>fk_a_fkey12</c>), which only fit if they are created in that order, so a foreign key is created after the one of the same stem with the next lower number (G-252).
    /// </summary>
    private static void AddForeignKeyNumberOrderDependencies(DatabaseModel model, ConstraintDefinition constraint, ISet<string> dependencyIds)
    {
        if (!ReferencesPartitionedTable(model, constraint) || !TryResolveTableKey(model, constraint.SchemaName, constraint.TableName, out var tableKey))
        {
            return;
        }

        var (stem, number) = SplitTrailingNumber(constraint.Name);
        ConstraintDefinition? predecessor = null;
        var predecessorNumber = -1;
        foreach (var other in model.Constraints.Values)
        {
            if (string.Equals(other.Key, constraint.Key, StringComparison.Ordinal)
                || !ReferencesPartitionedTable(model, other)
                || !TryResolveTableKey(model, other.SchemaName, other.TableName, out var otherTableKey)
                || !string.Equals(otherTableKey, tableKey, StringComparison.Ordinal))
            {
                continue;
            }

            var (otherStem, otherNumber) = SplitTrailingNumber(other.Name);
            if (string.Equals(otherStem, stem, StringComparison.Ordinal) && otherNumber < number && otherNumber > predecessorNumber)
            {
                predecessor = other;
                predecessorNumber = otherNumber;
            }
        }

        if (predecessor is not null)
        {
            dependencyIds.Add(predecessor.Key);
        }
    }

    private static bool ReferencesPartitionedTable(DatabaseModel model, ConstraintDefinition constraint)
        => !string.IsNullOrWhiteSpace(constraint.ForeignKeyReferencedTable)
            && TryResolveTableKey(model, constraint.ForeignKeyReferencedSchemaName ?? constraint.SchemaName, constraint.ForeignKeyReferencedTable, out var referencedKey)
            && model.TryGetTable(referencedKey, out var referenced) && referenced is { PartitionStrategy: not null };

    private static (string Stem, int Number) SplitTrailingNumber(string name)
    {
        var end = name.Length;
        while (end > 0 && char.IsAsciiDigit(name[end - 1]))
        {
            end--;
        }

        return (name[..end], end == name.Length || !int.TryParse(name[end..], System.Globalization.CultureInfo.InvariantCulture, out var number) ? 0 : number);
    }

    private static void AddReferencedKeyDependencies(DatabaseModel model, ConstraintDefinition foreignKey, ISet<string> dependencyIds)
    {
        if (string.IsNullOrWhiteSpace(foreignKey.ForeignKeyReferencedTable)
            || !TryResolveTableKey(model, foreignKey.ForeignKeyReferencedSchemaName ?? foreignKey.SchemaName, foreignKey.ForeignKeyReferencedTable, out var targetTableKey))
        {
            return;
        }

        var referencedColumns = foreignKey.ForeignKeyReferencedColumns;

        // A partition has the keys of its parent (a partition's own keys are the parent's, G-231), so a foreign key to a partition needs the parent's key, and its parent's.
        var targetTableKeys = new HashSet<string>(PgProj.Parser.SqlIdentifiers.NameComparer) { targetTableKey };
        for (var current = model.TryGetTable(targetTableKey, out var targetTable) ? targetTable : null;
             current?.PartitionOfTableName is not null && TryResolveTableKey(model, current.PartitionOfSchemaName ?? current.SchemaName, current.PartitionOfTableName, out var parentKey) && targetTableKeys.Add(parentKey);
             current = model.TryGetTable(parentKey, out var parentTable) ? parentTable : null)
        {
        }

        foreach (var candidate in model.Constraints.Values)
        {
            if (string.Equals(candidate.Key, foreignKey.Key, StringComparison.Ordinal)
                || !TryResolveTableKey(model, candidate.SchemaName, candidate.TableName, out var candidateTableKey)
                || !targetTableKeys.Contains(candidateTableKey))
            {
                continue;
            }

            var candidateKind = candidate.ConstraintType?.Trim().Replace('_', ' ').ToUpperInvariant();
            var isPrimaryKey = candidateKind is "PRIMARY KEY" or "CONSTR PRIMARY" or "P" or "PK";
            var isUnique = candidateKind is "UNIQUE" or "CONSTR UNIQUE" or "U";

            if ((isPrimaryKey && (referencedColumns.Count == 0 || SameColumnSet(referencedColumns, candidate.PrimaryKeyColumns)))
                || (isUnique && referencedColumns.Count > 0 && SameColumnSet(referencedColumns, candidate.UniqueColumns)))
            {
                dependencyIds.Add(candidate.Key);
            }
        }

        if (referencedColumns.Count == 0)
        {
            return;
        }

        foreach (var index in model.Indexes.Values)
        {
            // Only a non-partial unique index over plain columns can enforce a foreign key.
            if (index.IsUnique
                && string.IsNullOrWhiteSpace(index.RawPredicateExpression)
                && index.KeyElements.All(static element => string.IsNullOrWhiteSpace(element.RawExpression))
                && TryResolveTableKey(model, index.SchemaName, index.TableName, out var indexTableKey)
                && targetTableKeys.Contains(indexTableKey)
                && SameColumnSet(referencedColumns, index.KeyColumns))
            {
                dependencyIds.Add(index.Key);
            }
        }
    }

    /// <summary>
    /// A table written with <c>LIKE source INCLUDING ...</c> copies, when it is created, the source's constraints, indexes, and statistics that the options
    /// name (G-221). They are their own statements, after the source's <c>CREATE TABLE</c>, so the table is created after each of them, not only after the
    /// source; the edge to the source itself is one of the table's <see cref="ISemanticNode.ReferencedTables"/>.
    /// </summary>
    private static void AddLikeDependencies(DatabaseModel model, TableDefinition table, ISet<string> dependencyIds)
    {
        foreach (var like in table.LikeClauses.Where(static like => like.SourceKind is null))
        {
            var sourceKey = DatabaseModel.CreateKey(like.SchemaName, like.Name);
            bool OnSource(string? schemaName, string? tableName)
                => tableName is not null && string.Equals(DatabaseModel.CreateKey(schemaName, tableName), sourceKey, StringComparison.Ordinal);

            foreach (var constraint in model.Constraints.Values.Where(constraint => OnSource(constraint.SchemaName, constraint.TableName)))
            {
                var kind = constraint.ConstraintType?.Trim().Replace('_', ' ').ToUpperInvariant();
                if ((like.Includes(PgProj.Parser.TableLikeNode.Constraints) && kind is "CHECK" or "CONSTR CHECK" or "C")
                    || (like.Includes(PgProj.Parser.TableLikeNode.Indexes) && kind is "PRIMARY KEY" or "CONSTR PRIMARY" or "P" or "UNIQUE" or "CONSTR UNIQUE" or "U" or "EXCLUDE" or "EXCLUSION" or "CONSTR EXCLUSION" or "X"))
                {
                    dependencyIds.Add(constraint.Key);
                }
            }

            if (like.Includes(PgProj.Parser.TableLikeNode.Indexes))
            {
                foreach (var index in model.Indexes.Values.Where(index => OnSource(index.SchemaName, index.TableName)))
                {
                    dependencyIds.Add(index.Key);
                }
            }

            if (like.Includes(PgProj.Parser.TableLikeNode.Statistics))
            {
                foreach (var statistics in model.Statistics.Values.Where(statistics => OnSource(statistics.TableSchemaName, statistics.TableName)))
                {
                    dependencyIds.Add(statistics.Key);
                }
            }
        }
    }

    private static bool SameColumnSet(IReadOnlyCollection<string> left, IReadOnlyCollection<string> right)
        => left.Count > 0
            && left.Count == right.Count
            && left.All(column => right.Contains(column, PgProj.Parser.SqlIdentifiers.NameComparer));

    private static void AddTableDependencies(DatabaseModel model, string? schemaName, IEnumerable<string> references, ISet<string> dependencyIds)
    {
        foreach (var reference in references)
        {
            AddTableDependency(model, schemaName, reference, dependencyIds);
        }
    }

    private static void AddTypeDependencies(DatabaseModel model, string? schemaName, IEnumerable<string> references, ISet<string> dependencyIds)
    {
        foreach (var reference in references)
        {
            AddTypeDependency(model, schemaName, reference, dependencyIds);
        }
    }

    private static void AddFunctionDependencies(DatabaseModel model, string? schemaName, IEnumerable<string> references, ISet<string> dependencyIds)
    {
        foreach (var reference in references)
        {
            AddFunctionDependency(model, schemaName, reference, dependencyIds);
        }
    }

    /// <summary>A view, default, CHECK, index, or routine that uses one of the project's operators is created after it and dropped before it (G-138).</summary>
    private static void AddOperatorDependencies(DatabaseModel model, IEnumerable<string> operatorKeys, ISet<string> dependencyIds)
    {
        foreach (var key in operatorKeys)
        {
            if (model.Operators.ContainsKey(key))
            {
                dependencyIds.Add(key);
            }
        }
    }

    /// <summary>
    /// A column, index key, or range type that names one of the project's collations is created after it and dropped before it (G-148). A name that is not the project's
    /// (a built-in or operating-system collation such as <c>"C"</c>) adds nothing. An unqualified name is looked for in the using object's own schema first.
    /// </summary>
    private static void AddCollationDependencies(DatabaseModel model, string? schemaName, IEnumerable<string?> collationNames, ISet<string> dependencyIds)
    {
        foreach (var name in collationNames)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var key = name.Contains('.', StringComparison.Ordinal) ? name : DatabaseModel.CreateKey(schemaName, name);
            if (model.TryGetCollation(key, out var collation) && collation is not null)
            {
                dependencyIds.Add(collation.Key);
            }
        }
    }

    /// <summary>
    /// An object whose expression names one of the project's text search configurations by a string (<c>to_tsvector('app.my_cfg', body)</c>, <c>'app.my_cfg'::regconfig</c>)
    /// is created after it and dropped before it (G-149). Nothing reads such a name as a reference, so the text of the expressions is searched for a string literal equal to
    /// the name of a configuration the project declares (qualified, or alone). A string that merely looks like one adds an edge that only fixes an order.
    /// </summary>
    private static void AddTextSearchConfigurationDependencies(DatabaseModel model, ISemanticNode node, ISet<string> dependencyIds)
    {
        if (model.TextSearchConfigurations.Count == 0)
        {
            return;
        }

        IEnumerable<string?> texts = node switch
        {
            TableDefinition table => table.Columns.SelectMany(static column => new[] { column.RawDefaultExpression, column.GeneratedExpression }),
            IndexDefinition index => index.KeyElements.Select(static element => element.RawExpression).Append(index.RawPredicateExpression),
            ConstraintDefinition constraint => [constraint.CheckExpressionNode?.Text, constraint.RawExclusionPredicateExpression],
            ViewDefinition view => [view.RawBodyExpression],
            FunctionDefinition function => [function.RawBody],
            ProcedureDefinition procedure => [procedure.RawBody],
            TriggerDefinition trigger => [trigger.RawWhenCondition],
            PolicyDefinition policy => [policy.RawUsingExpression, policy.RawWithCheckExpression],
            _ => [],
        };

        foreach (var text in texts.Where(static text => !string.IsNullOrWhiteSpace(text)))
        {
            foreach (var configuration in model.TextSearchConfigurations.Values)
            {
                var qualified = DatabaseModel.CreateKey(configuration.SchemaName, configuration.Name);
                if (text!.Contains($"'{qualified}'", StringComparison.OrdinalIgnoreCase)
                    || text.Contains($"'{configuration.Name}'", StringComparison.OrdinalIgnoreCase))
                {
                    dependencyIds.Add(configuration.Key);
                }
            }
        }
    }

    private static void AddTableDependency(DatabaseModel model, string? schemaName, string? reference, ISet<string> dependencyIds)
    {
        if (TryResolveTableKey(model, schemaName, reference, out var key))
        {
            dependencyIds.Add(key);
        }
        else if (model.FindSequence(schemaName, reference) is { } sequence)
        {
            // A query may read a sequence as a relation (SELECT last_value FROM seq, G-231).
            dependencyIds.Add(sequence.Key);
        }
    }

    /// <summary>
    /// A view that groups by a table's primary key may select the table's other columns, which PostgreSQL allows through the functional dependency; it checks that
    /// when the view is created, so the key must exist first (G-231). Only a view with a <c>GROUP BY</c> gets the edge, and only to the primary keys of the tables it reads.
    /// </summary>
    private static void AddPrimaryKeyDependencies(DatabaseModel model, ViewDefinition view, ISet<string> dependencyIds)
    {
        if (string.IsNullOrWhiteSpace(view.RawBodyExpression) || !GroupByRegex.IsMatch(view.RawBodyExpression))
        {
            return;
        }

        var tableKeys = new HashSet<string>(PgProj.Parser.SqlIdentifiers.NameComparer);
        foreach (var reference in view.ReferencedTables)
        {
            if (TryResolveTableKey(model, view.SchemaName, reference, out var tableKey))
            {
                tableKeys.Add(tableKey);
            }
        }

        foreach (var constraint in model.Constraints.Values)
        {
            if (constraint.ConstraintType?.Trim().Replace('_', ' ').ToUpperInvariant() is "PRIMARY KEY" or "CONSTR PRIMARY" or "P" or "PK"
                && TryResolveTableKey(model, constraint.SchemaName, constraint.TableName, out var constraintTableKey)
                && tableKeys.Contains(constraintTableKey))
            {
                dependencyIds.Add(constraint.Key);
            }
        }
    }

    private static readonly System.Text.RegularExpressions.Regex GroupByRegex = new(@"\bGROUP\s+BY\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static void AddTypeDependency(DatabaseModel model, string? schemaName, string? reference, ISet<string> dependencyIds)
    {
        if (TryResolveTypeKey(model, schemaName, reference, out var key))
        {
            dependencyIds.Add(key);
            return;
        }

        // A table or view name is also a type (its row type); depend on it so a routine or column that uses that
        // row type is ordered after the table or view (G-50).
        if (TryResolveTableKey(model, schemaName, reference, out var tableKey))
        {
            dependencyIds.Add(tableKey);
        }
        else if (TryResolveKey<ViewDefinition>(reference, schemaName, model.TryGetView, out var viewKey))
        {
            dependencyIds.Add(viewKey);
        }
        else if (MultirangeTypes.FindRange(model, schemaName, reference) is { } range)
        {
            // A multirange is created with its range (G-204).
            dependencyIds.Add(range.Key);
        }
    }

    private static void AddFunctionDependency(DatabaseModel model, string? schemaName, string? reference, ISet<string> dependencyIds)
    {
        if (TryResolveFunctionKey(model, schemaName, reference, out var key))
        {
            dependencyIds.Add(key);
            return;
        }

        // A call that is no project function may be one of the project's aggregates (G-130); it does not say which overload, so all of them.
        foreach (var aggregate in AggregateReferences.Find(model, schemaName, reference))
        {
            dependencyIds.Add(aggregate.Key);
        }
    }

    private static bool TryResolveTableKey(DatabaseModel model, string? schemaName, string? reference, out string key)
        => TryResolveKey<TableDefinition>(reference, schemaName, model.TryGetTable, out key);

    private static bool TryResolveTypeKey(DatabaseModel model, string? schemaName, string? reference, out string key)
        => TryResolveKey<TypeDefinition>(reference, schemaName, model.TryGetType, out key);

    private static bool TryResolveFunctionKey(DatabaseModel model, string? schemaName, string? reference, out string key)
    {
        // A call site or a trigger's EXECUTE FUNCTION names a routine without its argument types, but FunctionDefinition.Key is
        // overload-qualified (G-29), so an exact-key lookup only succeeds for a reference that spells them. The shared resolver
        // also matches by schema and name, including a schema-qualified reference (public.bump); this fallback used to compare
        // the bare declared name with the whole reference, so a qualified call had no edge and its caller could be created
        // before the function (G-54).
        var function = FunctionReferences.Resolve(model, schemaName, reference);
        key = function?.Key ?? string.Empty;
        return function is not null;
    }

    private static readonly System.Text.RegularExpressions.Regex DeclareBlockRegex = new(
        @"\bDECLARE\b(?<declarations>.*?)\bBEGIN\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    // name [CONSTANT] type [COLLATE c] [NOT NULL] [DEFAULT | := | = expr]: the type is what follows the name (and CONSTANT) up to one of those words.
    private static readonly System.Text.RegularExpressions.Regex DeclaredVariableRegex = new(
        @"^\s*[\w\x22]+\s+(?:CONSTANT\s+)?(?<type>[\w.\x22]+(?:\s*%\s*(?:ROW)?TYPE)?)(?:\s*\[\s*\d*\s*\])*\s*(?:COLLATE\b|NOT\b|DEFAULT\b|:=|=|$)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// A PL/pgSQL function resolves the types its variables are declared with when it is created, so it needs them to exist (G-231): <c>DECLARE v mytype;</c> depends on
    /// <c>mytype</c>, and <c>r tbl%ROWTYPE</c> (or <c>col%TYPE</c>) on the table. Only the <c>DECLARE ... BEGIN</c> sections are read, each declaration up to its first
    /// <c>;</c>; a name that is no project type or table adds nothing, so a built-in type or a cursor is harmless.
    /// </summary>
    private static void AddDeclaredVariableDependencies(DatabaseModel model, string? schemaName, string? language, string? rawBody, ISet<string> dependencyIds)
    {
        if (!string.Equals(language, "plpgsql", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(rawBody))
        {
            return;
        }

        foreach (System.Text.RegularExpressions.Match block in DeclareBlockRegex.Matches(rawBody))
        {
            foreach (var declaration in block.Groups["declarations"].Value.Split(';'))
            {
                var match = DeclaredVariableRegex.Match(declaration);
                if (!match.Success)
                {
                    continue;
                }

                var type = match.Groups["type"].Value.Replace(((char)34).ToString(), string.Empty, StringComparison.Ordinal);
                var isRowType = type.Contains('%', StringComparison.Ordinal);
                var name = isRowType ? type[..type.IndexOf('%', StringComparison.Ordinal)].Trim() : type;
                if (isRowType)
                {
                    // tbl%ROWTYPE and tbl.col%TYPE name a table; a column's table is the name before its last dot.
                    var relation = type.Contains("%ROWTYPE", StringComparison.OrdinalIgnoreCase) || !name.Contains('.', StringComparison.Ordinal) ? name : name[..name.LastIndexOf('.')];
                    AddTableDependency(model, schemaName, relation, dependencyIds);
                }
                else
                {
                    AddTypeDependency(model, schemaName, name, dependencyIds);
                }
            }
        }
    }

    private static readonly System.Text.RegularExpressions.Regex CallRegex = new(
        @"(?<name>[\w.\x22]+)\s*\(",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// The project functions an expression's text calls (<c>DEFAULT foo(4)</c>), read as every <c>name(</c> that names one (G-231). A word that is no project function (a
    /// built-in, a type with modifiers) adds nothing.
    /// </summary>
    private static void AddCalledFunctionDependencies(DatabaseModel model, string? schemaName, string? text, ISet<string> dependencyIds)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        foreach (System.Text.RegularExpressions.Match match in CallRegex.Matches(text))
        {
            AddFunctionDependency(model, schemaName, match.Groups["name"].Value.Replace(((char)34).ToString(), string.Empty, StringComparison.Ordinal), dependencyIds);
        }
    }

    private static bool TryResolveSequenceKey(DatabaseModel model, string? schemaName, string? reference, out string key)
        => TryResolveKey<SequenceDefinition>(reference, schemaName, model.TryGetSequence, out key);

    // A sequence is named by a string literal: nextval('s.seq'), or any 'name'::regclass (which is what PostgreSQL
    // itself writes back, nextval('s.seq'::regclass)).
    private static readonly System.Text.RegularExpressions.Regex SequenceCallRegex = new(
        @"\b(?:nextval|currval|setval)\s*\(\s*'(?<name>(?:''|[^'])*)'",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex RegclassCastRegex = new(
        @"'(?<name>(?:''|[^'])*)'\s*::\s*(?:pg_catalog\s*\.\s*)?regclass\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// A column default such as <c>DEFAULT nextval('s.ticket_seq')</c> needs that sequence to exist first, so the table
    /// depends on it (G-56). Before defaults were captured this never mattered; now that they are deployed, creating
    /// the table first fails with <c>42P01</c>.
    /// </summary>
    private static void AddDefaultSequenceDependencies(DatabaseModel model, TableDefinition table, ISet<string> dependencyIds)
    {
        foreach (var column in table.Columns)
        {
            if (string.IsNullOrWhiteSpace(column.RawDefaultExpression))
            {
                continue;
            }

            AddSequencesNamedIn(model, table.SchemaName, column.RawDefaultExpression, dependencyIds);
        }
    }

    /// <summary>
    /// A SQL-language routine body is checked when the routine is created, so a sequence it names
    /// (<c>nextval('s.seq')</c>, <c>'s.seq'::regclass</c>) must exist first, and the routine depends on it (G-173). A PL/pgSQL
    /// body is not checked at <c>CREATE</c>, so it needs no ordering and is not read.
    /// </summary>
    private static void AddBodySequenceDependencies(DatabaseModel model, string? schemaName, string? language, string? rawBody, ISet<string> dependencyIds)
    {
        if (string.IsNullOrWhiteSpace(rawBody) || !string.Equals(language?.Trim(), "sql", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        AddSequencesNamedIn(model, schemaName, rawBody, dependencyIds);
    }

    /// <summary>
    /// A view, a materialized view, a <c>CHECK</c> or exclusion constraint, an index expression or predicate, a policy, or a trigger
    /// <c>WHEN</c> condition that names a sequence (<c>nextval('s.seq')</c>, <c>'s.seq'::regclass</c>) is created after it (G-174);
    /// PostgreSQL resolves the name when the object is created. A column default and a SQL routine body are read the same way.
    /// </summary>
    private static void AddExpressionSequenceDependencies(DatabaseModel model, ISemanticNode node, ISet<string> dependencyIds)
    {
        IEnumerable<string?> texts = node switch
        {
            IndexDefinition index => index.KeyElements.Select(static element => element.RawExpression).Append(index.RawPredicateExpression),
            ConstraintDefinition constraint => [constraint.RawCheckExpression, constraint.CheckExpressionNode?.Text, constraint.RawExclusionPredicateExpression],
            ViewDefinition view => [view.RawBodyExpression],
            TriggerDefinition trigger => [trigger.RawWhenCondition],
            PolicyDefinition policy => [policy.RawUsingExpression, policy.RawWithCheckExpression],
            _ => [],
        };

        foreach (var text in texts)
        {
            AddSequencesNamedIn(model, node.SchemaName, text, dependencyIds);
        }
    }

    private static void AddSequencesNamedIn(DatabaseModel model, string? schemaName, string? text, ISet<string> dependencyIds)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        foreach (var regex in new[] { SequenceCallRegex, RegclassCastRegex })
        {
            foreach (System.Text.RegularExpressions.Match match in regex.Matches(text))
            {
                var name = match.Groups["name"].Value.Replace("''", "'", StringComparison.Ordinal).Replace("\"", string.Empty, StringComparison.Ordinal);
                if (TryResolveSequenceKey(model, schemaName, name, out var sequenceKey))
                {
                    dependencyIds.Add(sequenceKey);
                }
            }
        }
    }

    /// <summary>
    /// A SQL-language procedure that <c>CALL</c>s another project procedure is created after it, because PostgreSQL checks the
    /// body when the procedure is created (G-175). A PL/pgSQL body is not checked, so two procedures that call each other need
    /// no order (and must not make a cycle). A procedure's own name is skipped, so recursion is not a cycle either.
    /// </summary>
    private static void AddCalledProcedureDependencies(DatabaseModel model, ProcedureDefinition procedure, ISet<string> dependencyIds)
    {
        if (!string.Equals(procedure.Language?.Trim(), "sql", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (var reference in procedure.ReferencedFunctions)
        {
            var called = FunctionReferences.ResolveProcedure(model, procedure.SchemaName, reference);
            if (called is not null && !string.Equals(called.Key, procedure.Key, StringComparison.Ordinal))
            {
                dependencyIds.Add(called.Key);
            }
        }
    }

    private static void AddOwnedSequenceDependencies(DatabaseModel model, TableDefinition table, ISet<string> dependencyIds)
    {
        foreach (var sequence in model.Sequences.Values)
        {
            if (!string.IsNullOrWhiteSpace(sequence.OwnedByTable)
                && string.Equals(sequence.OwnedByTable.Trim(), table.Key, StringComparison.OrdinalIgnoreCase))
            {
                dependencyIds.Add(sequence.Key);
            }
        }
    }

    private static bool TryResolveKey<TNode>(
        string? reference,
        string? schemaName,
        TryGetNode<TNode> tryGetNode,
        out string key)
        where TNode : class, ISemanticNode
    {
        key = string.Empty;

        if (string.IsNullOrWhiteSpace(reference))
        {
            return false;
        }

        var trimmedReference = reference.Trim();
        if (tryGetNode(trimmedReference, out var node) && node is not null)
        {
            key = node.Key;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(schemaName))
        {
            var scopedReference = DatabaseModel.CreateKey(schemaName, trimmedReference);
            if (tryGetNode(scopedReference, out node) && node is not null)
            {
                key = node.Key;
                return true;
            }
        }

        return false;
    }

    private delegate bool TryGetNode<TNode>(string key, out TNode? node)
        where TNode : class, ISemanticNode;
}
