using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-121 part 2: a key that gains or loses <c>WITHOUT OVERLAPS</c> (or <c>PERIOD</c>) is a modified constraint, and so is a temporal UNIQUE whose
/// columns are the same set with a different one last, since the last is the range that may not overlap. A UNIQUE that is not temporal is still
/// compared as a set (G-112).
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class TemporalKeyDiffTests
{
    private const string Room = "CREATE TABLE public.room (id integer, during tsrange, CONSTRAINT k PRIMARY KEY (id{0}));";
    private const string Unique = "CREATE TABLE public.t (a integer, b integer, r tsrange, CONSTRAINT k UNIQUE ({0}));";
    private const string Booking = "CREATE TABLE public.room (id integer, during tsrange, PRIMARY KEY (id, during WITHOUT OVERLAPS)); CREATE TABLE public.booking (room_id integer, during tsrange, CONSTRAINT k FOREIGN KEY ({0}) REFERENCES public.room ({1}));";

    [TestMethod]
    public void ATemporalKeyDiffedWithItselfIsEmpty()
    {
        var model = Build(string.Format(Room, ", during WITHOUT OVERLAPS"));

        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(model, model)));
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(Build(string.Format(Room, ", during WITHOUT OVERLAPS")), Build(string.Format(Room, ", during WITHOUT OVERLAPS")))));
    }

    [TestMethod]
    public void ATemporalForeignKeyDiffedWithItselfIsEmpty()
    {
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(
            Build(string.Format(Booking, "room_id, PERIOD during", "id, PERIOD during")),
            Build(string.Format(Booking, "room_id, PERIOD during", "id, PERIOD during")))));
    }

    [TestMethod]
    [DataRow(", during", ", during WITHOUT OVERLAPS")]
    [DataRow(", during WITHOUT OVERLAPS", ", during")]
    public void AKeyThatGainsOrLosesWithoutOverlapsIsReported(string before, string after)
    {
        var diff = new DiffEngine().Diff(Build(string.Format(Room, before)), Build(string.Format(Room, after)));

        var detail = diff.Modified.Single(static change => change.NodeId.EndsWith(".k", StringComparison.OrdinalIgnoreCase)).Details.Single();
        Assert.AreEqual(nameof(ConstraintDefinition.WithoutOverlaps), detail.PropertyName);
        Assert.AreEqual(before.Contains("WITHOUT", StringComparison.Ordinal).ToString(), detail.OldValue?.ToString());
        Assert.AreEqual(after.Contains("WITHOUT", StringComparison.Ordinal).ToString(), detail.NewValue?.ToString());
    }

    [TestMethod]
    [DataRow("room_id, during", "id, during", "room_id, PERIOD during", "id, PERIOD during")]
    [DataRow("room_id, PERIOD during", "id, PERIOD during", "room_id, during", "id, during")]
    public void AForeignKeyThatGainsOrLosesPeriodIsReported(string beforeLocal, string beforeReferenced, string afterLocal, string afterReferenced)
    {
        var diff = new DiffEngine().Diff(Build(string.Format(Booking, beforeLocal, beforeReferenced)), Build(string.Format(Booking, afterLocal, afterReferenced)));

        var detail = diff.Modified.Single(static change => change.NodeId.EndsWith(".k", StringComparison.OrdinalIgnoreCase)).Details.Single();
        Assert.AreEqual(nameof(ConstraintDefinition.ForeignKeyUsesPeriod), detail.PropertyName);
    }

    [TestMethod]
    public void ATemporalUniqueWithADifferentColumnLastIsReported()
    {
        // Arrange: the same two columns, a range last in one and a plain column last in the other.
        var diff = new DiffEngine().Diff(
            Build(string.Format(Unique, "a, r WITHOUT OVERLAPS")),
            Build(string.Format(Unique, "r, a WITHOUT OVERLAPS")));

        // Assert
        var detail = diff.Modified.Single(static change => change.NodeId.EndsWith(".k", StringComparison.OrdinalIgnoreCase)).Details.Single();
        Assert.AreEqual(nameof(ConstraintDefinition.UniqueColumns), detail.PropertyName);
    }

    [TestMethod]
    public void AUniqueThatIsNotTemporalIsStillComparedAsASet()
    {
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(Build(string.Format(Unique, "a, b")), Build(string.Format(Unique, "b, a")))));
    }

    private static bool IsEmpty(ChangeSet diff)
        => diff.Added.Count + diff.Removed.Count + diff.Modified.Count == 0;

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
