using PgProj.Semantics;

namespace PgProj.Diff;

public sealed record class ModifiedChange : ChangeRecord
{
    public required ISemanticNode OldNode { get; init; }

    public required ISemanticNode NewNode { get; init; }

    public required IReadOnlyList<ModificationDetail> Details { get; init; }
}
