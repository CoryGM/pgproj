namespace PgProj.Deploy;

/// <summary>
/// An operation pgproj knows is needed but cannot carry out, emitted as a <c>-- TODO:</c> placeholder so <c>publish --execute</c> refuses
/// the script (G-28) and names the object, rather than letting the server fail halfway (G-103).
/// </summary>
public sealed record class BlockedOperation : SqlOperation
{
    public required string Message { get; init; }

    public override string GenerateSql() => $"-- TODO: {Message}";
}
