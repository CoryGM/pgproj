using PgProj.Semantics;

namespace PgProj.Diff;

public sealed record class RemovedChange : ChangeRecord
{
    public required ISemanticNode Node { get; init; }
}
