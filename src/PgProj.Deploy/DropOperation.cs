using PgProj.Semantics;

namespace PgProj.Deploy;

public sealed record class DropOperation : SqlOperation
{
    public required ISemanticNode Node { get; init; }

    public override string GenerateSql()
        => Node switch
        {
            TableDefinition table => $"DROP TABLE {FormatQualifiedName(table.SchemaName, table.Name)};",
            ViewDefinition view => view.IsMaterialized
                ? $"DROP MATERIALIZED VIEW {FormatQualifiedName(view.SchemaName, view.Name)};"
                : $"DROP VIEW {FormatQualifiedName(view.SchemaName, view.Name)};",
            FunctionDefinition function => $"DROP FUNCTION {FormatQualifiedName(function.SchemaName, function.Name)}({function.Signature?.RenderArgumentTypeSignatureSql() ?? string.Empty});",
            ProcedureDefinition procedure => $"DROP PROCEDURE {FormatQualifiedName(procedure.SchemaName, procedure.Name)}({procedure.ArgumentIdentitySql});",
            TriggerDefinition trigger => trigger.TableName is { Length: > 0 } triggerTable
                ? $"DROP TRIGGER {QuoteIdentifier(trigger.Name)} ON {FormatQualifiedName(trigger.SchemaName, triggerTable)};"
                : $"-- TODO: Cannot emit DROP TRIGGER for '{trigger.Key}' without its table.",
            TypeDefinition type when IsExtension(type) => $"DROP EXTENSION {QuoteIdentifier(type.Name)};",
            TypeDefinition type when IsDomain(type) => $"DROP DOMAIN {FormatQualifiedName(type.SchemaName, type.Name)};",
            TypeDefinition type => $"DROP TYPE {FormatQualifiedName(type.SchemaName, type.Name)};",
            IndexDefinition index => $"DROP INDEX {FormatQualifiedName(index.SchemaName, index.Name)};",
            ConstraintDefinition constraint => $"ALTER TABLE {FormatQualifiedName(constraint.SchemaName, constraint.TableName ?? "<table>")} DROP CONSTRAINT {QuoteIdentifier(constraint.Name)};",
            EventTriggerDefinition eventTrigger => $"DROP EVENT TRIGGER {QuoteIdentifier(eventTrigger.Name)};",
            CastDefinition cast => $"DROP CAST ({CreateOperation.FormatCastType(cast.SourceType, cast.WrittenTypes)} AS {CreateOperation.FormatCastType(cast.TargetType, cast.WrittenTypes)});",
            OperatorDefinition definition => $"DROP OPERATOR {CreateOperation.FormatOperatorName(definition)} ({CreateOperation.FormatOperatorOperands(definition)});",
            RuleDefinition rule => $"DROP RULE {QuoteIdentifier(rule.Name)} ON {FormatQualifiedName(rule.SchemaName, rule.TableName ?? "<table>")};",
            TextSearchDictionaryDefinition dictionary => TextSearchSqlBuilder.DropDictionary(dictionary),
            TextSearchConfigurationDefinition configuration => TextSearchSqlBuilder.DropConfiguration(configuration),
            StatisticsDefinition statistics => $"DROP STATISTICS {CreateOperation.FormatStatisticsName(statistics)};",
            CollationDefinition collation => $"DROP COLLATION {CreateOperation.FormatCollationName(collation)};",
            AggregateDefinition aggregate => $"DROP AGGREGATE {CreateOperation.FormatAggregateName(aggregate)} ({CreateOperation.FormatAggregateArguments(aggregate)});",
            PublicationDefinition publication => $"DROP PUBLICATION {QuoteIdentifier(publication.Name)};",
            PolicyDefinition policy => $"DROP POLICY {QuoteIdentifier(policy.Name)} ON {FormatQualifiedName(policy.SchemaName, policy.TableName ?? "<table>")};",
            SequenceDefinition sequence => sequence.IsIdentityOwned
                ? $"-- Sequence {FormatQualifiedName(sequence.SchemaName, sequence.Name)} is dropped implicitly with its identity column/table; no explicit DROP SEQUENCE required."
                : IsOwnedByColumn(sequence)
                    // Dropping the owning table drops the sequence with it, and the table is dropped first (it depends on
                    // the sequence), so by now the sequence is usually gone (G-58).
                    ? $"DROP SEQUENCE IF EXISTS {FormatQualifiedName(sequence.SchemaName, sequence.Name)};"
                    : $"DROP SEQUENCE {FormatQualifiedName(sequence.SchemaName, sequence.Name)};",
            GrantDefinition grant => GenerateRevoke(grant),
            _ => $"-- TODO: Generate DROP SQL for {NodeKind} '{NodeId}'.",
        };

    private static bool IsOwnedByColumn(SequenceDefinition sequence)
        => !string.IsNullOrWhiteSpace(sequence.OwnedByTable) && !string.IsNullOrWhiteSpace(sequence.OwnedByColumn);

    private static string GenerateRevoke(GrantDefinition grant) => GrantSqlBuilder.Revoke(grant);

    private static string FormatQualifiedName(string? schemaName, string name)
        => string.IsNullOrWhiteSpace(schemaName)
            ? QuoteIdentifier(name)
            : $"{QuoteIdentifier(schemaName)}.{QuoteIdentifier(name)}";

    private static string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
