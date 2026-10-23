using PgProj.Diff;
using PgProj.Semantics;

namespace PgProj.Deploy;

public sealed record class AlterOperation : SqlOperation
{
    public required ModifiedChange Change { get; init; }

    public override string GenerateSql()
        => Change.NewNode switch
        {
            SequenceDefinition sequence => GenerateAlterSequence(sequence, Change.Details),
            TableDefinition newTable when Change.OldNode is TableDefinition oldTable => GenerateAlterTable(oldTable, newTable, Change.Details),
            TypeDefinition newType when Change.OldNode is TypeDefinition oldType => GenerateAlterType(oldType, newType),
            IndexDefinition newIndex when Change.OldNode is IndexDefinition oldIndex => GenerateAlterIndex(oldIndex, newIndex, Change.Details),
            ViewDefinition newView => GenerateAlterView(Change.OldNode as ViewDefinition, newView),
            PublicationDefinition newPublication when Change.OldNode is PublicationDefinition oldPublication => CreateOperation.GenerateAlterPublication(oldPublication, newPublication, Change.Details),
            TextSearchDictionaryDefinition newDictionary when Change.OldNode is TextSearchDictionaryDefinition oldDictionary => TextSearchSqlBuilder.AlterDictionary(oldDictionary, newDictionary),
            TextSearchConfigurationDefinition newConfiguration when Change.OldNode is TextSearchConfigurationDefinition oldConfiguration => TextSearchSqlBuilder.AlterConfiguration(oldConfiguration, newConfiguration),
            CollationDefinition newCollation => CommentSqlHelper.GenerateObjectComment("COLLATION", newCollation.SchemaName, newCollation.Name, newCollation.Comment)
                ?? CommentSqlHelper.GenerateObjectCommentClear("COLLATION", newCollation.SchemaName, newCollation.Name),
            StatisticsDefinition newStatistics => CommentSqlHelper.GenerateObjectComment("STATISTICS", newStatistics.SchemaName, newStatistics.Name, newStatistics.Comment)
                ?? CommentSqlHelper.GenerateObjectCommentClear("STATISTICS", newStatistics.SchemaName, newStatistics.Name),
            OwnerDefinition newOwner => OwnerSqlBuilder.Alter(newOwner),
            ConstraintDefinition newConstraint when Change.OldNode is ConstraintDefinition oldConstraint => GenerateAlterConstraint(oldConstraint, newConstraint, Change.Details),
            TriggerDefinition newTrigger when Change.OldNode is TriggerDefinition => string.Join(
                "\n",
                new[]
                {
                    Change.Details.Any(static detail => detail.PropertyName == nameof(TriggerDefinition.EnabledState))
                        ? CommentSqlHelper.GenerateTriggerState(newTrigger.SchemaName, newTrigger.TableName ?? "<table>", newTrigger.Name, newTrigger.EnabledState)
                        : null,
                    Change.Details.Any(static detail => detail.PropertyName == nameof(TriggerDefinition.Comment))
                        ? CommentSqlHelper.GenerateTriggerComment(newTrigger.SchemaName, newTrigger.TableName ?? "<table>", newTrigger.Name, newTrigger.Comment)
                        : null,
                }.OfType<string>().Concat(Change.Details.Any(static detail => detail.PropertyName == nameof(TriggerDefinition.PartitionStates))
                    ? CommentSqlHelper.GeneratePartitionTriggerStates(newTrigger.Name, newTrigger.PartitionStates, ((TriggerDefinition)Change.OldNode!).PartitionStates)
                    : [])),
            RuleDefinition newRule when Change.OldNode is RuleDefinition => CommentSqlHelper.GenerateRuleState(newRule.SchemaName, newRule.TableName ?? "<table>", newRule.Name, newRule.EnabledState),
            PolicyDefinition newPolicy when Change.OldNode is PolicyDefinition => CommentSqlHelper.GeneratePolicyComment(newPolicy.SchemaName, newPolicy.TableName ?? "<table>", newPolicy.Name, newPolicy.Comment),
            SchemaDefinition newSchema => CommentSqlHelper.GenerateSchemaComment(newSchema.Name, newSchema.Comment),
            _ => $"-- TODO: Generate ALTER SQL for {NodeKind} '{NodeId}'.\n-- Old Node: {Change.OldNode.Key}\n-- New Node: {Change.NewNode.Key}",
        };

    /// <summary>
    /// Emits one <c>ALTER TYPE ... ADD VALUE 'label'</c> statement per label appended to an enum
    /// type's label list. Only reachable for additive-only enum changes (see
    /// <c>ScriptGenerator.IsAdditiveEnumChange</c>); any label removal or reorder is redeployed via
    /// drop-then-recreate instead and never reaches this method (G-31). A comment change (G-34) is emitted here too, either
    /// after the added labels or alone when the comment is the only difference (G-53). A domain (G-77) is altered
    /// through <see cref="GenerateAlterDomain"/> instead - it is never routed here for its base type (there is no
    /// <c>ALTER DOMAIN ... TYPE</c>; <c>ScriptGenerator.IsDropAndRecreateObject</c> sends a base type change to
    /// drop-then-recreate), only for its DEFAULT/NOT NULL/CHECK constraints, all of which do have an in-place ALTER.
    /// </summary>
    private static string GenerateAlterType(TypeDefinition oldType, TypeDefinition newType)
    {
        if (IsExtension(newType))
        {
            return GenerateAlterExtension(oldType, newType);
        }

        if (IsDomain(newType))
        {
            return GenerateAlterDomain(oldType, newType);
        }

        var qualifiedName = FormatQualifiedName(newType.SchemaName, newType.Name);
        var statements = IsCompositeChange(oldType, newType)
            ? GenerateAlterCompositeFields(qualifiedName, oldType, newType)
            : GenerateAddEnumValues(qualifiedName, oldType, newType);

        if (!string.Equals(oldType.Comment, newType.Comment, StringComparison.Ordinal))
        {
            statements.Add(newType.Comment is null
                ? CommentSqlHelper.GenerateTypeCommentClear(newType.SchemaName, newType.Name)
                : CommentSqlHelper.GenerateTypeComment(newType.SchemaName, newType.Name, newType.Comment)!);
        }

        // A composite type's fields' comments (G-216), after any field changes, so a field added here can take its comment.
        statements.AddRange(CommentSqlHelper.GenerateColumnCommentChanges(newType.SchemaName, newType.Name, oldType.ColumnComments, newType.ColumnComments));
        return string.Join("\n", statements);
    }

    /// <summary>
    /// An extension's stated version, schema, and comment, changed in place (G-213), each only when the project states it and the database differs: the
    /// diff compares nothing else. The version is updated first, so the objects it adds move with the rest.
    /// </summary>
    private static string GenerateAlterExtension(TypeDefinition oldType, TypeDefinition newType)
        => string.Join("\n", ExtensionAlterStatements(oldType, newType));

    /// <summary>The statements that bring an extension's stated version, schema, and comment in line (G-213); none when it has nothing stated to change.</summary>
    internal static List<string> ExtensionAlterStatements(TypeDefinition oldType, TypeDefinition newType)
    {
        var name = QuoteIdentifier(newType.Name);
        var statements = new List<string>();
        if (newType.ExtensionVersion is { } version && !string.Equals(oldType.ExtensionVersion, version, StringComparison.Ordinal))
        {
            statements.Add($"ALTER EXTENSION {name} UPDATE TO {CommentSqlHelper.QuoteLiteral(version)};");
        }

        if (newType.ExtensionSchemaStated && !string.Equals(oldType.SchemaName, newType.SchemaName, StringComparison.Ordinal))
        {
            statements.Add($"ALTER EXTENSION {name} SET SCHEMA {QuoteIdentifier(newType.SchemaName!)};");
        }

        if (newType.CommentStated && !string.Equals(oldType.Comment, newType.Comment, StringComparison.Ordinal))
        {
            statements.Add(CommentSqlHelper.GenerateExtensionComment(newType.Name, newType.Comment));
        }

        return statements;
    }

    /// <summary>One <c>ALTER TYPE ... ADD VALUE</c> per inserted label, positioned with <c>BEFORE</c> or <c>AFTER</c> when it is not at the end (G-183).</summary>
    private static List<string> GenerateAddEnumValues(string qualifiedName, TypeDefinition oldType, TypeDefinition newType)
    {
        static string Literal(string label) => "'" + label.Replace("'", "''", StringComparison.Ordinal) + "'";

        return (EnumLabelAdditions.Plan(oldType.EnumLabels, newType.EnumLabels) ?? [])
            .Select(step => $"ALTER TYPE {qualifiedName} ADD VALUE {Literal(step.Label)}{(step.Position is null ? string.Empty : $" {step.Position} {Literal(step.Neighbour!)}")};")
            .ToList();
    }

