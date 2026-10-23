using PgProj.Diff;

namespace PgProj.Deploy;

/// <summary>
/// Emits <c>CREATE SCHEMA IF NOT EXISTS</c> for a schema referenced by newly created objects.
/// Unlike other operations, this is not derived from a <see cref="Semantics.ISemanticNode"/> —
/// it is synthesized directly from the schema names seen across <see cref="ChangeSet.Added"/>.
/// </summary>
public sealed record class SchemaCreateOperation : SqlOperation
{
    public required string SchemaName { get; init; }

    public override string GenerateSql() => $"CREATE SCHEMA IF NOT EXISTS {QuoteIdentifier(SchemaName)};";

    private static string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
