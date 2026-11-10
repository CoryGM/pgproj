namespace PgProj.Deploy;

/// <summary>
/// <c>CREATE TYPE name;</c>, the shell of a base type the script creates (G-267, the user's design of 2026-11-09, item 2): its input and output functions name the type,
/// so the shell stands in for it until they exist and the full <c>CREATE TYPE</c> follows them. Written just before the first of those functions; synthesized, like
/// <see cref="SchemaCreateOperation"/>, from the type being created. Its <see cref="SqlOperation.NodeId"/> is the type's.
/// </summary>
public sealed record class BaseTypeShellOperation : SqlOperation
{
    public required string? SchemaName { get; init; }

    public required string TypeName { get; init; }

    public override string GenerateSql()
        => $"CREATE TYPE {(string.IsNullOrWhiteSpace(SchemaName) ? string.Empty : QuoteIdentifier(SchemaName) + ".")}{QuoteIdentifier(TypeName)};";

    private static string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
