using PgProj.Diff;
using PgProj.Semantics;

namespace PgProj.Deploy;

/// <summary>
/// Emits <c>ALTER SEQUENCE ... OWNED BY table.column</c> for a sequence that was just created (G-58).
/// <c>OWNED BY</c> needs the table to exist, but the table's <c>DEFAULT nextval(...)</c> needs the sequence to exist
/// first, so a sequence is created without ownership and handed to its column after every table has been created.
/// Like <see cref="SchemaCreateOperation"/>, this is synthesized rather than derived from a change of its own.
/// </summary>
public sealed record class SequenceOwnershipOperation : SqlOperation
{
    public required SequenceDefinition Sequence { get; init; }

    public override string GenerateSql()
    {
        var owner = Sequence.OwnedByTable ?? string.Empty;
        var separator = owner.IndexOf('.', StringComparison.Ordinal);
        var ownerName = separator < 0
            ? Quote(owner)
            : $"{Quote(owner[..separator])}.{Quote(owner[(separator + 1)..])}";

        return $"ALTER SEQUENCE {QualifiedName(Sequence)} OWNED BY {ownerName}.{Quote(Sequence.OwnedByColumn ?? string.Empty)};";
    }

    private static string QualifiedName(SequenceDefinition sequence)
        => string.IsNullOrWhiteSpace(sequence.SchemaName)
            ? Quote(sequence.Name)
            : $"{Quote(sequence.SchemaName)}.{Quote(sequence.Name)}";

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
