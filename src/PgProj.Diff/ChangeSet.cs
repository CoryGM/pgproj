namespace PgProj.Diff;

public sealed record class ChangeSet
{
    public required IReadOnlyList<AddedChange> Added { get; init; }

    public required IReadOnlyList<RemovedChange> Removed { get; init; }

    public required IReadOnlyList<ModifiedChange> Modified { get; init; }
}
