namespace PgProj.Diff;

public sealed record class ModificationDetail
{
    public required string PropertyName { get; init; }

    public string? OldValue { get; init; }

    public string? NewValue { get; init; }
}
