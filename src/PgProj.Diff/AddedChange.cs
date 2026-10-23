using PgProj.Semantics;

namespace PgProj.Diff;

public sealed record class AddedChange : ChangeRecord
{
    public required ISemanticNode Node { get; init; }
}