    /// <summary>
    /// A composite type's fields changed in place (G-111), matched by name as the diff compares them: a field the project no
    /// longer has is dropped, one whose type changed is retyped, and a new one is added, in that order. PostgreSQL appends an
    /// added field, so one written in the middle of the type ends up last in the database; field order is not compared (G-110),
    /// so the type still converges. <c>ALTER ATTRIBUTE ... TYPE</c> is refused by PostgreSQL while any table column uses the
    /// type (the publish then rolls back); dropping and recreating the type fails in that case too, and in every other case in
    /// which anything uses it. <c>CASCADE</c> is written when a typed table is of the type (G-136), and only then: it carries the field to that table, which the table's own change does not repeat.
    /// </summary>
    private static List<string> GenerateAlterCompositeFields(string qualifiedName, TypeDefinition oldType, TypeDefinition newType)
    {
        var oldFields = oldType.CompositeFields.ToDictionary(static field => field.Name, StringComparer.Ordinal);
        var newFieldNames = newType.CompositeFields.Select(static field => field.Name).ToHashSet(StringComparer.Ordinal);
        // A typed table of the type (G-136) takes the change too: PostgreSQL refuses it without CASCADE, and with it carries the field to each typed table.
        var cascade = oldType.HasTypedTables || newType.HasTypedTables ? " CASCADE" : string.Empty;
        var statements = new List<string>();

        foreach (var oldField in oldType.CompositeFields.Where(field => !newFieldNames.Contains(field.Name)))
        {
            statements.Add($"ALTER TYPE {qualifiedName} DROP ATTRIBUTE {QuoteIdentifier(oldField.Name)}{cascade};");
        }

        foreach (var newField in newType.CompositeFields)
        {
            if (oldFields.TryGetValue(newField.Name, out var oldField)
                && (!string.Equals(ComparableFieldType(oldField), ComparableFieldType(newField), StringComparison.Ordinal)
                    || !CollationNames.Same(oldField.CollationName, newField.CollationName)))
            {
                statements.Add($"ALTER TYPE {qualifiedName} ALTER ATTRIBUTE {QuoteIdentifier(newField.Name)} TYPE {FieldType(newField)}{cascade};");
            }
        }

        foreach (var newField in newType.CompositeFields.Where(field => !oldFields.ContainsKey(field.Name)))
        {
            statements.Add($"ALTER TYPE {qualifiedName} ADD ATTRIBUTE {QuoteIdentifier(newField.Name)} {FieldType(newField)}{cascade};");
        }

        return statements;
    }

    /// <summary>
    /// The composite fields a change drops (G-111): every stored row of a column of the type loses that field's value, so
    /// <c>publish --execute</c> treats it as destructive (G-35), the same way it treats a dropped column.
    /// </summary>
    internal static IReadOnlyList<string> GetDestructiveCompositeFieldReasons(TypeDefinition oldType, TypeDefinition newType)
    {
        if (!IsCompositeChange(oldType, newType))
        {
            return [];
        }

        var newFieldNames = newType.CompositeFields.Select(static field => field.Name).ToHashSet(StringComparer.Ordinal);
        return oldType.CompositeFields
            .Where(field => !newFieldNames.Contains(field.Name))
            .Select(field => $"field \"{field.Name}\" of composite type {FormatQualifiedName(oldType.SchemaName, oldType.Name)} dropped; the values it holds in stored rows are lost")
            .ToArray();
    }

    /// <summary>A field's type in the form the diff compares it in (G-99).</summary>
    private static string ComparableFieldType(CompositeTypeFieldDefinition field)
        => field.ComparableType ?? field.DataType ?? field.RawType ?? string.Empty;

    /// <summary>A field's type as <c>CREATE TYPE</c> writes it (<see cref="CreateOperation"/>).</summary>
    private static string FieldType(CompositeTypeFieldDefinition field)
        => CreateOperation.FormatCompositeFieldType(field);

    /// <summary>
    /// A domain's DEFAULT, NOT NULL, and named CHECK constraints each have an in-place <c>ALTER DOMAIN</c> form, so
    /// a change confined to those is deployed without dropping the domain (which would fail while any column still
    /// uses it - the common case). Only reachable when <c>ScriptGenerator.IsAlterSafeDomainChange</c> already
    /// confirmed the base type is unchanged (there is no <c>ALTER DOMAIN ... TYPE</c>). A named CHECK constraint is
    /// matched old-to-new by name (G-77); an unnamed one by expression, dropped under its database name and added
    /// without one (G-109).
    /// </summary>
    private static string GenerateAlterDomain(TypeDefinition oldType, TypeDefinition newType)
    {
        var qualifiedName = FormatQualifiedName(newType.SchemaName, newType.Name);
        var statements = new List<string>();

        var oldDefault = oldType.GetComparableDomainDefaultExpression();
        var newDefault = newType.GetComparableDomainDefaultExpression();
        if (!string.Equals(oldDefault, newDefault, StringComparison.Ordinal))
        {
            statements.Add(string.IsNullOrWhiteSpace(newType.DomainRawDefaultExpression)
                ? $"ALTER DOMAIN {qualifiedName} DROP DEFAULT;"
                : $"ALTER DOMAIN {qualifiedName} SET DEFAULT {newType.DomainRawDefaultExpression};");
        }

        if (oldType.DomainIsNotNull != newType.DomainIsNotNull)
        {
            statements.Add(newType.DomainIsNotNull
                ? $"ALTER DOMAIN {qualifiedName} SET NOT NULL;"
                : $"ALTER DOMAIN {qualifiedName} DROP NOT NULL;");
        }

        // Named CHECKs are matched by name below; positionally named ones (generated, or PostgreSQL's default form, G-98) by expression.
        var oldNamed = oldType.DomainCheckConstraints.Where(check => !check.HasPositionalName(oldType.Name))
            .ToDictionary(static check => check.Name!, PgProj.Parser.SqlIdentifiers.NameComparer);
        var newNamed = newType.DomainCheckConstraints.Where(check => !check.HasPositionalName(newType.Name))
            .ToDictionary(static check => check.Name!, PgProj.Parser.SqlIdentifiers.NameComparer);

        foreach (var (name, _) in oldNamed)
        {
            if (!newNamed.ContainsKey(name))
            {
                statements.Add($"ALTER DOMAIN {qualifiedName} DROP CONSTRAINT {QuoteIdentifier(name)};");
            }
        }

        foreach (var (name, newCheck) in newNamed)
        {
            if (!oldNamed.TryGetValue(name, out var oldCheck) || !ExpressionUnchanged(oldCheck, newCheck))
            {
                if (oldNamed.ContainsKey(name))
                {
                    statements.Add($"ALTER DOMAIN {qualifiedName} DROP CONSTRAINT {QuoteIdentifier(name)};");
                }

                statements.Add($"ALTER DOMAIN {qualifiedName} ADD CONSTRAINT {QuoteIdentifier(name)} CHECK ({newCheck.RawExpression}){(newCheck.IsNotValid ? " NOT VALID" : string.Empty)};");
            }
            else if (oldCheck.IsNotValid && !newCheck.IsNotValid)
            {
                // The database has not validated a check the project states valid: validated in place (G-233).
                statements.Add($"ALTER DOMAIN {qualifiedName} VALIDATE CONSTRAINT {QuoteIdentifier(name)};");
            }
        }

        // An unnamed CHECK has no name of its own to match on, so the two sets are matched by expression (G-109): one the project no
        // longer has is dropped under the name the database gave it, and a new one is added unnamed, taking the next free default name.
        // The diff leaves positional names out of the comparison (G-98), so whatever number PostgreSQL picks, the domain converges.
        var unmatchedNew = newType.DomainCheckConstraints.Where(check => check.HasPositionalName(newType.Name)).ToList();
        var droppedPositional = new List<DomainCheckConstraintDefinition>();
        foreach (var oldCheck in oldType.DomainCheckConstraints.Where(check => check.HasPositionalName(oldType.Name)))
        {
            var match = unmatchedNew.FindIndex(newCheck => string.Equals(ComparableCheck(newCheck), ComparableCheck(oldCheck), StringComparison.Ordinal));
            if (match >= 0)
            {
                if (oldCheck.IsNotValid && !unmatchedNew[match].IsNotValid)
                {
                    statements.Add($"ALTER DOMAIN {qualifiedName} VALIDATE CONSTRAINT {QuoteIdentifier(oldCheck.Name!)};");
                }

                unmatchedNew.RemoveAt(match);
            }
            else
            {
                droppedPositional.Add(oldCheck);
            }
        }

        foreach (var oldCheck in droppedPositional)
        {
            statements.Add($"ALTER DOMAIN {qualifiedName} DROP CONSTRAINT {QuoteIdentifier(oldCheck.Name!)};");
        }

        foreach (var newCheck in unmatchedNew)
        {
            statements.Add($"ALTER DOMAIN {qualifiedName} ADD CHECK ({newCheck.RawExpression}){(newCheck.IsNotValid ? " NOT VALID" : string.Empty)};");
        }

        if (!string.Equals(oldType.Comment, newType.Comment, StringComparison.Ordinal))
        {
            statements.Add(newType.Comment is null
                ? CommentSqlHelper.GenerateTypeCommentClear(newType.SchemaName, newType.Name)
                : CommentSqlHelper.GenerateTypeComment(newType.SchemaName, newType.Name, newType.Comment)!);
        }

        return string.Join("\n", statements);
    }

