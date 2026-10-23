using PgProj.Semantics;

namespace PgProj.Deploy;

/// <summary>
/// The <c>ALTER ... OWNER TO</c> statement for an owner node (G-146). The object is written from its own definition, so a routine carries its argument
/// types and an operator its operands, as the statement that drops it would.
/// </summary>
internal static class OwnerSqlBuilder
{
    public static string Alter(OwnerDefinition owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var name = QualifiedName(owner);
        var target = owner.ObjectKind switch
        {
            "schema" => $"SCHEMA {Quote(owner.ObjectName)}",
            "table" => $"TABLE {name}",
            "view" => $"VIEW {name}",
            "materialized view" => $"MATERIALIZED VIEW {name}",
            "sequence" => $"SEQUENCE {name}",
            "type" => $"TYPE {name}",
            "domain" => $"DOMAIN {name}",
            "event trigger" => $"EVENT TRIGGER {Quote(owner.ObjectName)}",
            "publication" => $"PUBLICATION {Quote(owner.ObjectName)}",
            "collation" => $"COLLATION {name}",
            "statistics" => $"STATISTICS {name}",
            "text search dictionary" => $"TEXT SEARCH DICTIONARY {name}",
            "text search configuration" => $"TEXT SEARCH CONFIGURATION {name}",
            "function" when owner.Target is FunctionDefinition function => $"FUNCTION {name}({function.Signature?.RenderArgumentTypeSignatureSql() ?? string.Empty})",
            "procedure" when owner.Target is ProcedureDefinition procedure => $"PROCEDURE {name}({procedure.ArgumentIdentitySql})",
            "aggregate" when owner.Target is AggregateDefinition aggregate => $"AGGREGATE {CreateOperation.FormatAggregateName(aggregate)} ({CreateOperation.FormatAggregateArguments(aggregate)})",
            "operator" when owner.Target is OperatorDefinition @operator => $"OPERATOR {CreateOperation.FormatOperatorName(@operator)} ({CreateOperation.FormatOperatorOperands(@operator)})",
            _ => null,
        };

        return target is null
            ? $"-- TODO: Cannot emit the owner of {owner.ObjectKind} '{owner.ObjectKey}': its definition is not known."
            : $"ALTER {target} OWNER TO {Quote(owner.OwnerRole)};";
    }

    private static string QualifiedName(OwnerDefinition owner)
        => string.IsNullOrWhiteSpace(owner.SchemaName) ? Quote(owner.ObjectName) : $"{Quote(owner.SchemaName)}.{Quote(owner.ObjectName)}";

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
