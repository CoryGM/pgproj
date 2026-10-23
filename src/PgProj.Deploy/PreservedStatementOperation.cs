using PgProj.Diff;

namespace PgProj.Deploy;

/// <summary>
/// A statement that puts back something the live database had on an object this publish drops and recreates, and that the project
/// does not model: a privilege of a project that leaves grants out (G-116). It is emitted as read from the database, with a
/// <see cref="SqlOperation.Reason"/> that says why it is there. Like <see cref="SequenceOwnershipOperation"/>, it is synthesized rather than derived from a change.
/// </summary>
public sealed record class PreservedStatementOperation : SqlOperation
{
    /// <summary>The statement, as the live database's catalog gives it, without a trailing semicolon.</summary>
    public required string Statement { get; init; }

    public override string GenerateSql() => $"{Statement.TrimEnd().TrimEnd(';')};";
}
