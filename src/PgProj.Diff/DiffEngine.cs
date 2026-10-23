using PgProj.Semantics;

namespace PgProj.Diff;

public sealed class DiffEngine
{
    private DatabaseModel? _oldModel;
    private DatabaseModel? _newModel;
    private List<SemanticDiagnostic>? _diagnostics;

    public DiffResult Execute(DatabaseModel oldModel, DatabaseModel newModel)
    {
        ArgumentNullException.ThrowIfNull(oldModel);
        ArgumentNullException.ThrowIfNull(newModel);

        // A column whose extension type moves with its extension is the same column (G-218).
        oldModel = MovedExtensionTypes.Apply(oldModel, newModel);
        // A renamed column's constraints and indexes, and the foreign keys referencing it, go with it (G-277).
        oldModel = RenamedColumnReferences.Apply(oldModel, newModel);
        _oldModel = oldModel;
        _newModel = newModel;
        _diagnostics = [];

        var tableDiff = DiffTables(oldModel, newModel);
        var functionDiff = DiffFunctions(oldModel, newModel);
        var viewDiff = DiffViews(oldModel, newModel);
        var typeDiff = DiffTypes(oldModel, newModel);
        var sequenceDiff = DiffSequences(oldModel, newModel);
        var procedureDiff = DiffProcedures(oldModel, newModel);
        var triggerDiff = DiffTriggers(oldModel, newModel);
        var policyDiff = DiffPolicies(oldModel, newModel);
        var eventTriggerDiff = DiffEventTriggers(oldModel, newModel);
        var castDiff = DiffCasts(oldModel, newModel);
        var operatorDiff = DiffOperators(oldModel, newModel);
        var ruleDiff = DiffRules(oldModel, newModel);
        var aggregateDiff = DiffAggregates(oldModel, newModel);
        var collationDiff = DiffCollations(oldModel, newModel);
        var statisticsDiff = DiffStatistics(oldModel, newModel);
        var dictionaryDiff = DiffTextSearchDictionaries(oldModel, newModel);
        var configurationDiff = DiffTextSearchConfigurations(oldModel, newModel);
        var publicationDiff = DiffPublications(oldModel, newModel);
        var ownerDiff = DiffOwners(oldModel, newModel);
        var schemaDiff = DiffSchemaDefinitions(oldModel, newModel);

        return new DiffResult
        {
            AddedTables = tableDiff.Added,
            RemovedTables = tableDiff.Removed,
            ModifiedTables = tableDiff.Modified,
            AddedColumns = tableDiff.AddedColumns,
            RemovedColumns = tableDiff.RemovedColumns,
            ModifiedColumns = tableDiff.ModifiedColumns,
            AddedConstraints = tableDiff.AddedConstraints,
            RemovedConstraints = tableDiff.RemovedConstraints,
            ModifiedConstraints = tableDiff.ModifiedConstraints,
            AddedIndexes = tableDiff.AddedIndexes,
            RemovedIndexes = tableDiff.RemovedIndexes,
            ModifiedIndexes = tableDiff.ModifiedIndexes,
            AddedFunctions = functionDiff.Added,
            RemovedFunctions = functionDiff.Removed,
            ModifiedFunctions = functionDiff.Modified,
            AddedViews = viewDiff.Added,
            RemovedViews = viewDiff.Removed,
            ModifiedViews = viewDiff.Modified,
            AddedTypes = typeDiff.Added,
            RemovedTypes = typeDiff.Removed,
            ModifiedTypes = typeDiff.Modified,
            AddedSequences = sequenceDiff.Added,
            RemovedSequences = sequenceDiff.Removed,
            ModifiedSequences = sequenceDiff.Modified,
            AddedProcedures = procedureDiff.Added,
            RemovedProcedures = procedureDiff.Removed,
            ModifiedProcedures = procedureDiff.Modified,
            AddedTriggers = triggerDiff.Added,
            RemovedTriggers = triggerDiff.Removed,
            ModifiedTriggers = triggerDiff.Modified,
            AddedPolicies = policyDiff.Added,
            RemovedPolicies = policyDiff.Removed,
            ModifiedPolicies = policyDiff.Modified,
            AddedEventTriggers = eventTriggerDiff.Added,
            RemovedEventTriggers = eventTriggerDiff.Removed,
            ModifiedEventTriggers = eventTriggerDiff.Modified,
            AddedCasts = castDiff.Added,
            RemovedCasts = castDiff.Removed,
            ModifiedCasts = castDiff.Modified,
            AddedOperators = operatorDiff.Added,
            RemovedOperators = operatorDiff.Removed,
            ModifiedOperators = operatorDiff.Modified,
            AddedRules = ruleDiff.Added,
            RemovedRules = ruleDiff.Removed,
            ModifiedRules = ruleDiff.Modified,
            AddedAggregates = aggregateDiff.Added,
            RemovedAggregates = aggregateDiff.Removed,
            ModifiedAggregates = aggregateDiff.Modified,
            AddedCollations = collationDiff.Added,
            RemovedCollations = collationDiff.Removed,
            ModifiedCollations = collationDiff.Modified,
            AddedStatistics = statisticsDiff.Added,
            RemovedStatistics = statisticsDiff.Removed,
            ModifiedStatistics = statisticsDiff.Modified,
            AddedTextSearchDictionaries = dictionaryDiff.Added,
            RemovedTextSearchDictionaries = dictionaryDiff.Removed,
            ModifiedTextSearchDictionaries = dictionaryDiff.Modified,
            AddedTextSearchConfigurations = configurationDiff.Added,
            RemovedTextSearchConfigurations = configurationDiff.Removed,
            ModifiedTextSearchConfigurations = configurationDiff.Modified,
            AddedPublications = publicationDiff.Added,
            RemovedPublications = publicationDiff.Removed,
            ModifiedPublications = publicationDiff.Modified,
            AddedOwners = ownerDiff.Added,
            RemovedOwners = [],
            ModifiedOwners = ownerDiff.Modified,
            AddedSchemaDefinitions = schemaDiff.Added,
            RemovedSchemaDefinitions = [],
            ModifiedSchemaDefinitions = schemaDiff.Modified,
            Diagnostics = _diagnostics.ToArray(),
        };
    }

    public ChangeSet Diff(DatabaseModel oldModel, DatabaseModel newModel)
    {
        var result = Execute(oldModel, newModel);

        var added = new List<AddedChange>();
        var removed = new List<RemovedChange>();
        var modified = new List<ModifiedChange>();

        AddChanges(result.AddedTables, added);
        AddChanges(result.RemovedTables, removed);
        AddChanges(result.ModifiedTables, modified);

        AddChanges(result.AddedColumns, added);
        AddChanges(result.RemovedColumns, removed);
        AddChanges(result.ModifiedColumns, modified);

        AddChanges(result.AddedConstraints, added);
        AddChanges(result.RemovedConstraints, removed);
        AddChanges(result.ModifiedConstraints, modified);

        AddChanges(result.AddedIndexes, added);
        AddChanges(result.RemovedIndexes, removed);
        AddChanges(result.ModifiedIndexes, modified);

        AddChanges(result.AddedFunctions, added);
        AddChanges(result.RemovedFunctions, removed);
        AddChanges(result.ModifiedFunctions, modified);

        AddChanges(result.AddedViews, added);
        AddChanges(result.RemovedViews, removed);
        AddChanges(result.ModifiedViews, modified);

        AddChanges(result.AddedTypes, added);
        AddChanges(result.RemovedTypes, removed);
        AddChanges(result.ModifiedTypes, modified);

        AddChanges(result.AddedSequences, added);
        AddChanges(result.RemovedSequences, removed);
        AddChanges(result.ModifiedSequences, modified);

        AddChanges(result.AddedProcedures, added);
        AddChanges(result.RemovedProcedures, removed);
        AddChanges(result.ModifiedProcedures, modified);

        AddChanges(result.AddedTriggers, added);
        AddChanges(result.RemovedTriggers, removed);
        AddChanges(result.ModifiedTriggers, modified);

        AddChanges(result.AddedPolicies, added);
        AddChanges(result.RemovedPolicies, removed);
        AddChanges(result.ModifiedPolicies, modified);

        AddChanges(result.AddedEventTriggers, added);
        AddChanges(result.RemovedEventTriggers, removed);
        AddChanges(result.ModifiedEventTriggers, modified);

        AddChanges(result.AddedCasts, added);
        AddChanges(result.RemovedCasts, removed);
        AddChanges(result.ModifiedCasts, modified);
        AddChanges(result.AddedOperators, added);
        AddChanges(result.RemovedOperators, removed);
        AddChanges(result.ModifiedOperators, modified);
        AddChanges(result.AddedRules, added);
        AddChanges(result.RemovedRules, removed);
        AddChanges(result.ModifiedRules, modified);
        AddChanges(result.AddedAggregates, added);
        AddChanges(result.RemovedAggregates, removed);
        AddChanges(result.ModifiedAggregates, modified);
        AddChanges(result.AddedCollations, added);
        AddChanges(result.RemovedCollations, removed);
        AddChanges(result.ModifiedCollations, modified);
        AddChanges(result.AddedStatistics, added);
        AddChanges(result.RemovedStatistics, removed);
        AddChanges(result.ModifiedStatistics, modified);
        AddChanges(result.AddedTextSearchDictionaries, added);
        AddChanges(result.RemovedTextSearchDictionaries, removed);
        AddChanges(result.ModifiedTextSearchDictionaries, modified);
        AddChanges(result.AddedTextSearchConfigurations, added);
        AddChanges(result.RemovedTextSearchConfigurations, removed);
        AddChanges(result.ModifiedTextSearchConfigurations, modified);
        AddChanges(result.AddedPublications, added);
        AddChanges(result.RemovedPublications, removed);
        AddChanges(result.ModifiedPublications, modified);
        AddChanges(result.AddedOwners, added);
        AddChanges(result.ModifiedOwners, modified);
        AddChanges(result.AddedSchemaDefinitions, added);
        AddChanges(result.ModifiedSchemaDefinitions, modified);

        DiffGrants(oldModel, newModel, added, removed);

        return new ChangeSet
        {
            Added = added,
            Removed = removed,
            Modified = modified,
        };
    }

    private TableDiffState DiffTables(DatabaseModel oldModel, DatabaseModel newModel)
    {
        var addedTables = new List<TableDefinition>();
        var removedTables = new List<TableDefinition>();
        var modifiedTables = new List<SemanticObjectModification<TableDefinition>>();

        var addedColumns = new List<ColumnDefinition>();
        var removedColumns = new List<ColumnDefinition>();
        var modifiedColumns = new List<SemanticObjectModification<ColumnDefinition>>();

        var addedConstraints = new List<ConstraintDefinition>();
        var removedConstraints = new List<ConstraintDefinition>();
        var modifiedConstraints = new List<SemanticObjectModification<ConstraintDefinition>>();

        var addedIndexes = new List<IndexDefinition>();
        var removedIndexes = new List<IndexDefinition>();
        var modifiedIndexes = new List<SemanticObjectModification<IndexDefinition>>();

        foreach (var (tableKey, oldTable) in oldModel.Tables)
        {
            ValidateSemanticObject(oldTable, "DiffTables");

            if (!newModel.Tables.TryGetValue(tableKey, out var newTable))
            {
                removedTables.Add(oldTable);
                removedColumns.AddRange(oldTable.Columns);
                removedConstraints.AddRange(GetTableConstraints(oldModel, oldTable, "DiffTables(old)"));
                removedIndexes.AddRange(GetTableIndexes(oldModel, oldTable, "DiffTables(old)"));
                continue;
            }

            ValidateSemanticObject(newTable, "DiffTables");

            var columnDiff = DiffColumns(oldTable, newTable);
            var constraintDiff = DiffConstraints(oldTable, newTable);
            var indexDiff = DiffIndexes(oldTable, newTable);
            var details = BuildTableModificationDetails(oldTable, newTable, columnDiff, constraintDiff, indexDiff);

            addedColumns.AddRange(columnDiff.Added);
            removedColumns.AddRange(columnDiff.Removed);
            modifiedColumns.AddRange(columnDiff.Modified);

            addedConstraints.AddRange(constraintDiff.Added);
            removedConstraints.AddRange(constraintDiff.Removed);
            modifiedConstraints.AddRange(constraintDiff.Modified);

            addedIndexes.AddRange(indexDiff.Added);
            removedIndexes.AddRange(indexDiff.Removed);
            modifiedIndexes.AddRange(indexDiff.Modified);

            if (details.Count > 0)
            {
                modifiedTables.Add(new SemanticObjectModification<TableDefinition>
                {
                    NodeId = newTable.Key,
                    OldNode = oldTable,
                    NewNode = newTable,
                    Details = details,
                });
            }
        }

        foreach (var (tableKey, newTable) in newModel.Tables)
        {
            ValidateSemanticObject(newTable, "DiffTables");

            if (oldModel.Tables.ContainsKey(tableKey))
            {
                continue;
            }

            addedTables.Add(newTable);
            addedColumns.AddRange(newTable.Columns);
            addedConstraints.AddRange(GetTableConstraints(newModel, newTable, "DiffTables(new)"));
            addedIndexes.AddRange(GetTableIndexes(newModel, newTable, "DiffTables(new)"));
        }

        // A materialized view can own indexes too (G-171). They are found through the view, as a table's are through the table.
        foreach (var newView in newModel.Views.Values.Where(static view => view.IsMaterialized))
        {
            var oldView = oldModel.Views.TryGetValue(newView.Key, out var existing) && existing.IsMaterialized ? existing : null;
            var viewDiff = DiffIndexSets(
                oldView is null ? [] : GetRelationIndexes(oldModel, oldView.SchemaName, oldView.Name),
                GetRelationIndexes(newModel, newView.SchemaName, newView.Name));
            addedIndexes.AddRange(viewDiff.Added);
            removedIndexes.AddRange(viewDiff.Removed);
            modifiedIndexes.AddRange(viewDiff.Modified);
        }

        foreach (var oldView in oldModel.Views.Values.Where(static view => view.IsMaterialized))
        {
            if (newModel.Views.TryGetValue(oldView.Key, out var counterpart) && counterpart.IsMaterialized)
            {
                continue;
            }

            removedIndexes.AddRange(GetRelationIndexes(oldModel, oldView.SchemaName, oldView.Name));
        }

        return new TableDiffState(
            addedTables,
            removedTables,
            modifiedTables,
            addedColumns,
            removedColumns,
            modifiedColumns,
            addedConstraints,
            removedConstraints,
            modifiedConstraints,
            addedIndexes,
            removedIndexes,
            modifiedIndexes);
    }

    private NodeDiffState<ColumnDefinition> DiffColumns(TableDefinition oldTable, TableDefinition newTable)
    {
        var oldColumns = oldTable.Columns
            .Where(static column => column is not null)
            .ToDictionary(GetNodeIdentity, PgProj.Parser.SqlIdentifiers.NameComparer);
        var newColumns = newTable.Columns
            .Where(static column => column is not null)
            .ToDictionary(GetNodeIdentity, PgProj.Parser.SqlIdentifiers.NameComparer);

        var added = new List<ColumnDefinition>();
        var removed = new List<ColumnDefinition>();
        var modified = new List<SemanticObjectModification<ColumnDefinition>>();

        // Pair explicit renames first (see docs/renaming-columns.md), so a column annotated with
        // `-- @renamed-from: old_name` is treated as a Modified column rather than a destructive
        // remove+add pair, even though its identity key changed along with its name.
        var renamedOldColumnKeys = new HashSet<string>(PgProj.Parser.SqlIdentifiers.NameComparer);

        foreach (var (newColumnKey, newColumn) in newColumns)
        {
            if (string.IsNullOrWhiteSpace(newColumn.RenamedFrom) || oldColumns.ContainsKey(newColumnKey))
            {
                continue;
            }

            var oldColumnMatch = oldColumns.Values.FirstOrDefault(oldColumn =>
                newColumn.RenamedFrom is not null && string.Equals(oldColumn.Name, newColumn.RenamedFrom, StringComparison.Ordinal));

            if (oldColumnMatch is null || renamedOldColumnKeys.Contains(GetNodeIdentity(oldColumnMatch)))
            {
                continue;
            }

            ValidateSemanticObject(oldColumnMatch, "DiffColumns");
            ValidateSemanticObject(newColumn, "DiffColumns");

            var renameDetails = BuildColumnModificationDetails(oldColumnMatch, newColumn);
            AddOrUpdateDetail(renameDetails, nameof(ColumnDefinition.Name), oldColumnMatch.Name, newColumn.Name);

            modified.Add(new SemanticObjectModification<ColumnDefinition>
            {
                NodeId = newColumn.Key,
                OldNode = oldColumnMatch,
                NewNode = newColumn,
                Details = renameDetails,
            });

            renamedOldColumnKeys.Add(GetNodeIdentity(oldColumnMatch));
        }

        foreach (var (columnKey, oldColumn) in oldColumns)
        {
            ValidateSemanticObject(oldColumn, "DiffColumns");

            if (renamedOldColumnKeys.Contains(columnKey))
            {
                continue;
            }

            if (!newColumns.TryGetValue(columnKey, out var newColumn))
            {
                removed.Add(oldColumn);
                continue;
            }

            ValidateSemanticObject(newColumn, "DiffColumns");

            var details = BuildColumnModificationDetails(oldColumn, newColumn);
            if (details.Count == 0)
            {
                continue;
            }

            modified.Add(new SemanticObjectModification<ColumnDefinition>
            {
                NodeId = newColumn.Key,
                OldNode = oldColumn,
                NewNode = newColumn,
                Details = details,
            });
        }

        foreach (var (columnKey, newColumn) in newColumns)
        {
            ValidateSemanticObject(newColumn, "DiffColumns");

            if (oldColumns.ContainsKey(columnKey))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(newColumn.RenamedFrom)
                && modified.Any(m => ReferenceEquals(m.NewNode, newColumn)))
            {
                continue;
            }

            added.Add(newColumn);
        }