    private static bool ExpressionUnchanged(DomainCheckConstraintDefinition oldCheck, DomainCheckConstraintDefinition newCheck)
        => string.Equals(ComparableCheck(oldCheck), ComparableCheck(newCheck), StringComparison.Ordinal);

    /// <summary>The form a domain CHECK's expression is compared in, the same one the diff uses (G-73).</summary>
    private static string ComparableCheck(DomainCheckConstraintDefinition check)
        => check.NormalizedExpression ?? check.RawExpression;

    /// <summary>
    /// Only reachable for an index change to its storage parameters or its comment (<c>ScriptGenerator.IsDropAndRecreateObject</c> routes anything else to
    /// drop-then-recreate, since an index has no in-place ALTER for its definition): <c>ALTER INDEX ... SET/RESET</c> (G-195), then <c>COMMENT ON INDEX</c> (G-34).
    /// </summary>
    private static string GenerateAlterIndex(IndexDefinition oldIndex, IndexDefinition newIndex, IReadOnlyList<ModificationDetail> details)
    {
        var statements = new List<string>();

        // First, so what follows names the index as the project does (G-289).
        if (details.Any(static detail => detail.PropertyName == nameof(IndexDefinition.Name)))
        {
            statements.Add($"ALTER INDEX {FormatQualifiedName(oldIndex.SchemaName, oldIndex.Name)} RENAME TO {QuoteIdentifier(newIndex.Name)};");
        }

        if (details.Any(static detail => detail.PropertyName == nameof(IndexDefinition.IsClustered)))
        {
            statements.Add(CommentSqlHelper.GenerateClusterOn(newIndex.SchemaName, newIndex.TableName ?? "<table>", newIndex.Name));
        }

        if (details.Any(static detail => detail.PropertyName == nameof(IndexDefinition.IsReplicaIdentity)))
        {
            statements.Add(CommentSqlHelper.GenerateReplicaIdentityUsingIndex(newIndex.SchemaName, newIndex.TableName ?? "<table>", newIndex.Name));
        }

        if (details.Any(static detail => detail.PropertyName == nameof(IndexDefinition.StorageOptions)))
        {
            statements.AddRange(GenerateStorageChanges("INDEX " + FormatQualifiedName(newIndex.SchemaName, newIndex.Name), oldIndex.StorageOptions, newIndex.StorageOptions));
        }

        if (details.Any(static detail => detail.PropertyName == nameof(IndexDefinition.Comment)))
        {
            statements.Add(newIndex.Comment is null
                ? CommentSqlHelper.GenerateIndexCommentClear(newIndex.SchemaName, newIndex.Name)
                : CommentSqlHelper.GenerateIndexComment(newIndex.SchemaName, newIndex.Name, newIndex.Comment)!);
        }

        return string.Join("\n", statements);
    }

    /// <summary>
    /// A key or exclusion constraint whose index's storage parameters changed (G-195) is altered through that index, which has the constraint's name and the
    /// table's schema; then its comment (G-161). Anything else about a constraint is dropped and added again.
    /// </summary>
    private static string GenerateAlterConstraint(ConstraintDefinition oldConstraint, ConstraintDefinition newConstraint, IReadOnlyList<ModificationDetail> details)
    {
        var statements = new List<string>();

        // First, so what follows names the constraint as the project does; a key's index is renamed with it (G-289).
        if (details.Any(static detail => detail.PropertyName == nameof(ConstraintDefinition.Name)))
        {
            statements.Add($"ALTER TABLE {FormatQualifiedName(newConstraint.SchemaName, newConstraint.TableName ?? "<table>")} RENAME CONSTRAINT {QuoteIdentifier(oldConstraint.Name)} TO {QuoteIdentifier(newConstraint.Name)};");
        }

        if (details.Any(static detail => detail.PropertyName == nameof(ConstraintDefinition.IsClustered)))
        {
            statements.Add(CommentSqlHelper.GenerateClusterOn(newConstraint.SchemaName, newConstraint.TableName ?? "<table>", newConstraint.Name));
        }

        if (details.Any(static detail => detail.PropertyName == nameof(ConstraintDefinition.IsReplicaIdentity)))
        {
            statements.Add(CommentSqlHelper.GenerateReplicaIdentityUsingIndex(newConstraint.SchemaName, newConstraint.TableName ?? "<table>", newConstraint.Name));
        }

        if (details.Any(static detail => detail.PropertyName == nameof(ConstraintDefinition.IndexStorageOptions)))
        {
            statements.AddRange(GenerateStorageChanges("INDEX " + FormatQualifiedName(newConstraint.SchemaName, newConstraint.Name), oldConstraint.IndexStorageOptions, newConstraint.IndexStorageOptions));
        }

        if (details.Any(static detail => detail.PropertyName == nameof(ConstraintDefinition.IsNotValid)))
        {
            statements.Add($"ALTER TABLE {FormatQualifiedName(newConstraint.SchemaName, newConstraint.TableName ?? "<table>")} VALIDATE CONSTRAINT {QuoteIdentifier(newConstraint.Name)};");
        }

        if (details.Any(static detail => detail.PropertyName == nameof(ConstraintDefinition.Comment)))
        {
            statements.Add(CommentSqlHelper.GenerateConstraintComment(newConstraint.SchemaName, newConstraint.TableName ?? "<table>", newConstraint.Name, newConstraint.Comment));
        }

        if (details.Any(static detail => detail.PropertyName == nameof(ConstraintDefinition.IndexComment)))
        {
            statements.Add(CommentSqlHelper.GenerateConstraintIndexComment(newConstraint.SchemaName, newConstraint.Name, newConstraint.IndexComment));
        }

        return string.Join("\n", statements);
    }

    /// <summary>
    /// <c>ALTER INDEX ... SET (...)</c> for each option added or changed, as written, and <c>ALTER INDEX ... RESET (...)</c> for each the project no longer states
    /// (G-195); the same for a materialized view, with <c>ALTER MATERIALIZED VIEW</c> as <paramref name="alterTarget"/> (G-215). Neither rebuilds the index; a changed <c>fillfactor</c> applies to pages written from then on.
    /// </summary>
    private static IEnumerable<string> GenerateStorageChanges(string alterTarget, IReadOnlyList<PgProj.Parser.TableStorageOptionNode> oldOptions, IReadOnlyList<PgProj.Parser.TableStorageOptionNode> newOptions)
    {
        static string Name(PgProj.Parser.TableStorageOptionNode option) => option.Name.Trim().ToLowerInvariant();

        var oldByName = oldOptions.GroupBy(Name).ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.Ordinal);
        var newNames = newOptions.Select(Name).ToHashSet(StringComparer.Ordinal);

        var set = newOptions
            .Where(option => !oldByName.TryGetValue(Name(option), out var old) || !string.Equals(old.UnquotedComparisonText, option.UnquotedComparisonText, StringComparison.Ordinal))
            .Select(static option => option.RawText.Trim())
            .ToArray();
        if (set.Length > 0)
        {
            yield return $"ALTER {alterTarget} SET ({string.Join(", ", set)});";
        }