        return new NodeDiffState<ColumnDefinition>(added, removed, modified);
    }

    private NodeDiffState<ConstraintDefinition> DiffConstraints(TableDefinition oldTable, TableDefinition newTable)
    {
        var oldConstraints = GetTableConstraints(RequireOldModel(), oldTable, "DiffConstraints(old)")
            .ToDictionary(GetNodeIdentity, PgProj.Parser.SqlIdentifiers.NameComparer);
        var newConstraints = GetTableConstraints(RequireNewModel(), newTable, "DiffConstraints(new)")
            .ToDictionary(GetNodeIdentity, PgProj.Parser.SqlIdentifiers.NameComparer);

        var added = new List<ConstraintDefinition>();
        var removed = new List<ConstraintDefinition>();
        var modified = new List<SemanticObjectModification<ConstraintDefinition>>();

        foreach (var (constraintKey, oldConstraint) in oldConstraints)
        {
            ValidateSemanticObject(oldConstraint, "DiffConstraints");

            if (!newConstraints.TryGetValue(constraintKey, out var newConstraint))
            {
                removed.Add(oldConstraint);
                continue;
            }

            ValidateSemanticObject(newConstraint, "DiffConstraints");

            var details = BuildConstraintModificationDetails(oldConstraint, newConstraint);
            if (details.Count == 0)
            {
                continue;
            }

            modified.Add(new SemanticObjectModification<ConstraintDefinition>
            {
                NodeId = newConstraint.Key,
                OldNode = oldConstraint,
                NewNode = newConstraint,
                Details = details,
            });
        }

        foreach (var (constraintKey, newConstraint) in newConstraints)
        {
            ValidateSemanticObject(newConstraint, "DiffConstraints");

            if (oldConstraints.ContainsKey(constraintKey))
            {
                continue;
            }

            added.Add(newConstraint);
        }

        return new NodeDiffState<ConstraintDefinition>(added, removed, modified);
    }

    private NodeDiffState<IndexDefinition> DiffIndexes(TableDefinition oldTable, TableDefinition newTable)
        => DiffIndexSets(
            GetTableIndexes(RequireOldModel(), oldTable, "DiffIndexes(old)"),
            GetTableIndexes(RequireNewModel(), newTable, "DiffIndexes(new)"));

    private NodeDiffState<IndexDefinition> DiffIndexSets(IEnumerable<IndexDefinition> oldIndexSet, IEnumerable<IndexDefinition> newIndexSet)
    {
        var oldIndexes = oldIndexSet.ToDictionary(GetNodeIdentity, PgProj.Parser.SqlIdentifiers.NameComparer);
        var newIndexes = newIndexSet.ToDictionary(GetNodeIdentity, PgProj.Parser.SqlIdentifiers.NameComparer);

        var added = new List<IndexDefinition>();
        var removed = new List<IndexDefinition>();
        var modified = new List<SemanticObjectModification<IndexDefinition>>();

        foreach (var (indexKey, oldIndex) in oldIndexes)
        {
            ValidateSemanticObject(oldIndex, "DiffIndexes");

            if (!newIndexes.TryGetValue(indexKey, out var newIndex))
            {
                removed.Add(oldIndex);
                continue;
            }

            ValidateSemanticObject(newIndex, "DiffIndexes");

            var details = BuildIndexModificationDetails(oldIndex, newIndex);
            if (details.Count == 0)
            {
                continue;
            }

            modified.Add(new SemanticObjectModification<IndexDefinition>
            {
                NodeId = newIndex.Key,
                OldNode = oldIndex,
                NewNode = newIndex,
                Details = details,
            });
        }

        foreach (var (indexKey, newIndex) in newIndexes)
        {
            ValidateSemanticObject(newIndex, "DiffIndexes");

            if (oldIndexes.ContainsKey(indexKey))
            {
                continue;
            }

            added.Add(newIndex);
        }

        return new NodeDiffState<IndexDefinition>(added, removed, modified);
    }

    private NodeDiffState<FunctionDefinition> DiffFunctions(DatabaseModel oldModel, DatabaseModel newModel)
        => DiffModelNodes(
            oldModel.Functions,
            newModel.Functions,
            "DiffFunctions",
            BuildFunctionModificationDetails);

    private NodeDiffState<ProcedureDefinition> DiffProcedures(DatabaseModel oldModel, DatabaseModel newModel)
        => DiffModelNodes(
            oldModel.Procedures,
            newModel.Procedures,
            "DiffProcedures",
            BuildProcedureModificationDetails);

    private NodeDiffState<TriggerDefinition> DiffTriggers(DatabaseModel oldModel, DatabaseModel newModel)
        => DiffModelNodes(
            oldModel.Triggers,
            newModel.Triggers,
            "DiffTriggers",
            BuildTriggerModificationDetails);

    private NodeDiffState<PolicyDefinition> DiffPolicies(DatabaseModel oldModel, DatabaseModel newModel)
        => DiffModelNodes(
            oldModel.Policies,
            newModel.Policies,
            "DiffPolicies",
            BuildPolicyModificationDetails);

    private NodeDiffState<EventTriggerDefinition> DiffEventTriggers(DatabaseModel oldModel, DatabaseModel newModel)
        => DiffModelNodes(
            oldModel.EventTriggers,
            newModel.EventTriggers,
            "DiffEventTriggers",
            BuildEventTriggerModificationDetails);

    private NodeDiffState<OperatorDefinition> DiffOperators(DatabaseModel oldModel, DatabaseModel newModel)
        => DiffModelNodes(
            oldModel.Operators,
            newModel.Operators,
            "DiffOperators",
            BuildOperatorModificationDetails);

    private NodeDiffState<RuleDefinition> DiffRules(DatabaseModel oldModel, DatabaseModel newModel)
        => DiffModelNodes(
            oldModel.Rules,
            newModel.Rules,
            "DiffRules",
            BuildRuleModificationDetails);

    private NodeDiffState<AggregateDefinition> DiffAggregates(DatabaseModel oldModel, DatabaseModel newModel)
        => DiffModelNodes(
            oldModel.Aggregates,
            newModel.Aggregates,
            "DiffAggregates",
            BuildAggregateModificationDetails);

    private NodeDiffState<CollationDefinition> DiffCollations(DatabaseModel oldModel, DatabaseModel newModel)
        => DiffModelNodes(
            oldModel.Collations,
            newModel.Collations,
            "DiffCollations",
            BuildCollationModificationDetails);

    private NodeDiffState<StatisticsDefinition> DiffStatistics(DatabaseModel oldModel, DatabaseModel newModel)
        => DiffModelNodes(
            oldModel.Statistics,
            newModel.Statistics,
            "DiffStatistics",
            BuildStatisticsModificationDetails);

    private NodeDiffState<TextSearchDictionaryDefinition> DiffTextSearchDictionaries(DatabaseModel oldModel, DatabaseModel newModel)
        => DiffModelNodes(
            oldModel.TextSearchDictionaries,
            newModel.TextSearchDictionaries,
            "DiffTextSearchDictionaries",
            BuildTextSearchDictionaryModificationDetails);

    private NodeDiffState<TextSearchConfigurationDefinition> DiffTextSearchConfigurations(DatabaseModel oldModel, DatabaseModel newModel)
        => DiffModelNodes(
            oldModel.TextSearchConfigurations,
            newModel.TextSearchConfigurations,
            "DiffTextSearchConfigurations",
            BuildTextSearchConfigurationModificationDetails);

    private NodeDiffState<PublicationDefinition> DiffPublications(DatabaseModel oldModel, DatabaseModel newModel)
        => DiffModelNodes(
            oldModel.Publications,
            newModel.Publications,
            "DiffPublications",
            BuildPublicationModificationDetails);

    /// <summary>
    /// The owners the project states against the owners the old model has, by object. A project states an owner for the objects it chooses to, so an
    /// owner only the old model has (the database has one for every object) is not a change: there is no removed owner (G-146, decision 1).
    /// </summary>
    private NodeDiffState<OwnerDefinition> DiffOwners(DatabaseModel oldModel, DatabaseModel newModel)
        => DiffModelNodes(
            oldModel.Owners,
            newModel.Owners,
            "DiffOwners",
            BuildOwnerModificationDetails);

    /// <summary>
    /// The schemas the project declares or comments on against the old model's, by schema. A project states a comment for the schemas it chooses to, so a comment only the old model
    /// has is not a change: there is no removed schema comment (G-160), and no removed schema, since pgproj never drops one (G-166). A declared schema the old model lacks is added (G-284).
    /// </summary>
    private NodeDiffState<SchemaDefinition> DiffSchemaDefinitions(DatabaseModel oldModel, DatabaseModel newModel)
    {
        var state = DiffModelNodes(
            oldModel.SchemaDefinitions,
            newModel.SchemaDefinitions,
            "DiffSchemaDefinitions",
            BuildSchemaModificationDetails);

        // The live read declares every schema it reads (G-284), so a comment the project states on a schema that has none differs from a definition with no comment; it is still
        // reported as an added comment, as it was when the database had a definition only for a commented schema (G-160).
        var commentAdded = state.Modified.Where(static modified => !modified.OldNode.IsCommentStated).ToArray();

        // A project that states no comment (`IS NULL`) for a schema the database has no definition for already agrees with it; a declared schema is added, unless it is `public` or a
        // system schema, which every database has (and the live read does not declare).
        return state with
        {
            Added = state.Added
                .Where(static schema => schema.Comment is not null || (schema.IsDeclared && !IsBuiltInSchema(schema.Name)))
                .Concat(commentAdded.Select(static modified => modified.NewNode))
                .ToArray(),
            Modified = state.Modified.Except(commentAdded).ToArray(),
        };
    }

    /// <summary><c>public</c>, <c>information_schema</c>, or a <c>pg_</c> schema: one every database has, which is never created.</summary>
    private static bool IsBuiltInSchema(string name)
        => string.Equals(name, "public", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "information_schema", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("pg_", StringComparison.OrdinalIgnoreCase);

    private NodeDiffState<CastDefinition> DiffCasts(DatabaseModel oldModel, DatabaseModel newModel)
        => DiffModelNodes(
            oldModel.Casts,
            newModel.Casts,
            "DiffCasts",
            BuildCastModificationDetails);

    private NodeDiffState<ViewDefinition> DiffViews(DatabaseModel oldModel, DatabaseModel newModel)
        => DiffModelNodes(
            oldModel.Views,
            newModel.Views,
            "DiffViews",
            BuildViewModificationDetails);

    private NodeDiffState<TypeDefinition> DiffTypes(DatabaseModel oldModel, DatabaseModel newModel)
    {
        var state = DiffModelNodes(
            oldModel.Types,
            newModel.Types,
            "DiffTypes",
            BuildTypeModificationDetails);

        // An extension another one the project keeps requires, directly or through others, is not removed (G-213): CASCADE installed it, and PostgreSQL
        // could not drop it while that one is there. The old model's requirements are the database's.
        var required = new HashSet<string>(PgProj.Parser.SqlIdentifiers.NameComparer);
        var pending = new Stack<string>(newModel.Types.Keys.Where(oldModel.Types.ContainsKey));
        while (pending.TryPop(out var key))
        {
            if (oldModel.Types.TryGetValue(key, out var extension) && string.Equals(extension.TypeKind, "extension", StringComparison.Ordinal))
            {
                foreach (var requirement in extension.ExtensionRequires.Where(required.Add))
                {
                    pending.Push(requirement);
                }
            }
        }

        return required.Count == 0 ? state : state with { Removed = state.Removed.Where(type => !required.Contains(type.Key)).ToArray() };
    }

    private NodeDiffState<SequenceDefinition> DiffSequences(DatabaseModel oldModel, DatabaseModel newModel)
        => DiffModelNodes(
            oldModel.Sequences,
            newModel.Sequences,
            "DiffSequences",
            BuildSequenceModificationDetails);

    private bool CompareExpressions(ExpressionNode? oldExpr, ExpressionNode? newExpr)
    {
        if (ReferenceEquals(oldExpr, newExpr))
        {
            return true;
        }

        if (oldExpr is null || newExpr is null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(oldExpr.Kind) || string.IsNullOrWhiteSpace(newExpr.Kind))
        {
            AddDiagnostic(RequireDiagnostics(), "Encountered malformed expression tree with missing kind.", "CompareExpressions");
            return false;
        }

        if (string.IsNullOrWhiteSpace(oldExpr.Text) || string.IsNullOrWhiteSpace(newExpr.Text))
        {
            AddDiagnostic(RequireDiagnostics(), "Encountered malformed expression tree with missing text.", "CompareExpressions");
            return false;
        }

        if (!string.Equals(oldExpr.Kind, newExpr.Kind, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!ExpressionTextEquals(oldExpr, newExpr))
        {
            return false;
        }

        if (oldExpr.Children.Count != newExpr.Children.Count)
        {
            return false;
        }

        for (var index = 0; index < oldExpr.Children.Count; index++)
        {
            if (!CompareExpressions(oldExpr.Children[index], newExpr.Children[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static void AddDiagnostic(List<SemanticDiagnostic> diagnostics, string message, string source)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        diagnostics.Add(SemanticDiagnostics.Error(message, source: source));
    }

    /// <summary>
    /// Compares grants by what they grant, not by how they are written (G-65). A grant is expanded, against the objects of its own
    /// model, into atoms (one privilege on one object for one grantee; see <see cref="GrantExpansion"/>), because PostgreSQL
    /// records grants at that granularity: <c>ALL TABLES IN SCHEMA</c>, one table, and a list of tables read back the same.
    /// <list type="bullet">
    /// <item><description>A grant of <paramref name="oldModel"/> (the live database) is removed when it gives an atom that the
    /// project does not want. It is revoked as a whole.</description></item>
    /// <item><description>A grant of <paramref name="newModel"/> (the project) is added when any of its atoms is not in effect once
    /// those revocations are made. It is applied as a whole, which is harmless where part of it already holds.</description></item>
    /// </list>
    /// Two things are never an extra atom to revoke (G-61, G-65): PostgreSQL's implicit <c>PUBLIC</c> <c>EXECUTE</c> on routines
    /// (which is also never a missing one), and role membership, whose removal is a destructive change that is not made. A grant
    /// over <c>ALL ... IN SCHEMA</c> where the schema has no such object expands to nothing, so it can neither be missing nor
    /// re-added forever. A grant on an object the project drops is still revoked; the dependency graph orders it before the drop.
    /// </summary>
    private void DiffGrants(
        DatabaseModel oldModel,
        DatabaseModel newModel,
        ICollection<AddedChange> added,
        ICollection<RemovedChange> removed)
    {
        var oldGrants = OrderedGrants(oldModel);
        var newGrants = OrderedGrants(newModel);

        foreach (var oldGrant in oldGrants)
        {
            ValidateSemanticObject(oldGrant, "DiffGrants");
        }

        foreach (var newGrant in newGrants)
        {
            ValidateSemanticObject(newGrant, "DiffGrants");
        }

        // The live model knows the role it was read as; default privileges that the project states for that role, or for none, are one grant.
        var connectedRole = oldModel.ConnectedRole;
        var newAtoms = newGrants.Select(grant => (Grant: grant, Atoms: GrantExpansion.Expand(grant, newModel, connectedRole))).ToArray();
        var wanted = newAtoms.SelectMany(static item => item.Atoms).ToHashSet();

        var inEffect = new HashSet<GrantAtom>();
        foreach (var oldGrant in oldGrants)
        {
            var atoms = GrantExpansion.Expand(oldGrant, oldModel, connectedRole);
            var isExtra = atoms.Any(atom => !atom.IsImplicit && !atom.IsRoleMembership && !wanted.Contains(atom));

            if (isExtra)
            {
                removed.Add(new RemovedChange
                {
                    NodeId = oldGrant.Key,
                    NodeKind = ChangeRecord.MapKind(oldGrant),
                    Node = oldGrant,
                });

                continue;
            }

            inEffect.UnionWith(atoms);
        }

        foreach (var (newGrant, atoms) in newAtoms)
        {
            if (atoms.All(atom => atom.IsImplicit || inEffect.Contains(atom)))
            {
                continue;
            }

            added.Add(new AddedChange
            {
                NodeId = newGrant.Key,
                NodeKind = ChangeRecord.MapKind(newGrant),
                Node = newGrant,
            });
        }
    }

    private static GrantDefinition[] OrderedGrants(DatabaseModel model)
        => model.Grants.Values.OrderBy(static grant => grant.Key, StringComparer.Ordinal).ToArray();

    private NodeDiffState<TNode> DiffModelNodes<TNode>(
        IReadOnlyDictionary<string, TNode> oldNodes,
        IReadOnlyDictionary<string, TNode> newNodes,
        string source,
        Func<TNode, TNode, List<ModificationDetail>> buildDetails)
        where TNode : class, ISemanticNode
    {
        var added = new List<TNode>();
        var removed = new List<TNode>();
        var modified = new List<SemanticObjectModification<TNode>>();

        foreach (var (nodeKey, oldNode) in oldNodes)
        {
            ValidateSemanticObject(oldNode, source);

            if (!newNodes.TryGetValue(nodeKey, out var newNode))
            {
                removed.Add(oldNode);
                continue;
            }

            ValidateSemanticObject(newNode, source);
            var details = buildDetails(oldNode, newNode);
            if (details.Count == 0)
            {
                continue;
            }

            modified.Add(new SemanticObjectModification<TNode>
            {
                NodeId = newNode.Key,
                OldNode = oldNode,
                NewNode = newNode,
                Details = details,
            });
        }

        foreach (var (nodeKey, newNode) in newNodes)
        {
            ValidateSemanticObject(newNode, source);

            if (oldNodes.ContainsKey(nodeKey))
            {
                continue;
            }

            added.Add(newNode);
        }

        return new NodeDiffState<TNode>(added, removed, modified);
    }

    private List<ModificationDetail> BuildTableModificationDetails(
        TableDefinition oldTable,
        TableDefinition newTable,
        NodeDiffState<ColumnDefinition> columnDiff,
        NodeDiffState<ConstraintDefinition> constraintDiff,
        NodeDiffState<IndexDefinition> indexDiff)
    {
        var details = new List<ModificationDetail>();

        AddListDifference(details, nameof(TableDefinition.ConstraintKeys), oldTable.ConstraintKeys, newTable.ConstraintKeys);
        // A table's ReferencedTables/Columns/Types/Functions are aggregated from its columns' types and defaults, which
        // are compared column by column below, so comparing the aggregate only adds false positives (the project and live
        // models spell a column type differently, and only the project derives default references) (G-55, G-58).
        AddListDifference(details, "Columns", oldTable.Columns.Select(static column => column.Name), newTable.Columns.Select(static column => column.Name));
        AddValueDifference(details, nameof(TableDefinition.OfTypeKey), oldTable.OfTypeKey, newTable.OfTypeKey, StringComparison.Ordinal);

        AddNestedChangeSummary(details, "ModifiedColumns", columnDiff.Modified.Select(static change => change.NewNode.Name));
        // A constraint whose only change is its comment is not a change to the table (COMMENT ON CONSTRAINT alone deploys it, G-161).
        AddNestedChangeSummary(details, "ModifiedConstraints", constraintDiff.Modified
            .Where(static change => change.Details.Any(static detail => detail.PropertyName is not (nameof(ConstraintDefinition.Comment) or nameof(ConstraintDefinition.IndexComment))))
            .Select(static change => change.NewNode.Name));
        AddNestedChangeSummary(details, "ModifiedIndexes", indexDiff.Modified.Select(static change => change.NewNode.Name));

        var oldConstraintNames = GetTableConstraints(RequireOldModel(), oldTable, "BuildTableModificationDetails(old)")
            .Select(static constraint => constraint.Name);
        var newConstraintNames = GetTableConstraints(RequireNewModel(), newTable, "BuildTableModificationDetails(new)")
            .Select(static constraint => constraint.Name);
        AddListDifference(details, "Constraints", oldConstraintNames, newConstraintNames);

        var oldIndexNames = GetTableIndexes(RequireOldModel(), oldTable, "BuildTableModificationDetails(old)")
            .Select(static index => index.Name);
        var newIndexNames = GetTableIndexes(RequireNewModel(), newTable, "BuildTableModificationDetails(new)")
            .Select(static index => index.Name);
        AddListDifference(details, "Indexes", oldIndexNames, newIndexNames);

        if (!string.Equals(oldTable.Name, newTable.Name, StringComparison.Ordinal))
        {
            AddOrUpdateDetail(details, nameof(TableDefinition.Name), oldTable.Name, newTable.Name);
        }

        if (!string.Equals(oldTable.SchemaName, newTable.SchemaName, StringComparison.Ordinal))
        {
            AddOrUpdateDetail(details, nameof(TableDefinition.SchemaName), oldTable.SchemaName, newTable.SchemaName);
        }

        AddValueDifference(details, nameof(TableDefinition.Comment), oldTable.Comment, newTable.Comment, StringComparison.Ordinal);
        AddValueDifference(details, nameof(TableDefinition.IsUnlogged), oldTable.IsUnlogged, newTable.IsUnlogged);
        AddValueDifference(details, nameof(TableDefinition.TablespaceName), oldTable.TablespaceName, newTable.TablespaceName, StringComparison.Ordinal);

        // Only when the project states one: the default is the server's default_table_access_method (G-223).
        if (newTable.AccessMethod is not null)
        {
            AddValueDifference(details, nameof(TableDefinition.AccessMethod), oldTable.AccessMethod, newTable.AccessMethod, StringComparison.Ordinal);
        }
        AddValueDifference(details, nameof(TableDefinition.IsRowSecurityEnabled), oldTable.IsRowSecurityEnabled, newTable.IsRowSecurityEnabled);
        AddValueDifference(details, nameof(TableDefinition.IsRowSecurityForced), oldTable.IsRowSecurityForced, newTable.IsRowSecurityForced);

        // A storage parameter's value can be case-sensitive (a quoted string), so this compares exactly as written (G-24b).
        var oldOptions = oldTable.NormalizedStorageOptions.Select(static option => option.ComparisonText).ToArray();
        var newOptions = newTable.NormalizedStorageOptions.Select(static option => option.ComparisonText).ToArray();
        if (!oldOptions.SequenceEqual(newOptions, StringComparer.Ordinal))
        {
            AddOrUpdateDetail(details, nameof(TableDefinition.StorageOptions), oldOptions, newOptions);
        }

        // A structural change here has no in-place ALTER (a changed strategy or key means dropping and recreating the whole
        // partition hierarchy; a changed parent or bound means ATTACH/DETACH PARTITION), so deploy reports these with a
        // fail-closed placeholder rather than attempting them (G-24c, Phase 3 of the entry's plan; the same reasoning as
        // AlterOperation's ordinary-column-becomes-generated case, G-28). Diff's job here is only to report the change.
        AddValueDifference(details, nameof(TableDefinition.PartitionStrategy), oldTable.PartitionStrategy, newTable.PartitionStrategy, StringComparison.OrdinalIgnoreCase);

        // The key's order is significant (PARTITION BY RANGE (a, b) is not PARTITION BY RANGE (b, a)), so this is positional,
        // not a set comparison, the same reasoning as an index's key columns.
        if (!SamePartitionKey(oldTable.PartitionKeyElements, newTable.PartitionKeyElements))
        {
            AddOrUpdateDetail(details, nameof(TableDefinition.PartitionKeyElements), oldTable.PartitionKeyElements.Select(static element => element.ToSql()).ToArray(), newTable.PartitionKeyElements.Select(static element => element.ToSql()).ToArray());
        }

        // The replica identity, only when the project states one (G-255); a table with the default has none.
        if (newTable.ReplicaIdentity is not null)
        {
            AddValueDifference(details, nameof(TableDefinition.ReplicaIdentity), oldTable.ReplicaIdentity ?? "DEFAULT", newTable.ReplicaIdentity, StringComparison.Ordinal);
        }

        // SET WITHOUT CLUSTER (G-260): a change only when the project says so and the live table has a clustered index.
        if (newTable.WithoutCluster
            && (GetTableIndexes(RequireOldModel(), oldTable, "BuildTableModificationDetails(old)").Any(static index => index.IsClustered)
                || GetTableConstraints(RequireOldModel(), oldTable, "BuildTableModificationDetails(old)").Any(static constraint => constraint.IsClustered)))
        {
            AddOrUpdateDetail(details, nameof(TableDefinition.WithoutCluster), false, true);
        }

        // The firing mode of the internal (foreign-key) triggers, only when the project states one (G-261); a live table has it when they are disabled.
        if (newTable.InternalTriggerState is not null)
        {
            AddValueDifference(details, nameof(TableDefinition.InternalTriggerState), oldTable.InternalTriggerState ?? "ENABLE", newTable.InternalTriggerState, StringComparison.Ordinal);
        }

        AddValueDifference(details, nameof(TableDefinition.PartitionOfSchemaName), oldTable.PartitionOfSchemaName, newTable.PartitionOfSchemaName, StringComparison.Ordinal);
        AddValueDifference(details, nameof(TableDefinition.PartitionOfTableName), oldTable.PartitionOfTableName, newTable.PartitionOfTableName, StringComparison.Ordinal);
        var (oldBound, newBound) = TableDefinition.GetComparablePartitionBounds(oldTable, newTable);
        AddValueDifference(details, nameof(TableDefinition.PartitionBoundRaw), oldBound, newBound, StringComparison.Ordinal);

        // The parents of INHERITS, as a set (G-194): a parent added is INHERIT and one removed NO INHERIT, neither of which keeps an order.
        AddListDifference(details, nameof(TableDefinition.InheritsFrom), oldTable.InheritsFrom, newTable.InheritsFrom);

        return details;
    }

    private List<ModificationDetail> BuildColumnModificationDetails(ColumnDefinition oldColumn, ColumnDefinition newColumn)
    {
        var details = new List<ModificationDetail>();

        // Base types are compared without the pg_catalog./public. prefix: the same type spelled two ways (timestamptz
        // versus timestamp with time zone, or a project's public.citext versus the live-read-back unqualified citext)
        // reaches the model as bare and prefixed names (G-45, G-75).
        AddValueDifference(details, nameof(ColumnDefinition.DataType), ColumnTypeNormalizer.NormalizeBaseTypeForColumnComparison(oldColumn.DataType), ColumnTypeNormalizer.NormalizeBaseTypeForColumnComparison(newColumn.DataType), StringComparison.OrdinalIgnoreCase);
        AddValueDifference(details, nameof(ColumnDefinition.RawType), ColumnTypeNormalizer.NormalizeBaseTypeForColumnComparison(oldColumn.RawType), ColumnTypeNormalizer.NormalizeBaseTypeForColumnComparison(newColumn.RawType), StringComparison.OrdinalIgnoreCase);

        // Modifiers, interval fields, and array-ness are part of the type (G-45). Modifier order is significant
        // (precision then scale), and only array-or-not matters to PostgreSQL, not the number of dimensions.
        AddOrderedListDifference(details, nameof(ColumnDefinition.NormalizedTypeModifiers), oldColumn.NormalizedTypeModifiers, newColumn.NormalizedTypeModifiers);
        AddValueDifference(details, nameof(ColumnDefinition.IntervalFields), oldColumn.IntervalFields, newColumn.IntervalFields, StringComparison.OrdinalIgnoreCase);
        AddValueDifference(details, "IsArray", oldColumn.ArrayDimensions > 0, newColumn.ArrayDimensions > 0);
        AddValueDifference(details, nameof(ColumnDefinition.IsNullable), oldColumn.AllowsNull, newColumn.AllowsNull);
        AddValueDifference(details, nameof(ColumnDefinition.NotNullIsNotValid), oldColumn.NotNullIsNotValid, newColumn.NotNullIsNotValid);
        if (!oldColumn.AllowsNull && !newColumn.AllowsNull)
        {
            // PostgreSQL 18 stores a NOT NULL's name. Only a name the project states is compared, and only against one the database has (an older server has none);
            // a different one is a rename in place (G-235).
            if (oldColumn.NotNullNoInherit != newColumn.NotNullNoInherit && oldColumn.InheritedFromTable is null && newColumn.InheritedFromTable is null)
            {
                AddValueDifference(details, nameof(ColumnDefinition.NotNullNoInherit), oldColumn.NotNullNoInherit, newColumn.NotNullNoInherit);
            }

            if (newColumn.NotNullName is not null && oldColumn.NotNullName is not null)
            {
                AddValueDifference(details, nameof(ColumnDefinition.NotNullName), oldColumn.NotNullName, newColumn.NotNullName, StringComparison.Ordinal);
            }

            // The constraint's comment, when the project states one (G-235), as a constraint's is (G-161), and only against a database that has the constraint to
            // carry it: an older server stores no NOT NULL constraint, so there is nothing to comment on.
            if (newColumn.NotNullCommentStated && oldColumn.NotNullName is not null)
            {
                AddValueDifference(details, nameof(ColumnDefinition.NotNullComment), oldColumn.NotNullComment, newColumn.NotNullComment, StringComparison.Ordinal);
            }
        }
        else if (oldColumn.AllowsNull && !newColumn.AllowsNull && newColumn.NotNullCommentStated && newColumn.NotNullComment is not null)
        {
            // The column becomes NOT NULL and the project comments the new constraint: that comment is part of the change.
            AddValueDifference(details, nameof(ColumnDefinition.NotNullComment), null, newColumn.NotNullComment, StringComparison.Ordinal);
        }
        AddValueDifference(details, nameof(ColumnDefinition.IsGenerated), oldColumn.IsGenerated, newColumn.IsGenerated);
        if (oldColumn.IsGenerated && newColumn.IsGenerated)
        {
            // STORED to VIRTUAL or back (PostgreSQL 18; G-121). Beside a change to whether the column is generated at all, that change says it.
            AddValueDifference(details, nameof(ColumnDefinition.IsVirtual), oldColumn.IsVirtual, newColumn.IsVirtual);
        }
        // Compared on the canonical form (PostgreSQL prints a stored expression fully parenthesized), or on PostgreSQL's own
        // rendering of both sides when there is one (G-97); the values shown are as written (G-71).
        var (oldGenerated, newGenerated) = ColumnDefinition.GetComparableGeneratedExpressions(oldColumn, newColumn);
        if (!string.Equals(oldGenerated, newGenerated, StringComparison.Ordinal))
        {
            AddOrUpdateDetail(details, nameof(ColumnDefinition.GeneratedExpression), oldColumn.GeneratedExpression, newColumn.GeneratedExpression);
        }
        // The database names a built-in collation pg_catalog."C" and a project writes "C" (G-156): compared as the same collation, not as text.
        if (!CollationNames.Same(oldColumn.CollationName, newColumn.CollationName))
        {
            AddValueDifference(details, nameof(ColumnDefinition.CollationName), oldColumn.CollationName, newColumn.CollationName, StringComparison.Ordinal);
        }
        AddValueDifference(details, nameof(ColumnDefinition.IdentityGeneration), oldColumn.IdentityGeneration, newColumn.IdentityGeneration, StringComparison.OrdinalIgnoreCase);
        AddValueDifference(details, nameof(ColumnDefinition.Comment), oldColumn.Comment, newColumn.Comment, StringComparison.Ordinal);

        // Only what the project states (G-222).
        if (ColumnDefinition.IsStorageChange(oldColumn, newColumn))
        {
            AddOrUpdateDetail(details, nameof(ColumnDefinition.Storage), oldColumn.Storage, newColumn.Storage);
        }

        if (ColumnDefinition.IsCompressionChange(oldColumn, newColumn))
        {
            AddOrUpdateDetail(details, nameof(ColumnDefinition.Compression), oldColumn.Compression ?? "default", newColumn.Compression);
        }

        // The default is compared in its canonical form, which is equal for PostgreSQL's spelling ('x'::text) and the
        // author's ('x'), and different for a real change (G-56). The expression tree is not compared: it is built
        // from the raw text and would report the cosmetic difference the canonical form removes. When PostgreSQL rendered
        // both defaults, its renderings are compared instead (G-102).
        var (oldDefault, newDefault) = ColumnDefinition.GetComparableDefaultExpressions(oldColumn, newColumn);
        if (!string.Equals(oldDefault, newDefault, StringComparison.Ordinal))
        {
            AddOrUpdateDetail(details, nameof(ColumnDefinition.DefaultExpressionNode), oldDefault, newDefault);
        }

        AddDictionaryDifference(details, nameof(ColumnDefinition.StorageParameters), oldColumn.StorageParameters, newColumn.StorageParameters);

        // ReferencedTables/Columns/Types/Functions of a column are derived from its type and default expression, so a
        // change to either is already reported above. Comparing the derived lists reported a modification for every
        // defaulted column after publish, because the live model does not derive them from the statement text the way
        // the project model does (G-55).
        return details;
    }

    private List<ModificationDetail> BuildConstraintModificationDetails(ConstraintDefinition oldConstraint, ConstraintDefinition newConstraint)
    {
        var details = new List<ModificationDetail>();

        // Only a column rename can pair two names under one key (G-289): the project's generated name and the one the database kept.
        AddValueDifference(details, nameof(ConstraintDefinition.Name), oldConstraint.Name, newConstraint.Name, StringComparison.Ordinal);

        // Only a comment the project states is compared: one only the database has is not a change (G-161).
        if (newConstraint.CommentStated)
        {
            AddValueDifference(details, nameof(ConstraintDefinition.Comment), oldConstraint.Comment, newConstraint.Comment, StringComparison.Ordinal);
        }

        // The same for the comment on a key's or exclusion constraint's index (G-295).
        if (newConstraint.IndexCommentStated)
        {
            AddValueDifference(details, nameof(ConstraintDefinition.IndexComment), oldConstraint.IndexComment, newConstraint.IndexComment, StringComparison.Ordinal);
        }

        AddValueDifference(details, nameof(ConstraintDefinition.ConstraintType), oldConstraint.ConstraintType, newConstraint.ConstraintType, StringComparison.OrdinalIgnoreCase);
        AddValueDifference(details, nameof(ConstraintDefinition.IsNoInherit), oldConstraint.IsNoInherit, newConstraint.IsNoInherit);
        AddListDifference(
            details,
            nameof(ConstraintDefinition.IndexStorageOptions),
            oldConstraint.IndexStorageOptions.Select(static option => option.UnquotedComparisonText),
            newConstraint.IndexStorageOptions.Select(static option => option.UnquotedComparisonText));
        AddValueDifference(details, nameof(ConstraintDefinition.NullsNotDistinct), oldConstraint.NullsNotDistinct, newConstraint.NullsNotDistinct);
        AddOrderedListDifference(details, nameof(ConstraintDefinition.PrimaryKeyColumns), oldConstraint.PrimaryKeyColumns, newConstraint.PrimaryKeyColumns);
        AddOrderedListDifference(details, nameof(ConstraintDefinition.IncludeColumns), oldConstraint.IncludeColumns, newConstraint.IncludeColumns);
        if (oldConstraint.WithoutOverlaps || newConstraint.WithoutOverlaps)
        {
            // A temporal key's last column is the range that may not overlap, so the order matters (G-121); any other UNIQUE is a set (G-112).
            AddOrderedListDifference(details, nameof(ConstraintDefinition.UniqueColumns), oldConstraint.UniqueColumns, newConstraint.UniqueColumns);
        }
        else
        {
            AddListDifference(details, nameof(ConstraintDefinition.UniqueColumns), oldConstraint.UniqueColumns, newConstraint.UniqueColumns);
        }

        AddValueDifference(details, nameof(ConstraintDefinition.ForeignKeyReferencedTable), oldConstraint.ForeignKeyReferencedTable, newConstraint.ForeignKeyReferencedTable, StringComparison.Ordinal);
        AddValueDifference(details, nameof(ConstraintDefinition.ForeignKeyReferencedSchemaName), oldConstraint.ForeignKeyReferencedSchemaName, newConstraint.ForeignKeyReferencedSchemaName, StringComparison.Ordinal);
        AddOrderedListDifference(details, nameof(ConstraintDefinition.ForeignKeyColumns), oldConstraint.ForeignKeyColumns, newConstraint.ForeignKeyColumns);
        AddOrderedListDifference(details, nameof(ConstraintDefinition.ForeignKeyReferencedColumns), oldConstraint.ForeignKeyReferencedColumns, newConstraint.ForeignKeyReferencedColumns);
        AddValueDifference(details, nameof(ConstraintDefinition.ForeignKeyUpdateAction), oldConstraint.ForeignKeyUpdateAction, newConstraint.ForeignKeyUpdateAction, StringComparison.OrdinalIgnoreCase);
        AddValueDifference(details, nameof(ConstraintDefinition.ForeignKeyDeleteAction), oldConstraint.ForeignKeyDeleteAction, newConstraint.ForeignKeyDeleteAction, StringComparison.OrdinalIgnoreCase);
        AddListDifference(details, nameof(ConstraintDefinition.ForeignKeyDeleteSetColumns), oldConstraint.ForeignKeyDeleteSetColumns, newConstraint.ForeignKeyDeleteSetColumns);
        AddValueDifference(details, nameof(ConstraintDefinition.ForeignKeyMatchType), oldConstraint.ForeignKeyMatchType, newConstraint.ForeignKeyMatchType, StringComparison.OrdinalIgnoreCase);
        AddValueDifference(details, nameof(ConstraintDefinition.IsDeferrable), oldConstraint.IsDeferrable, newConstraint.IsDeferrable);
        AddValueDifference(details, nameof(ConstraintDefinition.IsInitiallyDeferred), oldConstraint.IsInitiallyDeferred, newConstraint.IsInitiallyDeferred);
        AddValueDifference(details, nameof(ConstraintDefinition.IsEnforced), oldConstraint.IsEnforced, newConstraint.IsEnforced);

        // The table clustered on, or replicating by, a key's index (G-255, G-256): only what the project states, and only that it becomes so.
        if (newConstraint.IsClustered && !oldConstraint.IsClustered)
        {
            AddValueDifference(details, nameof(ConstraintDefinition.IsClustered), oldConstraint.IsClustered, newConstraint.IsClustered);
        }

        if (newConstraint.IsReplicaIdentity && !oldConstraint.IsReplicaIdentity)
        {
            AddValueDifference(details, nameof(ConstraintDefinition.IsReplicaIdentity), oldConstraint.IsReplicaIdentity, newConstraint.IsReplicaIdentity);
        }

        // Only a constraint the database has not validated and the project states valid is a change (G-234); a validated one satisfies a NOT VALID statement.
        if (oldConstraint.IsNotValid && !newConstraint.IsNotValid)
        {
            AddValueDifference(details, nameof(ConstraintDefinition.IsNotValid), oldConstraint.IsNotValid, newConstraint.IsNotValid);
        }

        AddValueDifference(details, nameof(ConstraintDefinition.WithoutOverlaps), oldConstraint.WithoutOverlaps, newConstraint.WithoutOverlaps);
        AddValueDifference(details, nameof(ConstraintDefinition.ForeignKeyUsesPeriod), oldConstraint.ForeignKeyUsesPeriod, newConstraint.ForeignKeyUsesPeriod);
        AddValueDifference(details, nameof(ConstraintDefinition.ExclusionAccessMethod), oldConstraint.ExclusionAccessMethod, newConstraint.ExclusionAccessMethod, StringComparison.OrdinalIgnoreCase);
        AddValueDifference(details, nameof(ConstraintDefinition.RawExclusionPredicateExpression), oldConstraint.RawExclusionPredicateExpression, newConstraint.RawExclusionPredicateExpression, StringComparison.Ordinal);

        if (!ExclusionElementsEqual(oldConstraint.ExclusionElements, newConstraint.ExclusionElements))
        {
            AddOrUpdateDetail(
                details,
                nameof(ConstraintDefinition.ExclusionElements),
                oldConstraint.ExclusionElements.Select(FormatExclusionElement).ToArray(),
                newConstraint.ExclusionElements.Select(FormatExclusionElement).ToArray());
        }

        // With a parsed tree on both sides the canonical form decides (G-73); the text and its shallow parse only differ by how the
        // expression was written (PostgreSQL prints it fully parenthesized), and would report a difference on every publish.
        // G-172: when PostgreSQL rendered both expressions, compare its renderings; they type each literal and cast each domain column
        // the way the stored expression does.
        if (oldConstraint.ServerRenderedCheck is not null && newConstraint.ServerRenderedCheck is not null)
        {
            if (!string.Equals(oldConstraint.ServerRenderedCheck, newConstraint.ServerRenderedCheck, StringComparison.Ordinal))
            {
                AddOrUpdateDetail(
                    details,
                    nameof(ConstraintDefinition.CheckExpressionNode),
                    new[] { oldConstraint.ServerRenderedCheck },
                    new[] { newConstraint.ServerRenderedCheck });
            }
        }
        else if (oldConstraint.NormalizedCheckExpression is not null && newConstraint.NormalizedCheckExpression is not null)
        {
            if (!string.Equals(oldConstraint.NormalizedCheckExpression, newConstraint.NormalizedCheckExpression, StringComparison.Ordinal))
            {
                AddOrUpdateDetail(
                    details,
                    nameof(ConstraintDefinition.CheckExpressionNode),
                    new[] { oldConstraint.RawCheckExpression ?? oldConstraint.NormalizedCheckExpression },
                    new[] { newConstraint.RawCheckExpression ?? newConstraint.NormalizedCheckExpression });
            }
        }
        else if (!CompareExpressions(oldConstraint.CheckExpressionNode, newConstraint.CheckExpressionNode)
            || !SequenceEqual(oldConstraint.NormalizedExpressions, newConstraint.NormalizedExpressions))
        {
            AddOrUpdateDetail(
                details,
                nameof(ConstraintDefinition.CheckExpressionNode),
                oldConstraint.NormalizedExpressions.Count > 0 ? oldConstraint.NormalizedExpressions : [oldConstraint.RawCheckExpression ?? string.Empty],
                newConstraint.NormalizedExpressions.Count > 0 ? newConstraint.NormalizedExpressions : [newConstraint.RawCheckExpression ?? string.Empty]);
        }

        AddListDifference(details, nameof(ConstraintDefinition.ReferencedTables), oldConstraint.ReferencedTables, newConstraint.ReferencedTables);
        AddListDifference(details, nameof(ConstraintDefinition.ReferencedColumns), oldConstraint.ReferencedColumns, newConstraint.ReferencedColumns);
        AddListDifference(details, nameof(ConstraintDefinition.ReferencedTypes), oldConstraint.ReferencedTypes, newConstraint.ReferencedTypes);
        AddListDifference(details, nameof(ConstraintDefinition.ReferencedFunctions), oldConstraint.ReferencedFunctions, newConstraint.ReferencedFunctions);

        return details;
    }

    private static bool ExclusionElementsEqual(IReadOnlyList<ConstraintExclusionElementDefinition> oldElements, IReadOnlyList<ConstraintExclusionElementDefinition> newElements)
    {
        if (oldElements.Count != newElements.Count)
        {
            return false;
        }

        for (var i = 0; i < oldElements.Count; i++)
        {
            var oldElement = oldElements[i];
            var newElement = newElements[i];

            if (!string.Equals(oldElement.Name, newElement.Name, StringComparison.Ordinal)
                || !string.Equals(oldElement.NormalizedExpression ?? oldElement.RawExpression, newElement.NormalizedExpression ?? newElement.RawExpression, StringComparison.Ordinal)
                || !string.Equals(oldElement.OperatorName, newElement.OperatorName, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string FormatExclusionElement(ConstraintExclusionElementDefinition element)
    {
        var name = element.Name ?? element.RawExpression ?? "<expr>";
        return $"{name} WITH {element.OperatorName}";
    }

    private List<ModificationDetail> BuildIndexModificationDetails(IndexDefinition oldIndex, IndexDefinition newIndex)
    {
        var details = new List<ModificationDetail>();

        // Only a column rename can pair two names under one key (G-289): the project's generated name and the one the database kept.
        AddValueDifference(details, nameof(IndexDefinition.Name), oldIndex.Name, newIndex.Name, StringComparison.Ordinal);

        AddValueDifference(details, nameof(IndexDefinition.AccessMethod), oldIndex.AccessMethod, newIndex.AccessMethod, StringComparison.OrdinalIgnoreCase);
        AddValueDifference(details, nameof(IndexDefinition.IsUnique), oldIndex.IsUnique, newIndex.IsUnique);
        AddValueDifference(details, nameof(IndexDefinition.NullsNotDistinct), oldIndex.NullsNotDistinct, newIndex.NullsNotDistinct);
        AddValueDifference(details, nameof(IndexDefinition.Comment), oldIndex.Comment, newIndex.Comment, StringComparison.Ordinal);
        AddOrderedListDifference(details, nameof(IndexDefinition.KeyColumns), oldIndex.KeyColumns, newIndex.KeyColumns);
        AddOrderedListDifference(details, nameof(IndexDefinition.IncludeColumns), oldIndex.IncludeColumns, newIndex.IncludeColumns);
        // An index a failed CREATE INDEX CONCURRENTLY left INVALID is not finished, whatever its definition (G-271): it is rebuilt.
        AddValueDifference(details, nameof(IndexDefinition.IsValid), oldIndex.IsValid, newIndex.IsValid);
        // An unordered list; the database prints each value quoted (fillfactor='70'), which is the same option (G-195).
        // Only what the project states, and only that it becomes so (G-255, G-256).
        if (newIndex.IsClustered && !oldIndex.IsClustered)
        {
            AddValueDifference(details, nameof(IndexDefinition.IsClustered), oldIndex.IsClustered, newIndex.IsClustered);
        }

        if (newIndex.IsReplicaIdentity && !oldIndex.IsReplicaIdentity)
        {
            AddValueDifference(details, nameof(IndexDefinition.IsReplicaIdentity), oldIndex.IsReplicaIdentity, newIndex.IsReplicaIdentity);
        }

        AddListDifference(details, nameof(IndexDefinition.StorageOptions), oldIndex.StorageOptions.Select(static option => option.UnquotedComparisonText), newIndex.StorageOptions.Select(static option => option.UnquotedComparisonText));

        // The referenced tables/columns/types/functions are derived from the keys and the predicate, which are compared below, so they
        // are not compared on their own (G-100, as for a view in G-96 and a type in G-99): the cast PostgreSQL stores on a literal in
        // the predicate ('x'::s.st) added a referenced type the project's own predicate doesn't name.

        if (!KeyElementsEqual(oldIndex.KeyElements, newIndex.KeyElements))
        {
            AddOrUpdateDetail(
                details,
                nameof(IndexDefinition.KeyElements),
                oldIndex.KeyElements.Select(FormatKeyElement).ToArray(),
                newIndex.KeyElements.Select(FormatKeyElement).ToArray());
        }

        // G-100: when PostgreSQL rendered both predicates, compare its renderings; they type each literal by its column the way
        // the stored predicate does.
        if (oldIndex.ServerRenderedPredicate is not null && newIndex.ServerRenderedPredicate is not null)
        {
            if (!string.Equals(oldIndex.ServerRenderedPredicate, newIndex.ServerRenderedPredicate, StringComparison.Ordinal))
            {
                AddOrUpdateDetail(
                    details,
                    nameof(IndexDefinition.PredicateExpressionNode),
                    new[] { oldIndex.ServerRenderedPredicate },
                    new[] { newIndex.ServerRenderedPredicate });
            }
        }
        else if (oldIndex.NormalizedPredicateExpression is not null && newIndex.NormalizedPredicateExpression is not null)
        {
            if (!string.Equals(oldIndex.NormalizedPredicateExpression, newIndex.NormalizedPredicateExpression, StringComparison.Ordinal))
            {
                AddOrUpdateDetail(
                    details,
                    nameof(IndexDefinition.PredicateExpressionNode),
                    new[] { oldIndex.RawPredicateExpression ?? oldIndex.NormalizedPredicateExpression },
                    new[] { newIndex.RawPredicateExpression ?? newIndex.NormalizedPredicateExpression });
            }
        }
        else if (!CompareExpressions(oldIndex.PredicateExpressionNode, newIndex.PredicateExpressionNode)
            || !SequenceEqual(oldIndex.NormalizedExpressions, newIndex.NormalizedExpressions))
        {
            AddOrUpdateDetail(details, nameof(IndexDefinition.PredicateExpressionNode), oldIndex.NormalizedExpressions, newIndex.NormalizedExpressions);
        }

        return details;
    }

    private static bool KeyElementsEqual(IReadOnlyList<IndexKeyElementDefinition> oldElements, IReadOnlyList<IndexKeyElementDefinition> newElements)
    {
        if (oldElements.Count != newElements.Count)
        {
            return false;
        }

        for (var i = 0; i < oldElements.Count; i++)
        {
            var oldElement = oldElements[i];
            var newElement = newElements[i];

            if (!string.Equals(oldElement.Name, newElement.Name, StringComparison.Ordinal)
                || !string.Equals(
                    oldElement.NormalizedExpression ?? oldElement.RawExpression,
                    newElement.NormalizedExpression ?? newElement.RawExpression,
                    StringComparison.Ordinal)
                || !string.Equals(oldElement.CollationName, newElement.CollationName, StringComparison.Ordinal)
                // A class in pg_catalog or public is compared without that schema, as the live read prints it (G-278).
                || !OperatorClassNames.Same(oldElement.OperatorClassName, newElement.OperatorClassName)
                || oldElement.IsDescending != newElement.IsDescending
                || EffectiveNullsFirst(oldElement) != EffectiveNullsFirst(newElement)
                || !SequenceEqual(oldElement.OperatorClassOptions.Select(OperatorClassOptionComparisonText), newElement.OperatorClassOptions.Select(OperatorClassOptionComparisonText)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Where an index key puts nulls, with the default applied (G-185): first for a descending key, last for an ascending one. PostgreSQL reads a key back
    /// without the clause when it is the default, so <c>score DESC NULLS FIRST</c> and <c>score DESC</c> are one key.
    /// </summary>
    private static bool EffectiveNullsFirst(IndexKeyElementDefinition element)
        => element.NullsFirst ?? element.IsDescending;

    private static string FormatKeyElement(IndexKeyElementDefinition element)
    {
        var name = element.Name ?? element.RawExpression ?? "<expr>";
        var direction = element.IsDescending ? "DESC" : "ASC";
        var nulls = element.NullsFirst is null ? string.Empty : element.NullsFirst.Value ? " NULLS FIRST" : " NULLS LAST";
        var collation = string.IsNullOrWhiteSpace(element.CollationName) ? string.Empty : $" COLLATE {element.CollationName}";
        var opclass = string.IsNullOrWhiteSpace(element.OperatorClassName) ? string.Empty : $" {element.OperatorClassName}";
        return $"{name}{collation}{opclass} {direction}{nulls}";
    }

    private List<ModificationDetail> BuildFunctionModificationDetails(FunctionDefinition oldFunction, FunctionDefinition newFunction)
    {
        var details = new List<ModificationDetail>();

        AddValueDifference(details, nameof(FunctionDefinition.Language), oldFunction.Language, newFunction.Language, StringComparison.OrdinalIgnoreCase);
        AddValueDifference(details, nameof(FunctionDefinition.Comment), oldFunction.Comment, newFunction.Comment, StringComparison.Ordinal);
        AddRoutineSignatureDifferences(details, oldFunction.Signature, newFunction.Signature);

        // G-95: when PostgreSQL rendered both SQL-standard bodies, compare its renderings, as for a view (G-96); the lists below are
        // derived from the body.
        if (oldFunction.ServerRenderedBody is not null && newFunction.ServerRenderedBody is not null)
        {
            AddValueDifference(details, nameof(FunctionDefinition.ServerRenderedBody), oldFunction.ServerRenderedBody, newFunction.ServerRenderedBody, StringComparison.Ordinal);
            return details;
        }

        AddListDifference(details, nameof(FunctionDefinition.ReferencedTables), oldFunction.ReferencedTables, newFunction.ReferencedTables);
        AddListDifference(details, nameof(FunctionDefinition.ReferencedColumns), oldFunction.ReferencedColumns, newFunction.ReferencedColumns);
        AddListDifference(details, nameof(FunctionDefinition.ReferencedTypes), oldFunction.ReferencedTypes, newFunction.ReferencedTypes);
        AddListDifference(details, nameof(FunctionDefinition.ReferencedFunctions), oldFunction.ReferencedFunctions, newFunction.ReferencedFunctions);

        if (!CompareExpressions(oldFunction.BodyExpressionNode, newFunction.BodyExpressionNode)
            || !SequenceEqual(oldFunction.NormalizedExpressions, newFunction.NormalizedExpressions))
        {
            AddOrUpdateDetail(details, nameof(FunctionDefinition.BodyExpressionNode), oldFunction.NormalizedExpressions, newFunction.NormalizedExpressions);
        }

        return details;
    }

    /// <summary>
    /// A routine's parameters, result, and attributes (G-66). The parameters are compared in order, because the order is the call
    /// order; everything is compared in its canonical form (<see cref="RoutineSignatureComparison"/>), so spelling never counts.
    /// The parameters' types are part of the routine's identity, so a change there is a different routine; what is left to detect
    /// here is a parameter's name, mode, or default, the result, and the attributes.
    /// </summary>
    private static void AddRoutineSignatureDifferences(List<ModificationDetail> details, PgProj.Parser.RoutineSignature? oldSignature, PgProj.Parser.RoutineSignature? newSignature)
    {
        var oldParameters = RoutineSignatureComparison.Parameters(oldSignature);
        var newParameters = RoutineSignatureComparison.Parameters(newSignature);
        if (!oldParameters.SequenceEqual(newParameters, StringComparer.Ordinal))
        {
            AddOrUpdateDetail(details, "Signature.Parameters", oldParameters, newParameters);
        }

        AddValueDifference(details, "Signature.Result", RoutineSignatureComparison.Result(oldSignature), RoutineSignatureComparison.Result(newSignature), StringComparison.Ordinal);
        AddValueDifference(details, "Signature.Attributes", RoutineSignatureComparison.Attributes(oldSignature), RoutineSignatureComparison.Attributes(newSignature), StringComparison.Ordinal);
        AddValueDifference(details, "Signature.Configuration", RoutineSignatureComparison.Configuration(oldSignature, newSignature), RoutineSignatureComparison.Configuration(newSignature, oldSignature), StringComparison.Ordinal);
    }

    // Raw verbatim text is intentionally not compared. Only normalized semantics decide whether a
    // routine actually changed, so reformatting alone never produces a deployment operation.
    private List<ModificationDetail> BuildProcedureModificationDetails(ProcedureDefinition oldProcedure, ProcedureDefinition newProcedure)
    {
        var details = new List<ModificationDetail>();

        AddValueDifference(details, nameof(ProcedureDefinition.Language), oldProcedure.Language, newProcedure.Language, StringComparison.OrdinalIgnoreCase);
        AddValueDifference(details, nameof(ProcedureDefinition.Comment), oldProcedure.Comment, newProcedure.Comment, StringComparison.Ordinal);
        AddRoutineSignatureDifferences(details, oldProcedure.Signature, newProcedure.Signature);

        if (oldProcedure.ServerRenderedBody is not null && newProcedure.ServerRenderedBody is not null)
        {
            AddValueDifference(details, nameof(ProcedureDefinition.ServerRenderedBody), oldProcedure.ServerRenderedBody, newProcedure.ServerRenderedBody, StringComparison.Ordinal);
            return details;
        }

        AddListDifference(details, nameof(ProcedureDefinition.ReferencedTables), oldProcedure.ReferencedTables, newProcedure.ReferencedTables);
        AddListDifference(details, nameof(ProcedureDefinition.ReferencedColumns), oldProcedure.ReferencedColumns, newProcedure.ReferencedColumns);
        AddListDifference(details, nameof(ProcedureDefinition.ReferencedTypes), oldProcedure.ReferencedTypes, newProcedure.ReferencedTypes);
        AddListDifference(details, nameof(ProcedureDefinition.ReferencedFunctions), oldProcedure.ReferencedFunctions, newProcedure.ReferencedFunctions);

        if (!CompareExpressions(oldProcedure.BodyExpressionNode, newProcedure.BodyExpressionNode)
            || !SequenceEqual(oldProcedure.NormalizedExpressions, newProcedure.NormalizedExpressions))
        {
            AddOrUpdateDetail(details, nameof(ProcedureDefinition.BodyExpressionNode), oldProcedure.NormalizedExpressions, newProcedure.NormalizedExpressions);
        }

        return details;
    }

    private List<ModificationDetail> BuildPolicyModificationDetails(PolicyDefinition oldPolicy, PolicyDefinition newPolicy)
    {
        var details = new List<ModificationDetail>();

        // Only a comment the project states is compared: one only the database has is not a change (G-164).
        if (newPolicy.CommentStated)
        {
            AddValueDifference(details, nameof(PolicyDefinition.Comment), oldPolicy.Comment, newPolicy.Comment, StringComparison.Ordinal);
        }

        AddValueDifference(details, nameof(PolicyDefinition.TableName), oldPolicy.TableName, newPolicy.TableName, StringComparison.Ordinal);
        AddValueDifference(details, nameof(PolicyDefinition.Command), oldPolicy.Command, newPolicy.Command, StringComparison.OrdinalIgnoreCase);
        AddValueDifference(details, nameof(PolicyDefinition.IsPermissive), oldPolicy.IsPermissive, newPolicy.IsPermissive);
        AddListDifference(details, nameof(PolicyDefinition.Roles), oldPolicy.NormalizedRoles, newPolicy.NormalizedRoles);

        // Compared on PostgreSQL's own rendering of both when there is one (G-245), else on the canonical form; the values shown are the expressions as written (the same
        // reasoning as a trigger's WHEN).
        var sameUsing = oldPolicy.ServerRenderedUsing is not null && newPolicy.ServerRenderedUsing is not null
            ? string.Equals(oldPolicy.ServerRenderedUsing, newPolicy.ServerRenderedUsing, StringComparison.Ordinal)
            : string.Equals(oldPolicy.NormalizedUsingExpression, newPolicy.NormalizedUsingExpression, StringComparison.Ordinal);
        if (!sameUsing)
        {
            AddOrUpdateDetail(details, "UsingExpression", oldPolicy.RawUsingExpression, newPolicy.RawUsingExpression);
        }

        var sameWithCheck = oldPolicy.ServerRenderedWithCheck is not null && newPolicy.ServerRenderedWithCheck is not null
            ? string.Equals(oldPolicy.ServerRenderedWithCheck, newPolicy.ServerRenderedWithCheck, StringComparison.Ordinal)
            : string.Equals(oldPolicy.NormalizedWithCheckExpression, newPolicy.NormalizedWithCheckExpression, StringComparison.Ordinal);
        if (!sameWithCheck)
        {
            AddOrUpdateDetail(details, "WithCheckExpression", oldPolicy.RawWithCheckExpression, newPolicy.RawWithCheckExpression);
        }

        return details;
    }

    /// <summary>
    /// A trigger's function named without a schema is found through the default <c>search_path</c> (<c>public</c>, <c>pg_catalog</c>), so
    /// <c>tf</c>, <c>public.tf</c>, and <c>pg_catalog.x</c> against <c>x</c> are the same function for comparison (G-114, the rule G-63
    /// applies to defaults). A function in any other schema keeps its qualifier.
    /// </summary>
    private static string? WithoutSearchPathSchema(string? functionName)
    {
        if (functionName is null)
        {
            return null;
        }

        foreach (var schema in (string[])["public.", "pg_catalog.", "\"public\".", "\"pg_catalog\"."])
        {
            if (functionName.StartsWith(schema, StringComparison.OrdinalIgnoreCase))
            {
                return functionName[schema.Length..];
            }
        }

        return functionName;
    }

    /// <summary>
    /// What differs between two operators of one name and pair of operand types (G-129): the function it runs, its commutator and negator, its estimator
    /// functions, and whether it hashes and merges. A function or estimator is compared without a leading <c>public.</c> or <c>pg_catalog.</c>, which a live read does not write.
    /// </summary>
    private List<ModificationDetail> BuildOperatorModificationDetails(OperatorDefinition oldOperator, OperatorDefinition newOperator)
    {
        var details = new List<ModificationDetail>();

        AddValueDifference(details, nameof(OperatorDefinition.FunctionName), WithoutSearchPathSchema(oldOperator.FunctionName), WithoutSearchPathSchema(newOperator.FunctionName), StringComparison.Ordinal);
        AddValueDifference(details, nameof(OperatorDefinition.Commutator), WithoutSearchPathSchema(oldOperator.Commutator), WithoutSearchPathSchema(newOperator.Commutator), StringComparison.Ordinal);
        AddValueDifference(details, nameof(OperatorDefinition.Negator), WithoutSearchPathSchema(oldOperator.Negator), WithoutSearchPathSchema(newOperator.Negator), StringComparison.Ordinal);
        AddValueDifference(details, nameof(OperatorDefinition.RestrictFunction), WithoutSearchPathSchema(oldOperator.RestrictFunction), WithoutSearchPathSchema(newOperator.RestrictFunction), StringComparison.Ordinal);
        AddValueDifference(details, nameof(OperatorDefinition.JoinFunction), WithoutSearchPathSchema(oldOperator.JoinFunction), WithoutSearchPathSchema(newOperator.JoinFunction), StringComparison.Ordinal);
        AddValueDifference(details, nameof(OperatorDefinition.IsHashes), oldOperator.IsHashes.ToString(), newOperator.IsHashes.ToString(), StringComparison.OrdinalIgnoreCase);
        AddValueDifference(details, nameof(OperatorDefinition.IsMerges), oldOperator.IsMerges.ToString(), newOperator.IsMerges.ToString(), StringComparison.OrdinalIgnoreCase);

        return details;
    }

    /// <summary>
    /// What differs between two rules of one name on one relation (G-128): the definition. PostgreSQL rewrites a rule when it stores it, so when both rules have the
    /// server's rendering of them (<see cref="RuleDefinition.ServerRenderedDefinition"/>) they are compared by that, and a rule written another way from how
    /// PostgreSQL prints it is not a change; otherwise by the text in its normal form. The event and <c>INSTEAD</c> are part of the definition.
    /// </summary>
    private List<ModificationDetail> BuildRuleModificationDetails(RuleDefinition oldRule, RuleDefinition newRule)
    {
        var details = new List<ModificationDetail>();

        // The firing mode, only when the project states one (G-257).
        if (newRule.EnabledState is not null)
        {
            AddValueDifference(details, nameof(RuleDefinition.EnabledState), oldRule.EnabledState ?? "ENABLE", newRule.EnabledState, StringComparison.Ordinal);
        }

        if (oldRule.ServerRenderedDefinition is not null && newRule.ServerRenderedDefinition is not null)
        {
            AddValueDifference(details, nameof(RuleDefinition.ServerRenderedDefinition), oldRule.ServerRenderedDefinition, newRule.ServerRenderedDefinition, StringComparison.Ordinal);
        }
        else
        {
            AddValueDifference(details, nameof(RuleDefinition.NormalizedDefinition), oldRule.NormalizedDefinition, newRule.NormalizedDefinition, StringComparison.OrdinalIgnoreCase);
        }

        return details;
    }

    /// <summary>
    /// What differs between two aggregates of one name and argument types (G-130): whether it is an ordered-set aggregate, and each option in comparison form
    /// (<see cref="AggregateDefinition.NormalizedOptions"/>: PostgreSQL's defaults left out, so an option one side states and the other leaves at its default is not a
    /// change). A function, or the sort operator, is compared without a leading <c>public.</c> or <c>pg_catalog.</c>, which a live read does not write.
    /// </summary>
    private List<ModificationDetail> BuildAggregateModificationDetails(AggregateDefinition oldAggregate, AggregateDefinition newAggregate)
    {
        var details = new List<ModificationDetail>();

        AddValueDifference(details, nameof(AggregateDefinition.DirectArgumentCount), oldAggregate.DirectArgumentCount.ToString(System.Globalization.CultureInfo.InvariantCulture), newAggregate.DirectArgumentCount.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        foreach (var option in oldAggregate.NormalizedOptions.Keys.Union(newAggregate.NormalizedOptions.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            oldAggregate.NormalizedOptions.TryGetValue(option, out var oldValue);
            newAggregate.NormalizedOptions.TryGetValue(option, out var newValue);
            var isName = AggregateOptions.FunctionOptions.Contains(option) || option == "sortop";
            AddValueDifference(details, "Option:" + option, isName ? WithoutSearchPathSchema(oldValue) : oldValue, isName ? WithoutSearchPathSchema(newValue) : newValue, StringComparison.OrdinalIgnoreCase);
        }

        return details;
    }

    /// <summary>What differs between two text search dictionaries of one name (G-149): the template and each option, by name.</summary>
    private List<ModificationDetail> BuildTextSearchDictionaryModificationDetails(TextSearchDictionaryDefinition oldDictionary, TextSearchDictionaryDefinition newDictionary)
    {
        var details = new List<ModificationDetail>();

        AddValueDifference(details, nameof(TextSearchDictionaryDefinition.Template), oldDictionary.NormalizedTemplate, newDictionary.NormalizedTemplate, StringComparison.OrdinalIgnoreCase);
        foreach (var option in oldDictionary.Options.Keys.Union(newDictionary.Options.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            oldDictionary.Options.TryGetValue(option, out var oldValue);
            newDictionary.Options.TryGetValue(option, out var newValue);
            AddValueDifference(details, "Option:" + option, oldValue, newValue, StringComparison.Ordinal);
        }

        AddValueDifference(details, nameof(TextSearchDictionaryDefinition.Comment), oldDictionary.Comment, newDictionary.Comment, StringComparison.Ordinal);
        return details;
    }

    /// <summary>
    /// What differs between two text search configurations of one name (G-149): the parser and each token type's dictionaries, in the order they are tried. For a
    /// configuration the project makes as a <c>COPY</c> (always the new side) the source's own mappings are not known, so the parser is not compared and only the token
    /// types the project states are.
    /// </summary>
    private List<ModificationDetail> BuildTextSearchConfigurationModificationDetails(TextSearchConfigurationDefinition oldConfiguration, TextSearchConfigurationDefinition newConfiguration)
    {
        var details = new List<ModificationDetail>();

        if (!newConfiguration.IsCopy)
        {
            AddValueDifference(details, nameof(TextSearchConfigurationDefinition.Parser), oldConfiguration.NormalizedParser, newConfiguration.NormalizedParser, StringComparison.OrdinalIgnoreCase);
        }

        var tokens = newConfiguration.IsCopy
            ? newConfiguration.NormalizedMappings.Keys
            : oldConfiguration.NormalizedMappings.Keys.Union(newConfiguration.NormalizedMappings.Keys, StringComparer.Ordinal);
        foreach (var token in tokens.Order(StringComparer.Ordinal))
        {
            AddValueDifference(details, "Mapping:" + token, Describe(oldConfiguration, token), Describe(newConfiguration, token), StringComparison.OrdinalIgnoreCase);
        }

        AddValueDifference(details, nameof(TextSearchConfigurationDefinition.Comment), oldConfiguration.Comment, newConfiguration.Comment, StringComparison.Ordinal);
        return details;

        static string Describe(TextSearchConfigurationDefinition configuration, string token)
            => configuration.NormalizedMappings.TryGetValue(token, out var dictionaries) && dictionaries.Count > 0 ? string.Join(", ", dictionaries) : "(none)";
    }

    /// <summary>
    /// What differs between two extended statistics objects of one name (G-150): the table, the kinds in comparison form (sorted; all three when none is written), the
    /// columns as a set (PostgreSQL stores them in the table's column order), and the expressions in comparison form.
    /// </summary>
    private List<ModificationDetail> BuildStatisticsModificationDetails(StatisticsDefinition oldStatistics, StatisticsDefinition newStatistics)
    {
        var details = new List<ModificationDetail>();

        AddValueDifference(details, nameof(StatisticsDefinition.TableName), $"{oldStatistics.TableSchemaName}.{oldStatistics.TableName}", $"{newStatistics.TableSchemaName}.{newStatistics.TableName}", StringComparison.Ordinal);
        AddValueDifference(details, nameof(StatisticsDefinition.Kinds), string.Join(", ", oldStatistics.NormalizedKinds), string.Join(", ", newStatistics.NormalizedKinds), StringComparison.Ordinal);
        AddValueDifference(details, "Columns", ColumnSet(oldStatistics), ColumnSet(newStatistics), StringComparison.Ordinal);
        AddValueDifference(details, "Expressions", ExpressionSet(oldStatistics), ExpressionSet(newStatistics), StringComparison.Ordinal);
        AddValueDifference(details, nameof(StatisticsDefinition.Comment), oldStatistics.Comment, newStatistics.Comment, StringComparison.Ordinal);

        return details;

        static string ColumnSet(StatisticsDefinition statistics)
            => string.Join(", ", statistics.Elements.Where(static element => element.Name is not null).Select(static element => element.Name!).Order(PgProj.Parser.SqlIdentifiers.NameComparer));

        static string ExpressionSet(StatisticsDefinition statistics)
            => string.Join(", ", statistics.Elements.Where(static element => element.Name is null).Select(static element => element.NormalizedExpression ?? element.RawExpression ?? string.Empty).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// What differs between two collations of one name (G-148): the provider, the locale (an ICU one in the form two spellings share,
    /// <see cref="CollationLocales.Canonical"/>), a libc collation's <c>LC_COLLATE</c> and <c>LC_CTYPE</c>, whether it is deterministic, and its rules.
    /// </summary>
    private List<ModificationDetail> BuildCollationModificationDetails(CollationDefinition oldCollation, CollationDefinition newCollation)
    {
        var details = new List<ModificationDetail>();

        AddValueDifference(details, nameof(CollationDefinition.Provider), oldCollation.Provider, newCollation.Provider, StringComparison.OrdinalIgnoreCase);
        AddValueDifference(details, nameof(CollationDefinition.Locale), CollationLocales.Canonical(oldCollation.Provider, oldCollation.Locale), CollationLocales.Canonical(newCollation.Provider, newCollation.Locale), StringComparison.Ordinal);
        AddValueDifference(details, nameof(CollationDefinition.LcCollate), oldCollation.LcCollate, newCollation.LcCollate, StringComparison.Ordinal);
        AddValueDifference(details, nameof(CollationDefinition.LcCtype), oldCollation.LcCtype, newCollation.LcCtype, StringComparison.Ordinal);
        AddValueDifference(details, nameof(CollationDefinition.IsDeterministic), oldCollation.IsDeterministic.ToString(), newCollation.IsDeterministic.ToString(), StringComparison.OrdinalIgnoreCase);
        AddValueDifference(details, nameof(CollationDefinition.Rules), oldCollation.Rules, newCollation.Rules, StringComparison.Ordinal);
        AddValueDifference(details, nameof(CollationDefinition.Comment), oldCollation.Comment, newCollation.Comment, StringComparison.Ordinal);

        return details;
    }

    /// <summary>
    /// What differs between two publications of one name (G-134): whether it is <c>FOR ALL TABLES</c>, each option in comparison form (the operations it publishes as a
    /// set, PostgreSQL's defaults left out), the schemas it names, and the tables it names, each with its column list (a set) and its row filter (in comparison form).
    /// </summary>
    private List<ModificationDetail> BuildPublicationModificationDetails(PublicationDefinition oldPublication, PublicationDefinition newPublication)
    {
        var details = new List<ModificationDetail>();

        AddValueDifference(details, nameof(PublicationDefinition.IsAllTables), oldPublication.IsAllTables.ToString(), newPublication.IsAllTables.ToString(), StringComparison.OrdinalIgnoreCase);
        foreach (var option in oldPublication.NormalizedOptions.Keys.Union(newPublication.NormalizedOptions.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            oldPublication.NormalizedOptions.TryGetValue(option, out var oldValue);
            newPublication.NormalizedOptions.TryGetValue(option, out var newValue);
            AddValueDifference(details, "Option:" + option, oldValue, newValue, StringComparison.OrdinalIgnoreCase);
        }

        AddValueDifference(
            details,
            nameof(PublicationDefinition.Schemas),
            string.Join(",", oldPublication.Schemas.Order(PgProj.Parser.SqlIdentifiers.NameComparer)),
            string.Join(",", newPublication.Schemas.Order(PgProj.Parser.SqlIdentifiers.NameComparer)),
            StringComparison.Ordinal);

        var oldTables = oldPublication.Tables.ToDictionary(static table => table.Key, PgProj.Parser.SqlIdentifiers.NameComparer);
        var newTables = newPublication.Tables.ToDictionary(static table => table.Key, PgProj.Parser.SqlIdentifiers.NameComparer);
        AddValueDifference(
            details,
            nameof(PublicationDefinition.Tables),
            string.Join(",", oldTables.Keys.Order(PgProj.Parser.SqlIdentifiers.NameComparer)),
            string.Join(",", newTables.Keys.Order(PgProj.Parser.SqlIdentifiers.NameComparer)),
            StringComparison.Ordinal);
        foreach (var (key, oldTable) in oldTables)
        {
            if (newTables.TryGetValue(key, out var newTable))
            {
                AddValueDifference(details, $"Table:{key}:Columns", string.Join(",", oldTable.NormalizedColumns), string.Join(",", newTable.NormalizedColumns), StringComparison.Ordinal);
                AddValueDifference(details, $"Table:{key}:Filter", oldTable.NormalizedFilter, newTable.NormalizedFilter, StringComparison.OrdinalIgnoreCase);
            }
        }

        return details;
    }

    /// <summary>
    /// What differs between two casts of one pair of types (G-131): the method (function, binary, or in/out), the function and its argument types
    /// (compared in comparison form and in order, without a leading <c>public.</c> on the name), and when the cast applies.
    /// </summary>
    private List<ModificationDetail> BuildCastModificationDetails(CastDefinition oldCast, CastDefinition newCast)
    {
        var details = new List<ModificationDetail>();

        AddValueDifference(details, nameof(CastDefinition.Method), oldCast.Method, newCast.Method, StringComparison.OrdinalIgnoreCase);
        AddValueDifference(details, nameof(CastDefinition.FunctionName), WithoutSearchPathSchema(oldCast.FunctionName), WithoutSearchPathSchema(newCast.FunctionName), StringComparison.Ordinal);
        AddOrderedListDifference(details, nameof(CastDefinition.FunctionArguments), oldCast.NormalizedFunctionArguments, newCast.NormalizedFunctionArguments);
        AddValueDifference(details, nameof(CastDefinition.Context), oldCast.Context, newCast.Context, StringComparison.OrdinalIgnoreCase);

        return details;
    }

    /// <summary>
    /// What differs between two event triggers of one name (G-127): the event, the tag filter (a set), the function it runs (compared without a
    /// leading <c>public.</c>, as a table trigger's is), and when it fires.
    /// </summary>
    private List<ModificationDetail> BuildSchemaModificationDetails(SchemaDefinition oldSchema, SchemaDefinition newSchema)
    {
        var details = new List<ModificationDetail>();
        // A comment is compared only where the project states one (G-160); a schema it only declares keeps the database's (G-284).
        if (newSchema.IsCommentStated)
        {
            AddValueDifference(details, nameof(SchemaDefinition.Comment), oldSchema.Comment, newSchema.Comment, StringComparison.Ordinal);
        }

        return details;
    }

    private List<ModificationDetail> BuildOwnerModificationDetails(OwnerDefinition oldOwner, OwnerDefinition newOwner)
    {
        var details = new List<ModificationDetail>();

        AddValueDifference(details, nameof(OwnerDefinition.OwnerRole), oldOwner.OwnerRole, newOwner.OwnerRole, StringComparison.Ordinal);

        return details;
    }

    private List<ModificationDetail> BuildEventTriggerModificationDetails(EventTriggerDefinition oldTrigger, EventTriggerDefinition newTrigger)
    {
        var details = new List<ModificationDetail>();

        AddValueDifference(details, nameof(EventTriggerDefinition.EventName), oldTrigger.EventName, newTrigger.EventName, StringComparison.OrdinalIgnoreCase);
        AddListDifference(details, nameof(EventTriggerDefinition.Tags), oldTrigger.NormalizedTags, newTrigger.NormalizedTags);
        AddValueDifference(details, nameof(EventTriggerDefinition.FunctionName), WithoutSearchPathSchema(oldTrigger.FunctionName), WithoutSearchPathSchema(newTrigger.FunctionName), StringComparison.Ordinal);
        AddValueDifference(details, nameof(EventTriggerDefinition.EnabledMode), oldTrigger.EnabledMode, newTrigger.EnabledMode, StringComparison.OrdinalIgnoreCase);

        return details;
    }

    private List<ModificationDetail> BuildTriggerModificationDetails(TriggerDefinition oldTrigger, TriggerDefinition newTrigger)
    {
        var details = new List<ModificationDetail>();

        // Only a comment the project states is compared: one only the database has is not a change (G-163).
        if (newTrigger.CommentStated)
        {
            AddValueDifference(details, nameof(TriggerDefinition.Comment), oldTrigger.Comment, newTrigger.Comment, StringComparison.Ordinal);
        }

        // The firing mode, only when the project states one (G-257); a trigger the database has enabled is the default, which has no state.
        if (newTrigger.EnabledState is not null)
        {
            AddValueDifference(details, nameof(TriggerDefinition.EnabledState), oldTrigger.EnabledState ?? "ENABLE", newTrigger.EnabledState, StringComparison.Ordinal);
        }

        // The firing mode of its copy on each partition the project states one for (G-293), compared one way as the trigger's own is.
        var partitionStateChanges = newTrigger.PartitionStates.Where(pair => !string.Equals(oldTrigger.PartitionStates.GetValueOrDefault(pair.Key) ?? "ENABLE", pair.Value, StringComparison.Ordinal)).ToArray();
        if (partitionStateChanges.Length > 0)
        {
            details.Add(new ModificationDetail
            {
                PropertyName = nameof(TriggerDefinition.PartitionStates),
                OldValue = string.Join("; ", partitionStateChanges.Select(pair => $"{pair.Key}: {oldTrigger.PartitionStates.GetValueOrDefault(pair.Key) ?? "ENABLE"}")),
                NewValue = string.Join("; ", partitionStateChanges.Select(pair => $"{pair.Key}: {pair.Value}")),
            });
        }

        AddValueDifference(details, nameof(TriggerDefinition.TableName), oldTrigger.TableName, newTrigger.TableName, StringComparison.Ordinal);
        AddValueDifference(details, nameof(TriggerDefinition.FunctionName), WithoutSearchPathSchema(oldTrigger.FunctionName), WithoutSearchPathSchema(newTrigger.FunctionName), StringComparison.Ordinal);
        AddValueDifference(details, nameof(TriggerDefinition.Timing), oldTrigger.Timing, newTrigger.Timing, StringComparison.OrdinalIgnoreCase);
        AddListDifference(details, nameof(TriggerDefinition.Events), oldTrigger.Events, newTrigger.Events);

        AddValueDifference(details, nameof(TriggerDefinition.IsConstraintTrigger), oldTrigger.IsConstraintTrigger, newTrigger.IsConstraintTrigger);
        AddValueDifference(details, nameof(TriggerDefinition.IsDeferrable), oldTrigger.IsDeferrable, newTrigger.IsDeferrable);
        AddValueDifference(details, nameof(TriggerDefinition.IsInitiallyDeferred), oldTrigger.IsInitiallyDeferred, newTrigger.IsInitiallyDeferred);
        AddValueDifference(details, nameof(TriggerDefinition.ConstraintTableName), oldTrigger.ConstraintTableName, newTrigger.ConstraintTableName, StringComparison.Ordinal);
        AddListDifference(details, nameof(TriggerDefinition.UpdateColumns), oldTrigger.UpdateColumns, newTrigger.UpdateColumns);
        // Positional, and literals rather than identifiers: 'X' and 'x' are different arguments.
        if (!oldTrigger.FunctionArguments.SequenceEqual(newTrigger.FunctionArguments, StringComparer.Ordinal))
        {
            AddOrUpdateDetail(details, nameof(TriggerDefinition.FunctionArguments), oldTrigger.FunctionArguments, newTrigger.FunctionArguments);
        }
        AddValueDifference(details, nameof(TriggerDefinition.OldTransitionTable), oldTrigger.OldTransitionTable, newTrigger.OldTransitionTable, StringComparison.Ordinal);
        AddValueDifference(details, nameof(TriggerDefinition.NewTransitionTable), oldTrigger.NewTransitionTable, newTrigger.NewTransitionTable, StringComparison.Ordinal);

        // Compared on the canonical form; the values shown are the conditions as written.
        if (!string.Equals(oldTrigger.NormalizedWhenCondition, newTrigger.NormalizedWhenCondition, StringComparison.Ordinal))
        {
            AddOrUpdateDetail(details, "WhenCondition", oldTrigger.RawWhenCondition, newTrigger.RawWhenCondition);
        }

        if (oldTrigger.IsRowLevel != newTrigger.IsRowLevel)
        {
            AddOrUpdateDetail(
                details,
                nameof(TriggerDefinition.IsRowLevel),
                oldTrigger.IsRowLevel,
                newTrigger.IsRowLevel);
        }

        return details;
    }

    /// <summary>The comments on a view's or a composite type's columns (G-216), compared as table column comments are: always, and exactly.</summary>
    private static void AddColumnCommentDifference(List<ModificationDetail> details, IReadOnlyDictionary<string, string> oldComments, IReadOnlyDictionary<string, string> newComments)
    {
        if (!ColumnCommentMap.AreEqual(oldComments, newComments))
        {
            details.Add(new ModificationDetail { PropertyName = nameof(ViewDefinition.ColumnComments), OldValue = ColumnCommentMap.Describe(oldComments), NewValue = ColumnCommentMap.Describe(newComments) });
        }
    }

    private List<ModificationDetail> BuildViewModificationDetails(ViewDefinition oldView, ViewDefinition newView)
    {
        var details = new List<ModificationDetail>();

        AddValueDifference(details, nameof(ViewDefinition.IsMaterialized), oldView.IsMaterialized, newView.IsMaterialized);
        AddValueDifference(details, nameof(ViewDefinition.Comment), oldView.Comment, newView.Comment, StringComparison.Ordinal);
        AddColumnCommentDifference(details, oldView.ColumnComments, newView.ColumnComments);

        // The defaults of a view's columns are compared as a table column's are (always), in their comparison form; the values shown are as written (G-291).
        if (!ColumnCommentMap.AreEqual(oldView.NormalizedColumnDefaults, newView.NormalizedColumnDefaults))
        {
            details.Add(new ModificationDetail { PropertyName = nameof(ViewDefinition.ColumnDefaults), OldValue = ColumnCommentMap.Describe(oldView.ColumnDefaults), NewValue = ColumnCommentMap.Describe(newView.ColumnDefaults) });
        }

        // A materialized view's storage parameters are a set, compared as PostgreSQL stores them (G-215).
        AddListDifference(
            details,
            nameof(ViewDefinition.StorageOptions),
            oldView.StorageOptions.Select(static option => option.UnquotedComparisonText).Order(StringComparer.Ordinal),
            newView.StorageOptions.Select(static option => option.UnquotedComparisonText).Order(StringComparer.Ordinal));

        // A materialized view's tablespace as a table's is; its access method only when the project states one, the default being the server's (G-219).
        AddValueDifference(details, nameof(ViewDefinition.TablespaceName), oldView.TablespaceName, newView.TablespaceName, StringComparison.Ordinal);
        if (newView.AccessMethod is not null)
        {
            AddValueDifference(details, nameof(ViewDefinition.AccessMethod), oldView.AccessMethod, newView.AccessMethod, StringComparison.Ordinal);
        }

        // G-180: the options are sorted and canonical already (ViewOptions), so they are compared exactly, in order.
        if (!oldView.Options.SequenceEqual(newView.Options, StringComparer.Ordinal))
        {
            AddOrUpdateDetail(details, nameof(ViewDefinition.Options), oldView.Options, newView.Options);
        }

        // G-96: when PostgreSQL rendered both bodies, compare its renderings; they already account for everything the
        // parsed body and the lists derived from it would, and without PostgreSQL's own rewrites of the view.
        if (oldView.ServerRenderedBody is not null && newView.ServerRenderedBody is not null)
        {
            AddValueDifference(details, nameof(ViewDefinition.ServerRenderedBody), oldView.ServerRenderedBody, newView.ServerRenderedBody, StringComparison.Ordinal);
            return details;
        }

        // The referenced tables/columns/types/functions are derived from the body, which is compared below, so they are
        // not compared on their own (G-96); on their own they only differed where binding read the two spellings of one
        // body differently.
        AddListDifference(details, nameof(ViewDefinition.OutputColumnNames), oldView.OutputColumnNames, newView.OutputColumnNames);
        AddListDifference(details, nameof(ViewDefinition.OutputColumnTypes), oldView.OutputColumnTypes, newView.OutputColumnTypes);

        if (!CompareExpressions(oldView.SelectExpressionNode, newView.SelectExpressionNode)
            || !SequenceEqual(oldView.NormalizedExpressions, newView.NormalizedExpressions))
        {
            AddOrUpdateDetail(details, nameof(ViewDefinition.SelectExpressionNode), oldView.NormalizedExpressions, newView.NormalizedExpressions);
        }

        return details;
    }

    private List<ModificationDetail> BuildTypeModificationDetails(TypeDefinition oldType, TypeDefinition newType)
    {
        var details = new List<ModificationDetail>();

        // An extension's schema, version, and comment are compared only when the project states them (G-213): otherwise PostgreSQL chose the schema,
        // any version will do, and the extension's own comment stands. Nothing else about it is the project's.
        if (string.Equals(newType.TypeKind, "extension", StringComparison.Ordinal))
        {
            if (newType.ExtensionSchemaStated)
            {
                AddValueDifference(details, nameof(TypeDefinition.SchemaName), oldType.SchemaName, newType.SchemaName, StringComparison.Ordinal);
            }

            if (newType.ExtensionVersion is not null)
            {
                AddValueDifference(details, nameof(TypeDefinition.ExtensionVersion), oldType.ExtensionVersion, newType.ExtensionVersion, StringComparison.Ordinal);
            }

            if (newType.CommentStated)
            {
                AddValueDifference(details, nameof(TypeDefinition.Comment), oldType.Comment, newType.Comment, StringComparison.Ordinal);
            }

            return details;
        }

        AddValueDifference(details, nameof(TypeDefinition.TypeKind), oldType.TypeKind, newType.TypeKind, StringComparison.OrdinalIgnoreCase);
        AddValueDifference(details, nameof(TypeDefinition.Comment), oldType.Comment, newType.Comment, StringComparison.Ordinal);
        AddColumnCommentDifference(details, oldType.ColumnComments, newType.ColumnComments);
        AddListDifference(details, nameof(TypeDefinition.CompositeFields), FormatCompositeFields(oldType.CompositeFields), FormatCompositeFields(newType.CompositeFields));
        AddListDifference(details, nameof(TypeDefinition.RangeOptions), FormatRangeOptions(oldType.RangeOptions, oldType.Name), FormatRangeOptions(newType.RangeOptions, newType.Name));
        AddOrderedListDifference(details, nameof(TypeDefinition.EnumLabels), oldType.EnumLabels, newType.EnumLabels);

        // An enum with no labels and a composite with no fields have nothing above to tell them apart (G-249, G-268); their form does. A type with any structure
        // already shows its change there, so this is reported only when the structure says nothing.
        if (details.Count == 0 && !string.Equals(EmptyTypeForm(oldType), EmptyTypeForm(newType), StringComparison.Ordinal))
        {
            AddValueDifference(details, "Form", EmptyTypeForm(oldType), EmptyTypeForm(newType), StringComparison.Ordinal);
        }

        // The referenced tables/columns/types/functions are derived from the structure compared here (composite fields, a domain's
        // base type and CHECKs, a range's subtype), so they are not compared on their own (G-99, as for a view in G-96). The type
        // list is derived from the field text, so it differed for every field PostgreSQL spells differently (varchar against
        // character varying) even when the field itself matched.
        // Exactly: "Mytype" and mytype are two types (G-286); the parser has already folded an unquoted name.
        AddValueDifference(details, nameof(TypeDefinition.DomainBaseType), ColumnTypeNormalizer.NormalizeBaseTypeForColumnComparison(oldType.DomainBaseType), ColumnTypeNormalizer.NormalizeBaseTypeForColumnComparison(newType.DomainBaseType), StringComparison.Ordinal);
        AddOrderedListDifference(details, nameof(TypeDefinition.DomainBaseTypeModifiers), oldType.DomainBaseTypeModifiers, newType.DomainBaseTypeModifiers);
        AddValueDifference(details, nameof(TypeDefinition.DomainBaseTypeArrayDimensions), oldType.DomainBaseTypeArrayDimensions, newType.DomainBaseTypeArrayDimensions);
        AddValueDifference(details, nameof(TypeDefinition.DomainIntervalFields), oldType.DomainIntervalFields, newType.DomainIntervalFields, StringComparison.OrdinalIgnoreCase);
        AddValueDifference(details, nameof(TypeDefinition.DomainIsNotNull), oldType.DomainIsNotNull, newType.DomainIsNotNull);
        if (!CollationNames.Same(oldType.DomainCollationName, newType.DomainCollationName))
        {
            // The database names the collation with its schema (pg_catalog."C") and leaves out the base type's own (G-196).
            AddValueDifference(details, nameof(TypeDefinition.DomainCollationName), oldType.DomainCollationName, newType.DomainCollationName, StringComparison.Ordinal);
        }
        AddValueDifference(details, nameof(TypeDefinition.DomainRawDefaultExpression), oldType.GetComparableDomainDefaultExpression(), newType.GetComparableDomainDefaultExpression(), StringComparison.Ordinal);
        // Compared as PostgreSQL stores them when the server rendered every check of both sides (G-288): it adds casts the written text lacks.
        var rendered = AllRendered(oldType) && AllRendered(newType);
        AddListDifference(details, nameof(TypeDefinition.DomainCheckConstraints), FormatDomainCheckConstraints(oldType, rendered: rendered), FormatDomainCheckConstraints(newType, rendered: rendered));

        // A check the database has not validated and the project states valid is a change (G-233); a validated one satisfies a NOT VALID statement.
        var newValid = FormatDomainCheckConstraints(newType, static check => !check.IsNotValid, rendered).ToHashSet(StringComparer.Ordinal);
        var validated = FormatDomainCheckConstraints(oldType, static check => check.IsNotValid, rendered).Where(newValid.Contains).Order(StringComparer.Ordinal).ToArray();
        if (validated.Length > 0)
        {
            AddValueDifference(details, "DomainCheckConstraintsValidated", string.Join(", ", validated) + " (NOT VALID)", string.Join(", ", validated) + " (validated)", StringComparison.Ordinal);
        }

        return details;
    }

    /// <summary>
    /// A generated name (<see cref="DomainCheckConstraintDefinition.IsNameGenerated"/>) is a position artifact -
    /// reordering two unnamed CHECK constraints changes which one PostgreSQL numbers <c>_check</c> vs
    /// <c>_check1</c>, which is not a real change - so only an explicit name is part of the comparison (G-77). A name in
    /// PostgreSQL's default form counts as positional too, since that is how a live unnamed CHECK reads back (G-98).
    /// </summary>
    private static IEnumerable<string> FormatDomainCheckConstraints(TypeDefinition type, Func<DomainCheckConstraintDefinition, bool>? filter = null, bool rendered = false)
        => type.DomainCheckConstraints.Where(constraint => filter is null || filter(constraint)).Select(constraint =>
            $"{(constraint.HasPositionalName(type.Name) ? string.Empty : constraint.Name ?? string.Empty)}:{(rendered ? constraint.ServerRenderedCheck : constraint.NormalizedExpression ?? constraint.RawExpression)}");

    private static bool AllRendered(TypeDefinition type)
        => type.DomainCheckConstraints.Count > 0 && type.DomainCheckConstraints.All(static check => check.ServerRenderedCheck is not null);

    private static List<ModificationDetail> BuildSequenceModificationDetails(SequenceDefinition oldSequence, SequenceDefinition newSequence)
    {
        var details = new List<ModificationDetail>();
        var oldOptions = oldSequence.GetEffectiveOptions();
        var newOptions = newSequence.GetEffectiveOptions();

        // An identity's sequence belongs to its column and follows its type, so only that type and the options the column states are compared (G-214).
        if (newSequence.IdentityStatedOptions is { } stated)
        {
            AddValueDifference(details, nameof(SequenceDefinition.DataType), oldOptions.DataType, newOptions.DataType, StringComparison.Ordinal);
            void Compare(string property, string? oldValue, string? newValue)
            {
                if (stated.Contains(property))
                {
                    AddValueDifference(details, property, oldValue, newValue, StringComparison.Ordinal);
                }
            }

            Compare(nameof(SequenceDefinition.IncrementBy), oldOptions.IncrementBy, newOptions.IncrementBy);
            Compare(nameof(SequenceDefinition.MinValue), oldOptions.MinValue, newOptions.MinValue);
            Compare(nameof(SequenceDefinition.MaxValue), oldOptions.MaxValue, newOptions.MaxValue);
            Compare(nameof(SequenceDefinition.StartValue), oldOptions.StartValue, newOptions.StartValue);
            Compare(nameof(SequenceDefinition.CacheSize), oldOptions.CacheSize, newOptions.CacheSize);
            if (stated.Contains(nameof(SequenceDefinition.Cycle)))
            {
                AddValueDifference(details, nameof(SequenceDefinition.Cycle), oldOptions.Cycle, newOptions.Cycle);
            }

            // Whether the sequence is logged follows its table unless the column says (UNLOGGED or LOGGED among its options; G-294).
            if (stated.Contains(nameof(SequenceDefinition.IsUnlogged)))
            {
                AddValueDifference(details, nameof(SequenceDefinition.IsUnlogged), oldSequence.IsUnlogged, newSequence.IsUnlogged);
            }

            return details;
        }

        // Options are compared with PostgreSQL's defaults applied. A project that writes CREATE SEQUENCE s; leaves every
        // option implicit while the live catalog reports them all, so comparing what was written reported a change for
        // every such sequence, forever (G-57). What was written still drives the DDL.
        AddValueDifference(details, nameof(SequenceDefinition.DataType), oldOptions.DataType, newOptions.DataType, StringComparison.Ordinal);
        AddValueDifference(details, nameof(SequenceDefinition.IncrementBy), oldOptions.IncrementBy, newOptions.IncrementBy, StringComparison.Ordinal);
        AddValueDifference(details, nameof(SequenceDefinition.MinValue), oldOptions.MinValue, newOptions.MinValue, StringComparison.Ordinal);
        AddValueDifference(details, nameof(SequenceDefinition.MaxValue), oldOptions.MaxValue, newOptions.MaxValue, StringComparison.Ordinal);
        AddValueDifference(details, nameof(SequenceDefinition.StartValue), oldOptions.StartValue, newOptions.StartValue, StringComparison.Ordinal);
        AddValueDifference(details, nameof(SequenceDefinition.CacheSize), oldOptions.CacheSize, newOptions.CacheSize, StringComparison.Ordinal);
        AddValueDifference(details, nameof(SequenceDefinition.Cycle), oldOptions.Cycle, newOptions.Cycle);
        // An identity's sequence follows its table, and returns above; any other is compared (G-224).
        AddValueDifference(details, nameof(SequenceDefinition.IsUnlogged), oldSequence.IsUnlogged, newSequence.IsUnlogged);
        // An owning table written without its schema is compared as the table the search path finds (G-283).
        AddValueDifference(details, nameof(SequenceDefinition.OwnedByTable), oldSequence.ResolvedOwnedByTable ?? oldSequence.OwnedByTable, newSequence.ResolvedOwnedByTable ?? newSequence.OwnedByTable, StringComparison.Ordinal);
        AddValueDifference(details, nameof(SequenceDefinition.OwnedByColumn), oldSequence.OwnedByColumn, newSequence.OwnedByColumn, StringComparison.Ordinal);
        AddValueDifference(details, nameof(SequenceDefinition.Comment), oldSequence.Comment, newSequence.Comment, StringComparison.Ordinal);

        return details;
    }

    private void ValidateSemanticObject(ISemanticNode node, string source)
    {
        if (string.IsNullOrWhiteSpace(node.Key))
        {
            AddDiagnostic(RequireDiagnostics(), $"Malformed semantic object '{node.Kind}' is missing a key.", source);
        }

        if (string.IsNullOrWhiteSpace(node.Name))
        {
            AddDiagnostic(RequireDiagnostics(), $"Malformed semantic object '{node.Kind}' with key '{node.Key}' is missing a name.", source);
        }
    }

    private IReadOnlyList<ConstraintDefinition> GetTableConstraints(DatabaseModel model, TableDefinition table, string source)
    {
        var constraints = new List<ConstraintDefinition>();

        foreach (var constraintKey in table.ConstraintKeys)
        {
            if (string.IsNullOrWhiteSpace(constraintKey))
            {
                AddDiagnostic(RequireDiagnostics(), $"Table '{table.Key}' contains a blank constraint key.", source);
                continue;
            }

            if (!model.Constraints.TryGetValue(constraintKey, out var constraint))
            {
                AddDiagnostic(RequireDiagnostics(), $"Constraint '{constraintKey}' referenced by table '{table.Key}' was not found during diffing.", source);
                continue;
            }

            constraints.Add(constraint);
        }

        return constraints;
    }

    private IReadOnlyList<IndexDefinition> GetTableIndexes(DatabaseModel model, TableDefinition table, string source)
    {
        var indexes = model.Indexes.Values
            .Where(index => BelongsToTable(index, table))
            .ToArray();

        foreach (var index in indexes)
        {
            if (!string.IsNullOrWhiteSpace(index.TableName))
            {
                continue;
            }

            AddDiagnostic(RequireDiagnostics(), $"Index '{index.Key}' matched table '{table.Key}' without a table name.", source);
        }

        return indexes;
    }

    /// <summary>The indexes on the relation (a materialized view, G-171) named <paramref name="name"/> in <paramref name="schemaName"/>.</summary>
    private static IndexDefinition[] GetRelationIndexes(DatabaseModel model, string? schemaName, string name)
        => model.Indexes.Values
            .Where(index => !string.IsNullOrWhiteSpace(index.TableName)
                && string.Equals(index.SchemaName ?? "public", schemaName ?? "public", StringComparison.Ordinal)
                && (string.Equals(index.TableName, name, StringComparison.Ordinal)
                    || string.Equals(index.TableName, DatabaseModel.CreateKey(schemaName, name), StringComparison.Ordinal)))
            .ToArray();

    private static bool BelongsToTable(IndexDefinition index, TableDefinition table)
    {
        if (string.IsNullOrWhiteSpace(index.TableName))
        {
            return false;
        }

        // An index lives in its table's schema, so a same-named table in another schema is a different table (G-89).
        // An unqualified name is taken as public, as FindTriggerTable does for triggers.
        if (!string.Equals(index.SchemaName ?? "public", table.SchemaName ?? "public", StringComparison.Ordinal))
        {
            return false;
        }

        return string.Equals(index.TableName, table.Key, StringComparison.Ordinal)
            || string.Equals(index.TableName, table.Name, StringComparison.Ordinal)
            || string.Equals(DatabaseModel.CreateKey(table.SchemaName, index.TableName), table.Key, StringComparison.Ordinal);
    }

    private static void AddChanges<TNode>(IEnumerable<TNode> nodes, ICollection<AddedChange> added)
        where TNode : class, ISemanticNode
    {
        foreach (var node in nodes)
        {
            added.Add(new AddedChange
            {
                NodeId = node.Key,
                NodeKind = ChangeRecord.MapKind(node),
                Node = node,
            });
        }
    }

    private static void AddChanges<TNode>(IEnumerable<TNode> nodes, ICollection<RemovedChange> removed)
        where TNode : class, ISemanticNode
    {
        foreach (var node in nodes)
        {
            removed.Add(new RemovedChange
            {
                NodeId = node.Key,
                NodeKind = ChangeRecord.MapKind(node),
                Node = node,
            });
        }
    }

    private static void AddChanges<TNode>(IEnumerable<SemanticObjectModification<TNode>> changes, ICollection<ModifiedChange> modified)
        where TNode : class, ISemanticNode
    {
        foreach (var change in changes)
        {
            modified.Add(new ModifiedChange
            {
                NodeId = change.NodeId,
                NodeKind = ChangeRecord.MapKind(change.NewNode),
                OldNode = change.OldNode,
                NewNode = change.NewNode,
                Details = change.Details,
            });
        }
    }

    private static void AddNestedChangeSummary(ICollection<ModificationDetail> details, string propertyName, IEnumerable<string> values)
    {
        var items = values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(PgProj.Parser.SqlIdentifiers.NameComparer)
            .Order(PgProj.Parser.SqlIdentifiers.NameComparer)
            .ToArray();

        if (items.Length == 0)
        {
            return;
        }

        AddOrUpdateDetail(details, propertyName, null, items);
    }

    private static void AddListDifference(ICollection<ModificationDetail> details, string propertyName, IEnumerable<string> oldValues, IEnumerable<string> newValues)
    {
        var oldArray = NormalizeValues(oldValues);
        var newArray = NormalizeValues(newValues);
        if (oldArray.SequenceEqual(newArray, PgProj.Parser.SqlIdentifiers.NameComparer))
        {
            return;
        }

        AddOrUpdateDetail(details, propertyName, oldArray, newArray);
    }

    /// <summary>
    /// Compares two string lists positionally rather than as sets. Used for index key columns,
    /// where order is semantically significant (e.g. B-tree column ordering affects query
    /// planning), unlike the unordered dependency lists compared by <see cref="AddListDifference"/>.
    /// Only casing is canonicalized; items are not sorted or de-duplicated.
    /// </summary>
    private static void AddOrderedListDifference(ICollection<ModificationDetail> details, string propertyName, IReadOnlyList<string> oldValues, IReadOnlyList<string> newValues)
    {
        if (oldValues.SequenceEqual(newValues, PgProj.Parser.SqlIdentifiers.NameComparer))
        {
            return;
        }

        AddOrUpdateDetail(details, propertyName, oldValues, newValues);
    }

    private static void AddDictionaryDifference(
        ICollection<ModificationDetail> details,
        string propertyName,
        IDictionary<string, string>? oldValues,
        IDictionary<string, string>? newValues)
    {
        var oldText = FormatDictionary(oldValues);
        var newText = FormatDictionary(newValues);
        if (string.Equals(oldText, newText, StringComparison.Ordinal))
        {
            return;
        }

        AddOrUpdateDetail(details, propertyName, oldText, newText);
    }

    private static void AddValueDifference<T>(
        ICollection<ModificationDetail> details,
        string propertyName,
        T oldValue,
        T newValue,
        StringComparison comparison = StringComparison.Ordinal)
    {
        var differs = (oldValue, newValue) switch
        {
            (string oldText, string newText) => !string.Equals(oldText, newText, comparison),
            _ => !EqualityComparer<T>.Default.Equals(oldValue, newValue),
        };

        if (!differs)
        {
            return;
        }

        AddOrUpdateDetail(details, propertyName, oldValue, newValue);
    }

    private static void AddOrUpdateDetail(ICollection<ModificationDetail> details, string propertyName, object? oldValue, object? newValue)
    {
        if (details.Any(detail => string.Equals(detail.PropertyName, propertyName, StringComparison.Ordinal)))
        {
            return;
        }

        details.Add(new ModificationDetail
        {
            PropertyName = propertyName,
            OldValue = FormatValue(oldValue),
            NewValue = FormatValue(newValue),
        });
    }

    private static bool ExpressionTextEquals(ExpressionNode oldExpr, ExpressionNode newExpr)
    {
        return oldExpr.Kind switch
        {
            "Identifier" => string.Equals(oldExpr.Text, newExpr.Text, StringComparison.OrdinalIgnoreCase),
            "FunctionCall" => string.Equals(oldExpr.Text, newExpr.Text, StringComparison.OrdinalIgnoreCase),
            "Operator" => string.Equals(oldExpr.Text, newExpr.Text, StringComparison.OrdinalIgnoreCase),
            "Literal" => string.Equals(oldExpr.Text, newExpr.Text, StringComparison.Ordinal),
            "SimpleExpression" => string.Equals(oldExpr.Text, newExpr.Text, StringComparison.Ordinal),
            _ => string.Equals(oldExpr.Text, newExpr.Text, StringComparison.OrdinalIgnoreCase),
        };
    }

    private static string GetNodeIdentity(ISemanticNode node)
        => string.IsNullOrWhiteSpace(node.Key) ? node.Name : node.Key;

    /// <summary>
    /// Whether two <c>PARTITION BY</c> keys are the same, element by element in order (G-240): the same column or expression, the same collation (<see cref="CollationNames.Same"/>,
    /// so the database's <c>pg_catalog."C"</c> is the project's <c>"C"</c>), and the same operator class when the project states one.
    /// </summary>
    public static bool SamePartitionKey(IReadOnlyList<PgProj.Parser.PartitionKeyElementNode> first, IReadOnlyList<PgProj.Parser.PartitionKeyElementNode> second)
        => first.Count == second.Count
            && first.Zip(second, static (a, b) =>
                string.Equals(a.Name ?? a.RawExpression ?? string.Empty, b.Name ?? b.RawExpression ?? string.Empty, StringComparison.Ordinal)
                && CollationNames.Same(a.CollationName, b.CollationName)
                && string.Equals(
                    string.IsNullOrWhiteSpace(a.OperatorClassName) ? string.Empty : CollationNames.Plain(OperatorClassNames.Comparison(a.OperatorClassName)),
                    string.IsNullOrWhiteSpace(b.OperatorClassName) ? string.Empty : CollationNames.Plain(OperatorClassNames.Comparison(b.OperatorClassName)),
                    StringComparison.Ordinal)).All(static same => same);

    // An operator class option is compared like a storage parameter: spaces around "=" and quotes around the value do not matter (G-239), since
    // pg_get_indexdef prints siglen='1' for what the author wrote as siglen = 1.
    private static string OperatorClassOptionComparisonText(string option)
        => new PgProj.Parser.TableStorageOptionNode(string.Empty, option).UnquotedComparisonText;

    private static bool SequenceEqual(IEnumerable<string> oldValues, IEnumerable<string> newValues)
        => NormalizeValues(oldValues).SequenceEqual(NormalizeValues(newValues), PgProj.Parser.SqlIdentifiers.NameComparer);

    private static string[] NormalizeValues(IEnumerable<string> values)
        => values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(PgProj.Parser.SqlIdentifiers.NameComparer)
            .Order(PgProj.Parser.SqlIdentifiers.NameComparer)
            .ToArray();

    /// <summary>
    /// A field is compared on its canonical type (G-99), so <c>varchar(10)</c> and PostgreSQL's <c>character varying(10)</c> are one
    /// type; the text is the fallback when the canonical type is not available.
    /// </summary>
    /// <summary>A type's form when it has no structure: <c>enum</c> for an enum with no labels, <c>composite</c> for a composite with no fields, null otherwise (G-268).</summary>
    private static string? EmptyTypeForm(TypeDefinition type)
        => type.IsEnum ? "enum" : type.IsComposite ? "composite" : null;

    private static IEnumerable<string> FormatCompositeFields(IEnumerable<CompositeTypeFieldDefinition> fields)
        => fields.Select(static field => $"{field.Name}:{field.ComparableType ?? field.DataType ?? field.RawType ?? string.Empty}{FieldCollation(field.CollationName)}");

    /// <summary>
    /// A composite field's collation (G-192), compared as a column's is (<see cref="CollationNames"/>): <c>"C"</c> and the database's <c>pg_catalog."C"</c> are one, and none is
    /// the same as <c>default</c>. A collation in <c>public</c> is named without it, as the range options' are.
    /// </summary>
    private static string FieldCollation(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var plain = CollationNames.Plain(name);
        if (plain.StartsWith("public.", StringComparison.Ordinal))
        {
            plain = plain["public.".Length..];
        }

        return plain == "default" ? string.Empty : $" COLLATE {plain}";
    }

    /// <summary><c>"C"</c>, <c>pg_catalog."C"</c>, and <c>"pg_catalog"."C"</c> are one collation; <c>float8mi</c> and <c>pg_catalog.float8mi</c> one function (G-115).</summary>
    private static string MultirangeName(string? written, string? rangeName)
        => string.IsNullOrWhiteSpace(written)
            ? RangeTypeOptionsDefinition.DefaultMultirangeName(rangeName ?? string.Empty)
            : PgProj.Parser.SqlIdentifiers.SplitQualifiedName(written)[^1];

    private static string RangeOptionName(string? name)
    {
        var parts = PgProj.Parser.SqlIdentifiers.SplitQualifiedName(name);
        return parts.Count == 2 && parts[0] is "public" or "pg_catalog" ? parts[1] : string.Join('.', parts);
    }

    private static IEnumerable<string> FormatRangeOptions(RangeTypeOptionsDefinition? options, string? rangeName)
    {
        if (options is null)
        {
            return [];
        }

        return new[]
        {
            // The subtype is compared as a column's type is (float8 and double precision are one), and the operator class, collation, and
            // functions as PostgreSQL reads their names, without a public or pg_catalog schema: the live model names each with its schema (G-115).
            $"Subtype:{options.ComparableSubtype ?? options.Subtype ?? string.Empty}",
            $"SubtypeOperatorClass:{RangeOptionName(options.SubtypeOperatorClass)}",
            $"CollationName:{RangeOptionName(options.CollationName)}",
            $"CanonicalFunction:{RangeOptionName(options.CanonicalFunction)}",
            $"SubtypeDiffFunction:{RangeOptionName(options.SubtypeDiffFunction)}",
            // The multirange's name, or PostgreSQL's default for it when none is stated, without its schema (G-204).
            $"MultirangeTypeName:{MultirangeName(options.MultirangeTypeName, rangeName)}",
        };
    }

    private static string? FormatDictionary(IDictionary<string, string>? values)
    {
        if (values is null || values.Count == 0)
        {
            return null;
        }

        return string.Join(", ", values
            .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(static pair => $"{pair.Key}={pair.Value}"));
    }

    private static string? FormatValue(object? value)
        => value switch
        {
            null => null,
            string text => text,
            IEnumerable<string> texts => string.Join(", ", texts),
            System.Collections.IEnumerable items when value is not string => string.Join(", ", items.Cast<object?>().Select(FormatSingleValue)),
            _ => FormatSingleValue(value),
        };

    private static string FormatSingleValue(object? value) => value?.ToString() ?? string.Empty;

    private DatabaseModel RequireOldModel()
        => _oldModel ?? throw new InvalidOperationException("DiffEngine must be executed before diff helpers are used.");

    private DatabaseModel RequireNewModel()
        => _newModel ?? throw new InvalidOperationException("DiffEngine must be executed before diff helpers are used.");

    private List<SemanticDiagnostic> RequireDiagnostics()
        => _diagnostics ?? throw new InvalidOperationException("DiffEngine must be executed before diagnostics are recorded.");

    private sealed record NodeDiffState<TNode>(
        IReadOnlyList<TNode> Added,
        IReadOnlyList<TNode> Removed,
        IReadOnlyList<SemanticObjectModification<TNode>> Modified)
        where TNode : class, ISemanticNode;

    private sealed record TableDiffState(
        IReadOnlyList<TableDefinition> Added,
        IReadOnlyList<TableDefinition> Removed,
        IReadOnlyList<SemanticObjectModification<TableDefinition>> Modified,
        IReadOnlyList<ColumnDefinition> AddedColumns,
        IReadOnlyList<ColumnDefinition> RemovedColumns,
        IReadOnlyList<SemanticObjectModification<ColumnDefinition>> ModifiedColumns,
        IReadOnlyList<ConstraintDefinition> AddedConstraints,
        IReadOnlyList<ConstraintDefinition> RemovedConstraints,
        IReadOnlyList<SemanticObjectModification<ConstraintDefinition>> ModifiedConstraints,
        IReadOnlyList<IndexDefinition> AddedIndexes,
        IReadOnlyList<IndexDefinition> RemovedIndexes,
        IReadOnlyList<SemanticObjectModification<IndexDefinition>> ModifiedIndexes);
}