        var reset = oldOptions.Select(Name).Where(name => !newNames.Contains(name)).Distinct(StringComparer.Ordinal).ToArray();
        if (reset.Length > 0)
        {
            yield return $"ALTER {alterTarget} RESET ({string.Join(", ", reset)});";
        }
    }

    /// <summary>
    /// A materialized view's comment, its columns' comments, and its storage parameters, changed in place (G-34, G-216, G-215): only those that differ.
    /// <c>ScriptGenerator.AddModifiedViewOperations</c> sends any other change to drop-then-recreate, since a materialized view has no <c>CREATE OR REPLACE</c>.
    /// </summary>
    private static string GenerateAlterView(ViewDefinition? oldView, ViewDefinition newView)
    {
        var statements = new List<string>();
        if (oldView is null || !string.Equals(oldView.Comment, newView.Comment, StringComparison.Ordinal))
        {
            statements.Add(newView.Comment is null
                ? CommentSqlHelper.GenerateViewCommentClear(newView.SchemaName, newView.Name, newView.IsMaterialized)
                : CommentSqlHelper.GenerateViewComment(newView.SchemaName, newView.Name, newView.IsMaterialized, newView.Comment)!);
        }

        statements.AddRange(CommentSqlHelper.GenerateColumnCommentChanges(newView.SchemaName, newView.Name, oldView?.ColumnComments ?? ColumnCommentMap.Empty, newView.ColumnComments));
        if (newView.IsMaterialized)
        {
            var target = "MATERIALIZED VIEW " + FormatQualifiedName(newView.SchemaName, newView.Name);
            statements.AddRange(GenerateStorageChanges(target, oldView?.StorageOptions ?? [], newView.StorageOptions));

            // In place, as a table's (G-219); there is no RESET TABLESPACE, so a tablespace no longer stated is left to the user, as a table's is.
            if (oldView is not null && !string.Equals(oldView.TablespaceName, newView.TablespaceName, StringComparison.Ordinal))
            {
                statements.Add(newView.TablespaceName is null
                    ? $"-- TODO: Materialized view {FormatQualifiedName(newView.SchemaName, newView.Name)} no longer specifies a TABLESPACE. PostgreSQL has no ALTER MATERIALIZED VIEW ... RESET TABLESPACE; name the database's actual default tablespace explicitly."
                    : $"ALTER {target} SET TABLESPACE {QuoteIdentifier(newView.TablespaceName)};");
            }

            if (oldView is not null && newView.AccessMethod is not null && !string.Equals(oldView.AccessMethod, newView.AccessMethod, StringComparison.Ordinal))
            {
                statements.Add($"ALTER {target} SET ACCESS METHOD {QuoteIdentifier(newView.AccessMethod)};");
            }
        }
        return string.Join("\n", statements);
    }

    /// <summary>
    /// Emits one <c>ALTER TABLE</c> statement per column change: <c>RENAME COLUMN</c> for columns
    /// annotated with an explicit <c>-- @renamed-from: old_name</c> comment (see
    /// <c>docs/renaming-columns.md</c>), <c>ADD COLUMN</c> for new columns, <c>DROP COLUMN</c> for
    /// removed columns, <c>ALTER COLUMN ... TYPE ... USING ...</c> for type changes, and
    /// <c>SET</c>/<c>DROP NOT NULL</c> and <c>SET</c>/<c>DROP DEFAULT</c> for columns whose
    /// nullability or default changed. Rename statements are always emitted before other column
    /// alterations so subsequent statements can reference the column by its new name. Column and
    /// table comment changes (see G-24a) are emitted as trailing <c>COMMENT ON ...</c> statements.
    /// </summary>
    /// <summary>Compares PostgreSQL's renderings of the two bounds when both exist, else the bounds as written (G-101).</summary>
    private static bool PartitionBoundsMatch(TableDefinition oldTable, TableDefinition newTable)
    {
        var (oldBound, newBound) = TableDefinition.GetComparablePartitionBounds(oldTable, newTable);
        return string.Equals(oldBound, newBound, StringComparison.Ordinal);
    }

    /// <summary>
    /// What takes a column from its old nullability to its new one. An explicit NULL and an unspecified nullability are the same column
    /// (G-62). PostgreSQL 18's <c>NOT VALID</c> NOT NULL (G-121) adds three cases: it is added with <c>ADD ... NOT NULL ... NOT VALID</c>; a
    /// valid NOT NULL that becomes not valid is dropped and added back, since nothing un-validates it; and a not valid one that becomes valid is
    /// validated by name (the live database's, or a TODO when it has none to give).
    /// </summary>
    /// <summary>
    /// Makes an existing column an identity (G-214), with its options as written. PostgreSQL starts the new sequence at its start value whatever the
    /// column holds, so when the project states no <c>START WITH</c> and the sequence ascends, it is moved past the column's largest value, and the next
    /// row cannot collide with one already there.
    /// </summary>
    private static void AddIdentity(List<string> statements, string qualifiedName, ColumnDefinition column)
    {
        var quotedColumn = QuoteIdentifier(column.Name);
        statements.Add($"ALTER TABLE {qualifiedName} ALTER COLUMN {quotedColumn} ADD GENERATED {column.IdentityGeneration} AS IDENTITY{CreateOperation.FormatIdentityOptions(column)};");

        var options = column.DeclarationNode?.IdentityOptions;
        var statesStart = options is not null && options.ContainsKey("start");
        var descends = options is not null && options.TryGetValue("increment", out var increment) && increment.TrimStart().StartsWith('-');
        if (!statesStart && !descends)
        {
            var table = CommentSqlHelper.QuoteLiteral(qualifiedName);
            var name = CommentSqlHelper.QuoteLiteral(column.Name);
            statements.Add($"SELECT pg_catalog.setval(pg_catalog.pg_get_serial_sequence({table}, {name}), max({quotedColumn})) FROM {qualifiedName} HAVING max({quotedColumn}) IS NOT NULL;");
        }
    }

    /// <summary>
    /// A NOT NULL constraint's name and comment (PostgreSQL 18; G-235). A column that is already NOT NULL under another name is renamed in place. One that becomes NOT NULL
    /// is given the project's name after <c>SET NOT NULL</c>: the rename runs only on a server that stores the name (a <c>DO</c> block asks), so the same script
    /// serves every version. The comment is set when it differs, or with a constraint that is new.
    /// </summary>
    private static void AddNotNullNameChange(List<string> statements, TableDefinition newTable, string qualifiedName, ColumnDefinition oldColumn, ColumnDefinition newColumn)
    {
        if (newColumn.AllowsNull || newColumn.NotNullName is not { Length: > 0 } name || newColumn.InheritedFromTable is not null)
        {
            return;
        }

        if (oldColumn.AllowsNull)
        {
            if (!newColumn.NotNullIsNotValid && !string.Equals(name, $"{newTable.Name}_{newColumn.Name}_not_null", StringComparison.Ordinal))
            {
                var table = CommentSqlHelper.QuoteLiteral(qualifiedName);
                var column = CommentSqlHelper.QuoteLiteral(newColumn.Name);
                statements.Add(
                    "DO $pgproj$\nBEGIN\n"
                    + "    IF current_setting('server_version_num')::int >= 180000 THEN\n"
                    + $"        EXECUTE format('ALTER TABLE %s RENAME CONSTRAINT %I TO %I', {table}::regclass, (SELECT conname FROM pg_catalog.pg_constraint WHERE conrelid = {table}::regclass AND contype = 'n' AND conkey = ARRAY[(SELECT attnum FROM pg_catalog.pg_attribute WHERE attrelid = {table}::regclass AND attname = {column})]::int2[]), {CommentSqlHelper.QuoteLiteral(name)});\n"
                    + "    END IF;\nEND\n$pgproj$;");
            }
        }
        else if (oldColumn.NotNullName is { Length: > 0 } oldName && !string.Equals(oldName, name, StringComparison.Ordinal))
        {
            statements.Add($"ALTER TABLE {qualifiedName} RENAME CONSTRAINT {QuoteIdentifier(oldName)} TO {QuoteIdentifier(name)};");
        }

        var commentChanged = newColumn.NotNullCommentStated && (oldColumn.AllowsNull
            ? newColumn.NotNullComment is not null
            : !string.Equals(oldColumn.NotNullComment, newColumn.NotNullComment, StringComparison.Ordinal));
        if (commentChanged)
        {
            statements.Add(CommentSqlHelper.GenerateNotNullConstraintComment(newTable.SchemaName, newTable.Name, name, newColumn.NotNullComment));
        }
    }

    private static void AddNullabilityChange(List<string> statements, string qualifiedName, ColumnDefinition oldColumn, ColumnDefinition newColumn)
    {
        var quotedColumn = QuoteIdentifier(newColumn.Name);
        var oldNotValid = !oldColumn.AllowsNull && oldColumn.NotNullIsNotValid;
        var newNotValid = !newColumn.AllowsNull && newColumn.NotNullIsNotValid;

        if (oldColumn.AllowsNull != newColumn.AllowsNull)
        {
            if (newColumn.AllowsNull)
            {
                statements.Add($"ALTER TABLE {qualifiedName} ALTER COLUMN {quotedColumn} DROP NOT NULL;");
            }
            else if (newColumn.NotNullNoInherit && newColumn.InheritedFromTable is null)
            {
                // NO INHERIT has no SET NOT NULL form (PostgreSQL 18; G-253).
                var named = string.IsNullOrWhiteSpace(newColumn.NotNullName) ? string.Empty : $"CONSTRAINT {QuoteIdentifier(newColumn.NotNullName)} ";
                statements.Add($"ALTER TABLE {qualifiedName} ADD {named}NOT NULL {quotedColumn} NO INHERIT{(newNotValid ? " NOT VALID" : string.Empty)};");
            }
            else
            {
                statements.Add(newNotValid
                    ? CreateOperation.BuildNotValidNotNullSql(qualifiedName, newColumn)
                    : $"ALTER TABLE {qualifiedName} ALTER COLUMN {quotedColumn} SET NOT NULL;");
            }

            return;
        }

        if (!newColumn.AllowsNull && oldColumn.NotNullNoInherit != newColumn.NotNullNoInherit && newColumn.InheritedFromTable is null)
        {
            // Toggled in place by the constraint's live name (PostgreSQL 18; G-253).
            statements.Add(string.IsNullOrWhiteSpace(oldColumn.NotNullName)
                ? $"-- TODO: The NOT NULL of column {quotedColumn} of {qualifiedName} {(newColumn.NotNullNoInherit ? "became" : "is no longer")} NO INHERIT, but its constraint's name is not known; run ALTER TABLE ... ALTER CONSTRAINT <name> {(newColumn.NotNullNoInherit ? "NO INHERIT" : "INHERIT")}."
                : $"ALTER TABLE {qualifiedName} ALTER CONSTRAINT {QuoteIdentifier(oldColumn.NotNullName)} {(newColumn.NotNullNoInherit ? "NO INHERIT" : "INHERIT")};");
        }

        if (newColumn.AllowsNull || oldNotValid == newNotValid)
        {
            return;
        }

        if (newNotValid)
        {
            statements.Add($"ALTER TABLE {qualifiedName} ALTER COLUMN {quotedColumn} DROP NOT NULL;");
            statements.Add(CreateOperation.BuildNotValidNotNullSql(qualifiedName, newColumn));
        }
        else if (string.IsNullOrWhiteSpace(oldColumn.NotNullConstraintName))
        {
            statements.Add($"-- TODO: The NOT NULL of column {quotedColumn} of {qualifiedName} is NOT VALID and became valid, but its constraint's name is not known; run ALTER TABLE ... VALIDATE CONSTRAINT with it.");
        }
        else
        {
            statements.Add($"ALTER TABLE {qualifiedName} VALIDATE CONSTRAINT {QuoteIdentifier(oldColumn.NotNullConstraintName)};");
        }
    }

    private static string GenerateAlterTable(TableDefinition oldTable, TableDefinition newTable, IReadOnlyList<ModificationDetail> details)
    {
        var qualifiedName = FormatQualifiedName(newTable.SchemaName, newTable.Name);

        var oldColumns = oldTable.Columns
            .Where(static column => column is not null)
            .ToDictionary(static column => column.Name, PgProj.Parser.SqlIdentifiers.NameComparer);
        var newColumns = newTable.Columns
            .Where(static column => column is not null)
            .ToDictionary(static column => column.Name, PgProj.Parser.SqlIdentifiers.NameComparer);

        var statements = new List<string>();

        if (details.Any(static detail => detail.PropertyName == nameof(TableDefinition.WithoutCluster)))
        {
            statements.Add(CommentSqlHelper.GenerateWithoutCluster(newTable.SchemaName, newTable.Name));
        }

        // The replica identity, when the project states one that differs (G-255); USING INDEX is the index's statement.
        if (newTable.ReplicaIdentity is "DEFAULT" or "FULL" or "NOTHING" && !string.Equals(oldTable.ReplicaIdentity ?? "DEFAULT", newTable.ReplicaIdentity, StringComparison.Ordinal))
        {
            statements.Add(CommentSqlHelper.GenerateReplicaIdentity(newTable.SchemaName, newTable.Name, newTable.ReplicaIdentity));
        }

        // A typed table's columns are its type's fields (G-136): they are added, dropped, and retyped by ALTER TYPE ... CASCADE, which the type's own change has
        // already done by the time this runs (the table depends on the type), so only what a table can change about a column is changed here. A table that becomes
        // typed, or stops being typed, or changes its type, is converted with OF and NOT OF, which PostgreSQL allows when the columns already match.
        var typed = newTable.IsTypedTable;
        if (!string.Equals(oldTable.OfTypeKey, newTable.OfTypeKey, StringComparison.Ordinal))
        {
            if (oldTable.IsTypedTable)
            {
                statements.Add($"ALTER TABLE {qualifiedName} NOT OF;");
            }

            if (newTable.IsTypedTable)
            {
                statements.Add($"ALTER TABLE {qualifiedName} OF {FormatQualifiedName(newTable.OfTypeSchemaName, newTable.OfTypeName!)};");
            }
        }

        // INHERITS (G-194): a parent removed is NO INHERIT before the columns change (its columns then become the child's own, and can be dropped); a parent added is
        // INHERIT after them (PostgreSQL wants the child to have the parent's columns first). A column inherited from a parent kept on both sides is changed on the
        // parent, and PostgreSQL carries the change to the child, so it is skipped here.
        var oldParents = oldTable.InheritsFrom.ToHashSet(PgProj.Parser.SqlIdentifiers.NameComparer);
        var newParents = newTable.InheritsFrom.ToHashSet(PgProj.Parser.SqlIdentifiers.NameComparer);
        var keptParents = oldParents.Intersect(newParents, PgProj.Parser.SqlIdentifiers.NameComparer).ToHashSet(PgProj.Parser.SqlIdentifiers.NameComparer);
        bool ThroughKeptParent(ColumnDefinition? column) => column?.InheritedFromTable is { } parent && keptParents.Contains(parent);
        foreach (var parent in oldTable.InheritsFrom.Where(parent => !newParents.Contains(parent)))
        {
            statements.Add($"ALTER TABLE {qualifiedName} NO INHERIT {CreateOperation.FormatTableKey(parent)};");
        }

        // Resolve explicit renames first so the columns are excluded from add/drop handling below
        // and so later ALTER COLUMN statements can target the column's new name.
        var renamedOldColumnNames = new HashSet<string>(PgProj.Parser.SqlIdentifiers.NameComparer);
        var renamePairs = new Dictionary<string, ColumnDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer);

        foreach (var newColumn in newTable.Columns)
        {
            if (string.IsNullOrWhiteSpace(newColumn.RenamedFrom))
            {
                continue;
            }

            if (!oldColumns.TryGetValue(newColumn.RenamedFrom, out var oldColumn)
                || renamedOldColumnNames.Contains(oldColumn.Name)
                || newColumns.ContainsKey(oldColumn.Name))
            {
                // Skip if the old name doesn't exist, was already claimed by another rename, or the
                // old name still exists as a separate column in the new schema (not a true rename).
                continue;
            }

            statements.Add($"ALTER TABLE {qualifiedName} RENAME COLUMN {QuoteIdentifier(oldColumn.Name)} TO {QuoteIdentifier(newColumn.Name)};");
            renamedOldColumnNames.Add(oldColumn.Name);
            renamePairs[newColumn.Name] = oldColumn;
        }

        foreach (var oldColumn in oldTable.Columns)
        {
            if (renamedOldColumnNames.Contains(oldColumn.Name))
            {
                continue;
            }

            if (!typed && !newColumns.ContainsKey(oldColumn.Name) && !ThroughKeptParent(oldColumn))
            {
                statements.Add($"ALTER TABLE {qualifiedName} DROP COLUMN {QuoteIdentifier(oldColumn.Name)};");
            }
        }

        foreach (var newColumn in newTable.Columns)
        {
            if (ThroughKeptParent(newColumn) && !newColumn.OverridesInheritedDefault && (!oldColumns.TryGetValue(newColumn.Name, out var inheritedBefore) || (inheritedBefore.InheritedFromTable is not null && !inheritedBefore.OverridesInheritedDefault)))
            {
                continue;
            }

            ColumnDefinition? oldColumn;
            if (renamePairs.TryGetValue(newColumn.Name, out var renamedOldColumn))
            {
                oldColumn = renamedOldColumn;
            }
            else if (!oldColumns.TryGetValue(newColumn.Name, out oldColumn))
            {
                if (typed)
                {
                    // The field is on the table once its type has it; what remains is the default, NOT NULL, or comment the table gives it, written as a change to an
                    // ordinary nullable column with nothing on it.
                    oldColumn = newColumn with { RawDefaultExpression = null, DefaultExpressionNode = null, IsNullable = true, IdentityGeneration = null, Comment = null };
                }
                else
                {
                    statements.Add($"ALTER TABLE {qualifiedName} ADD COLUMN {CreateOperation.BuildColumnDefinitionSql(newColumn)};");
                    if (newColumn is { IsStorageSetByAlter: true, Storage: not null })
                    {
                        statements.Add(CreateOperation.BuildSetStorageSql(qualifiedName, newColumn));
                    }

                    if (!newColumn.AllowsNull && newColumn.NotNullIsNotValid)
                    {
                        statements.Add(CreateOperation.BuildNotValidNotNullSql(qualifiedName, newColumn));
                    }

                    // A new column has no comment yet, so the project's is set with it (G-170); a new table gets its columns'
                    // comments from CreateOperation.
                    if (newColumn.Comment is not null)
                    {
                        statements.Add(CommentSqlHelper.GenerateColumnComment(newTable.SchemaName, newTable.Name, newColumn.Name, newColumn.Comment));
                    }

                    if (!newColumn.AllowsNull && newColumn.NotNullComment is not null && newColumn.NotNullName is not null)
                    {
                        statements.Add(CommentSqlHelper.GenerateNotNullConstraintComment(newTable.SchemaName, newTable.Name, newColumn.NotNullName, newColumn.NotNullComment));
                    }

                    continue;
                }
            }

            // A change to whether a column is generated, or to its expression (G-71). PostgreSQL 14 to 16 cannot change a stored
            // expression in place; a generated column's values are derived, so dropping and adding it loses nothing.
            var (oldGenerated, newGenerated) = ColumnDefinition.GetComparableGeneratedExpressions(oldColumn, newColumn);
            // STORED and VIRTUAL (PostgreSQL 18) cannot become one another in place either, and lose nothing when dropped and added (G-121).
            var expressionChanged = newColumn.IsGenerated
                && oldColumn.IsGenerated
                && (!string.Equals(oldGenerated, newGenerated, StringComparison.Ordinal) || oldColumn.IsVirtual != newColumn.IsVirtual);
            if (expressionChanged)
            {
                statements.Add($"ALTER TABLE {qualifiedName} DROP COLUMN {QuoteIdentifier(oldColumn.Name)};");
                statements.Add($"ALTER TABLE {qualifiedName} ADD COLUMN {CreateOperation.BuildColumnDefinitionSql(newColumn)};");
                    if (newColumn is { IsStorageSetByAlter: true, Storage: not null })
                    {
                        statements.Add(CreateOperation.BuildSetStorageSql(qualifiedName, newColumn));
                    }


                // Dropping the column dropped its comment too.
                if (newColumn.Comment is not null)
                {
                    statements.Add(CommentSqlHelper.GenerateColumnComment(newTable.SchemaName, newTable.Name, newColumn.Name, newColumn.Comment));
                }

                continue;
            }

            if (oldColumn.IsGenerated && !newColumn.IsGenerated)
            {
                // The values it holds stay, and the column becomes an ordinary one.
                statements.Add($"ALTER TABLE {qualifiedName} ALTER COLUMN {QuoteIdentifier(newColumn.Name)} DROP EXPRESSION;");
            }
            else if (!oldColumn.IsGenerated && newColumn.IsGenerated)
            {
                // There is no ALTER that makes an ordinary column generated; dropping and adding it would discard what it holds, so it is left to the user.
                statements.Add($"-- TODO: Column {QuoteIdentifier(newColumn.Name)} of {qualifiedName} became a generated column. PostgreSQL cannot convert an existing column in place; drop and add it (its current values are lost).");
                continue;
            }

            if (!typed && !string.Equals(oldColumn.GetComparableTypeSignature(), newColumn.GetComparableTypeSignature(), StringComparison.OrdinalIgnoreCase))
            {
                var quotedColumn = QuoteIdentifier(newColumn.Name);
                var newType = CreateOperation.BuildColumnTypeSql(newColumn);
                statements.Add($"ALTER TABLE {qualifiedName} ALTER COLUMN {quotedColumn} TYPE {newType} USING {quotedColumn}::{newType};");
            }

            // An identity is dropped before its column may allow nulls, since PostgreSQL refuses DROP NOT NULL on an identity column (G-214). Its sequence goes with it.
            var oldIdentity = string.IsNullOrWhiteSpace(oldColumn.IdentityGeneration) ? null : oldColumn.IdentityGeneration;
            var newIdentity = string.IsNullOrWhiteSpace(newColumn.IdentityGeneration) ? null : newColumn.IdentityGeneration;
            if (oldIdentity is not null && newIdentity is null)
            {
                statements.Add($"ALTER TABLE {qualifiedName} ALTER COLUMN {QuoteIdentifier(newColumn.Name)} DROP IDENTITY;");
            }

            AddNullabilityChange(statements, qualifiedName, oldColumn, newColumn);
            AddNotNullNameChange(statements, newTable, qualifiedName, oldColumn, newColumn);

            if (oldIdentity is null && newIdentity is not null)
            {
                AddIdentity(statements, qualifiedName, newColumn);
            }
            else if (oldIdentity is not null && newIdentity is not null && !string.Equals(oldIdentity, newIdentity, StringComparison.OrdinalIgnoreCase))
            {
                statements.Add($"ALTER TABLE {qualifiedName} ALTER COLUMN {QuoteIdentifier(newColumn.Name)} SET GENERATED {newIdentity};");
            }

            // The default is emitted as written, but whether it changed is decided on the comparison form, so
            // PostgreSQL's rendering ('x'::text) of an unchanged default does not become a SET DEFAULT (G-56, G-102).
            var newDefault = newColumn.IsGenerated ? null : newColumn.RawDefaultExpression;
            var (oldComparableDefault, newComparableDefault) = ColumnDefinition.GetComparableDefaultExpressions(oldColumn, newColumn);
            if (string.IsNullOrWhiteSpace(newColumn.IdentityGeneration)
                && string.IsNullOrWhiteSpace(oldColumn.IdentityGeneration)
                && !string.Equals(oldComparableDefault, newComparableDefault, StringComparison.Ordinal))
            {
                statements.Add(string.IsNullOrWhiteSpace(newDefault)
                    ? $"ALTER TABLE {qualifiedName} ALTER COLUMN {QuoteIdentifier(newColumn.Name)} DROP DEFAULT;"
                    : $"ALTER TABLE {qualifiedName} ALTER COLUMN {QuoteIdentifier(newColumn.Name)} SET DEFAULT {newDefault};");
            }

            if (!string.Equals(oldColumn.Comment, newColumn.Comment, StringComparison.Ordinal))
            {
                statements.Add(CommentSqlHelper.GenerateColumnComment(newTable.SchemaName, newTable.Name, newColumn.Name, newColumn.Comment));
            }

            // In place; neither rewrites the rows already stored (G-222).
            if (ColumnDefinition.IsStorageChange(oldColumn, newColumn))
            {
                statements.Add($"ALTER TABLE {qualifiedName} ALTER COLUMN {QuoteIdentifier(newColumn.Name)} SET STORAGE {newColumn.Storage};");
            }

            if (ColumnDefinition.IsCompressionChange(oldColumn, newColumn))
            {
                statements.Add($"ALTER TABLE {qualifiedName} ALTER COLUMN {QuoteIdentifier(newColumn.Name)} SET COMPRESSION {newColumn.Compression};");
            }
        }

        // Storage parameters (G-24b): a changed or added option is SET, a removed one is RESET back to its default; PostgreSQL
        // has no single ALTER that replaces the whole list, so this is the same add/remove reasoning as a column.
        var oldOptionsByName = oldTable.NormalizedStorageOptions.ToDictionary(static option => option.Name, StringComparer.Ordinal);
        var newOptionsByName = newTable.NormalizedStorageOptions.ToDictionary(static option => option.Name, StringComparer.Ordinal);

        // Emitted in source order (G-112); the sorted NormalizedStorageOptions are only for lookup and comparison.
        var setOptions = newTable.StorageOptions
            .Where(option => !oldOptionsByName.TryGetValue(option.Name, out var existing) || !string.Equals(existing.ComparisonText, option.ComparisonText, StringComparison.Ordinal))
            .Select(static option => option.RawText)
            .ToArray();
        if (setOptions.Length > 0)
        {
            statements.Add($"ALTER TABLE {qualifiedName} SET ({string.Join(", ", setOptions)});");
        }

        var resetOptions = oldTable.StorageOptions
            .Where(option => !newOptionsByName.ContainsKey(option.Name))
            .Select(static option => option.Name)
            .ToArray();
        if (resetOptions.Length > 0)
        {
            statements.Add($"ALTER TABLE {qualifiedName} RESET ({string.Join(", ", resetOptions)});");
        }

        if (oldTable.IsUnlogged != newTable.IsUnlogged)
        {
            statements.Add($"ALTER TABLE {qualifiedName} SET {(newTable.IsUnlogged ? "UNLOGGED" : "LOGGED")};");
        }

        // G-76: these are genuine in-place ALTER TABLE toggles, unlike a policy's own definition (drop-then-recreate; see
        // ScriptGenerator.IsDropAndRecreateObject), since PostgreSQL has no other way to change them.
        if (oldTable.IsRowSecurityEnabled != newTable.IsRowSecurityEnabled)
        {
            statements.Add($"ALTER TABLE {qualifiedName} {(newTable.IsRowSecurityEnabled ? "ENABLE" : "DISABLE")} ROW LEVEL SECURITY;");
        }

        if (oldTable.IsRowSecurityForced != newTable.IsRowSecurityForced)
        {
            statements.Add($"ALTER TABLE {qualifiedName} {(newTable.IsRowSecurityForced ? "FORCE" : "NO FORCE")} ROW LEVEL SECURITY;");
        }

        if (!string.Equals(oldTable.TablespaceName, newTable.TablespaceName, StringComparison.Ordinal))
        {
            if (newTable.TablespaceName is null)
            {
                // There is no ALTER TABLE ... RESET TABLESPACE; the database's default tablespace is not known here, and
                // guessing pg_default would be wrong for a database whose default is something else, so this is left to the user.
                statements.Add($"-- TODO: Table {qualifiedName} no longer specifies a TABLESPACE. PostgreSQL has no ALTER TABLE ... RESET TABLESPACE; name the database's actual default tablespace explicitly.");
            }
            else
            {
                statements.Add($"ALTER TABLE {qualifiedName} SET TABLESPACE {QuoteIdentifier(newTable.TablespaceName)};");
            }
        }

        // In place (G-223). PostgreSQL rewrites the table to do it.
        if (newTable.AccessMethod is not null && !string.Equals(oldTable.AccessMethod, newTable.AccessMethod, StringComparison.Ordinal))
        {
            statements.Add($"ALTER TABLE {qualifiedName} SET ACCESS METHOD {QuoteIdentifier(newTable.AccessMethod)};");
        }

        // A changed partition strategy/key means dropping and recreating the whole hierarchy (no in-place ALTER changes a
        // partitioned table's key); a changed parent or bound means ATTACH/DETACH PARTITION. Neither is attempted this pass
        // (G-24c) — a changed strategy is destructive and belongs with G-35's policy, and ATTACH/DETACH has its own edge cases
        // (concurrent detach, existing-data validation) better handled as its own piece of work. This blocks `--execute`
        // (G-28) rather than silently leaving the live table on its old partitioning.
        if (!string.Equals(oldTable.PartitionStrategy, newTable.PartitionStrategy, StringComparison.OrdinalIgnoreCase)
            || !PgProj.Diff.DiffEngine.SamePartitionKey(oldTable.PartitionKeyElements, newTable.PartitionKeyElements))
        {
            statements.Add($"-- TODO: Table {qualifiedName}'s partition key changed. PostgreSQL has no in-place ALTER for a partitioned table's PARTITION BY; dropping and recreating the whole hierarchy is destructive and is not attempted here.");
        }
        else if (!string.Equals(oldTable.PartitionOfSchemaName, newTable.PartitionOfSchemaName, StringComparison.Ordinal)
            || !string.Equals(oldTable.PartitionOfTableName, newTable.PartitionOfTableName, StringComparison.Ordinal)
            || !PartitionBoundsMatch(oldTable, newTable))
        {
            statements.Add($"-- TODO: Table {qualifiedName}'s partition parent or bound changed. Use ALTER TABLE ... ATTACH/DETACH PARTITION; not generated here.");
        }

        // A parent added once the child has its columns (G-194).
        foreach (var parent in newTable.InheritsFrom.Where(parent => !oldParents.Contains(parent)))
        {
            statements.Add($"ALTER TABLE {qualifiedName} INHERIT {CreateOperation.FormatTableKey(parent)};");
        }

        if (!string.Equals(oldTable.Comment, newTable.Comment, StringComparison.Ordinal))
        {
            statements.Add(newTable.Comment is null
                ? CommentSqlHelper.GenerateTableCommentClear(newTable.SchemaName, newTable.Name)
                : CommentSqlHelper.GenerateTableComment(newTable.SchemaName, newTable.Name, newTable.Comment)!);
        }

        return statements.Count == 0
            ? $"-- No column-level changes detected for table '{newTable.Key}'."
            : string.Join("\n", statements);
    }

    /// <summary>
    /// One human-readable reason per column-level change in a table alter that can discard data (G-35): a column
    /// removed (not renamed), or a column's type changed. Mirrors the same rename/removal detection
    /// <see cref="GenerateAlterTable"/> uses (a separate, self-contained pass rather than a shared helper, so this
    /// classification can never change what SQL is generated). Deliberately conservative: any type change is
    /// reported, even a widening one PostgreSQL could execute losslessly (<c>integer</c> to <c>bigint</c>), because
    /// telling a safe widening from a truncating narrowing in general needs per-type knowledge this pass does not
    /// attempt (a known limit of G-35). A column that changed from
    /// ordinary to generated is excluded: it already blocks <c>--execute</c> as an unsupported placeholder (G-28),
    /// so it never reaches here as an executable, silently destructive statement. A generated column's expression
    /// changing is also excluded: its values are recomputed from other columns, not discarded (the same reasoning
    /// as the <c>DROP COLUMN</c>/<c>ADD COLUMN</c> pair <see cref="GenerateAlterTable"/> emits for it).
    /// </summary>
    internal static IReadOnlyList<string> GetDestructiveColumnReasons(TableDefinition oldTable, TableDefinition newTable)
    {
        // A typed table's columns are its type's fields, and dropping or retyping one is the type's change, which reports it (G-136).
        if (newTable.IsTypedTable)
        {
            return [];
        }

        var oldColumns = oldTable.Columns
            .Where(static column => column is not null)
            .ToDictionary(static column => column.Name, PgProj.Parser.SqlIdentifiers.NameComparer);
        var newColumns = newTable.Columns
            .Where(static column => column is not null)
            .ToDictionary(static column => column.Name, PgProj.Parser.SqlIdentifiers.NameComparer);

        var renamedOldColumnNames = new HashSet<string>(PgProj.Parser.SqlIdentifiers.NameComparer);
        var renamePairs = new Dictionary<string, ColumnDefinition>(PgProj.Parser.SqlIdentifiers.NameComparer);
        foreach (var newColumn in newTable.Columns)
        {
            if (string.IsNullOrWhiteSpace(newColumn.RenamedFrom))
            {
                continue;
            }

            if (!oldColumns.TryGetValue(newColumn.RenamedFrom, out var oldColumn)
                || renamedOldColumnNames.Contains(oldColumn.Name)
                || newColumns.ContainsKey(oldColumn.Name))
            {
                continue;
            }

            renamedOldColumnNames.Add(oldColumn.Name);
            renamePairs[newColumn.Name] = oldColumn;
        }

        var reasons = new List<string>();

        foreach (var oldColumn in oldTable.Columns)
        {
            if (!renamedOldColumnNames.Contains(oldColumn.Name) && !newColumns.ContainsKey(oldColumn.Name))
            {
                reasons.Add($"column \"{oldColumn.Name}\" removed");
            }
        }

        foreach (var newColumn in newTable.Columns)
        {
            ColumnDefinition? oldColumn;
            if (renamePairs.TryGetValue(newColumn.Name, out var renamedOldColumn))
            {
                oldColumn = renamedOldColumn;
            }
            else if (!oldColumns.TryGetValue(newColumn.Name, out oldColumn))
            {
                continue;
            }

            if (oldColumn.IsGenerated && newColumn.IsGenerated)
            {
                continue;
            }

            if (!oldColumn.IsGenerated && newColumn.IsGenerated)
            {
                // Blocked as an unsupported placeholder (G-28); never reaches --execute.
                continue;
            }

            // A change that keeps every value (varchar(50) to varchar(100), int to bigint) loses nothing (G-281); whether it rewrites the table is the lock warning's.
            if (!string.Equals(oldColumn.GetComparableTypeSignature(), newColumn.GetComparableTypeSignature(), StringComparison.OrdinalIgnoreCase)
                && !ColumnTypeWidening.KeepsEveryValue(oldColumn, newColumn))
            {
                reasons.Add($"column \"{newColumn.Name}\" type changed ({oldColumn.GetComparableTypeSignature()} -> {newColumn.GetComparableTypeSignature()})");
            }
        }

        return reasons;
    }

    /// <summary>
    /// The options an identity's column states, changed with <c>ALTER TABLE ... ALTER COLUMN ... SET</c> (G-214). A changed <c>START WITH</c> is recorded
    /// with <c>SET START WITH</c>, which does not move the counter: restarting it could hand out values the column already holds.
    /// </summary>
    private static string GenerateAlterIdentity(SequenceDefinition sequence, IReadOnlySet<string> changedProperties)
    {
        var effective = sequence.GetEffectiveOptions();
        var clauses = new List<string>();
        if (changedProperties.Contains(nameof(SequenceDefinition.IncrementBy)))
        {
            clauses.Add($"SET INCREMENT BY {sequence.IncrementBy ?? effective.IncrementBy}");
        }

        if (changedProperties.Contains(nameof(SequenceDefinition.MinValue)))
        {
            clauses.Add(sequence.MinValue is { } minimum ? $"SET MINVALUE {minimum}" : "SET NO MINVALUE");
        }

        if (changedProperties.Contains(nameof(SequenceDefinition.MaxValue)))
        {
            clauses.Add(sequence.MaxValue is { } maximum ? $"SET MAXVALUE {maximum}" : "SET NO MAXVALUE");
        }

        if (changedProperties.Contains(nameof(SequenceDefinition.StartValue)))
        {
            clauses.Add($"SET START WITH {sequence.StartValue ?? effective.StartValue}");
        }

        if (changedProperties.Contains(nameof(SequenceDefinition.CacheSize)))
        {
            clauses.Add($"SET CACHE {sequence.CacheSize ?? effective.CacheSize}");
        }

        if (changedProperties.Contains(nameof(SequenceDefinition.Cycle)))
        {
            clauses.Add(sequence.Cycle == true ? "SET CYCLE" : "SET NO CYCLE");
        }

        // Whether the sequence is logged is not an option of the column: it is the sequence's own, changed with ALTER SEQUENCE (G-294).
        var persistence = changedProperties.Contains(nameof(SequenceDefinition.IsUnlogged))
            ? $"ALTER SEQUENCE {FormatQualifiedName(sequence.SchemaName, sequence.Name)} SET {(sequence.IsUnlogged ? "UNLOGGED" : "LOGGED")};"
            : null;
        if (clauses.Count == 0)
        {
            return persistence ?? $"-- No changes to the options of identity sequence '{sequence.Key}'.";
        }

        var table = sequence.OwnedByTable!.Split('.', 2);
        var qualifiedTable = table.Length == 2 ? FormatQualifiedName(table[0], table[1]) : FormatQualifiedName(sequence.SchemaName, sequence.OwnedByTable!);
        var alterColumn = $"ALTER TABLE {qualifiedTable} ALTER COLUMN {QuoteIdentifier(sequence.OwnedByColumn!)} {string.Join(" ", clauses)};";
        return persistence is null ? alterColumn : alterColumn + "\n" + persistence;
    }

    private static string GenerateAlterSequence(SequenceDefinition sequence, IReadOnlyList<ModificationDetail> details)
    {
        var changedProperties = details.Select(static detail => detail.PropertyName).ToHashSet(StringComparer.Ordinal);
        if (changedProperties.Count == 0)
        {
            return $"-- No changes detected for sequence '{sequence.Key}'.";
        }

        if (sequence.IsIdentityOwned && !string.IsNullOrWhiteSpace(sequence.OwnedByTable) && !string.IsNullOrWhiteSpace(sequence.OwnedByColumn))
        {
            return GenerateAlterIdentity(sequence, changedProperties);
        }

        var qualifiedName = FormatQualifiedName(sequence.SchemaName, sequence.Name);

        // Comment is not a sequence option; it needs no ALTER SEQUENCE clause (G-34). Without this, a comment-only change built
        // a bare "ALTER SEQUENCE name;" with no action, which PostgreSQL rejects as a syntax error.
        var hasOptionChange = changedProperties.Any(static property => property is not (nameof(SequenceDefinition.Comment) or nameof(SequenceDefinition.IsUnlogged)));

        var builder = new System.Text.StringBuilder();
        if (hasOptionChange)
        {
            builder.Append("ALTER SEQUENCE ").Append(qualifiedName);
        }

        // The diff compares options with PostgreSQL's defaults applied, so an option the target left implicit can still be
        // a reported change. It is then set to its default rather than skipped (G-57).
        var effective = sequence.GetEffectiveOptions();

        if (changedProperties.Contains(nameof(SequenceDefinition.DataType)))
        {
            builder.Append(" AS ").Append(CreateOperation.FormatFriendlyTypeName(string.IsNullOrWhiteSpace(sequence.DataType) ? effective.DataType : sequence.DataType));
        }

        if (changedProperties.Contains(nameof(SequenceDefinition.IncrementBy)))
        {
            builder.Append(" INCREMENT BY ").Append(string.IsNullOrWhiteSpace(sequence.IncrementBy) ? effective.IncrementBy : sequence.IncrementBy);
        }

        if (changedProperties.Contains(nameof(SequenceDefinition.MinValue)))
        {
            builder.Append(string.IsNullOrWhiteSpace(sequence.MinValue) ? " NO MINVALUE" : $" MINVALUE {sequence.MinValue}");
        }

        if (changedProperties.Contains(nameof(SequenceDefinition.MaxValue)))
        {
            builder.Append(string.IsNullOrWhiteSpace(sequence.MaxValue) ? " NO MAXVALUE" : $" MAXVALUE {sequence.MaxValue}");
        }

        // A changed start is recorded, and the counter left where it is (G-220): RESTART moved it, which could hand out values already used, and did not
        // change the recorded start, so the change was found again on every publish.
        if (changedProperties.Contains(nameof(SequenceDefinition.StartValue)))
        {
            builder.Append(" START WITH ").Append(string.IsNullOrWhiteSpace(sequence.StartValue) ? effective.StartValue : sequence.StartValue);
        }

        if (changedProperties.Contains(nameof(SequenceDefinition.CacheSize)))
        {
            builder.Append(" CACHE ").Append(string.IsNullOrWhiteSpace(sequence.CacheSize) ? effective.CacheSize : sequence.CacheSize);
        }

        if (changedProperties.Contains(nameof(SequenceDefinition.Cycle)))
        {
            builder.Append(sequence.Cycle == true ? " CYCLE" : " NO CYCLE");
        }

        if (changedProperties.Contains(nameof(SequenceDefinition.OwnedByTable)) || changedProperties.Contains(nameof(SequenceDefinition.OwnedByColumn)))
        {
            builder.Append(" OWNED BY ").Append(
                !string.IsNullOrWhiteSpace(sequence.OwnedByTable) && !string.IsNullOrWhiteSpace(sequence.OwnedByColumn)
                    ? $"{sequence.OwnedByTable}.{QuoteIdentifier(sequence.OwnedByColumn)}"
                    : "NONE");
        }

        var statements = new List<string>();
        if (hasOptionChange)
        {
            statements.Add(builder.Append(';').ToString());
        }

        // A statement of its own: SET LOGGED/UNLOGGED takes no other option (G-224).
        if (changedProperties.Contains(nameof(SequenceDefinition.IsUnlogged)))
        {
            statements.Add($"ALTER SEQUENCE {qualifiedName} SET {(sequence.IsUnlogged ? "UNLOGGED" : "LOGGED")};");
        }

        if (changedProperties.Contains(nameof(SequenceDefinition.Comment)))
        {
            statements.Add(sequence.Comment is null
                ? CommentSqlHelper.GenerateSequenceCommentClear(sequence.SchemaName, sequence.Name)
                : CommentSqlHelper.GenerateSequenceComment(sequence.SchemaName, sequence.Name, sequence.Comment)!);
        }

        return string.Join("\n", statements);
    }

    private static string FormatQualifiedName(string? schemaName, string name)
        => string.IsNullOrWhiteSpace(schemaName)
            ? QuoteIdentifier(name)
            : $"{QuoteIdentifier(schemaName)}.{QuoteIdentifier(name)}";

    private static string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
