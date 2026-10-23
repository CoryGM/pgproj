using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>G-121 part 2: a temporal key (PostgreSQL 18's <c>WITHOUT OVERLAPS</c> and <c>PERIOD</c>) reaches the model with its range column last.</summary>
[TestClass]
public sealed class TemporalKeySemanticsTests
{
    private const string Room = "CREATE TABLE public.room (id int, during tsrange, CONSTRAINT room_pk PRIMARY KEY (id, during WITHOUT OVERLAPS));";

    [TestMethod]
    public void ATemporalPrimaryKeyIsWithoutOverlapsWithTheRangeLast()
    {
        // Act
        var key = Build(Room).Constraints.Values.Single(static c => c.Name == "room_pk");

        // Assert
        Assert.IsTrue(key.WithoutOverlaps);
        CollectionAssert.AreEqual(new[] { "id", "during" }, key.PrimaryKeyColumns.ToArray());
        Assert.IsFalse(key.ForeignKeyUsesPeriod);
    }

    [TestMethod]
    [DataRow("CREATE TABLE public.t (a int, r tsrange, CONSTRAINT k UNIQUE (a, r WITHOUT OVERLAPS));", new[] { "a", "r" })]
    [DataRow("CREATE TABLE public.t (a int, b int, r tsrange, CONSTRAINT k UNIQUE (b, a, r WITHOUT OVERLAPS));", new[] { "b", "a", "r" })]
    public void ATemporalUniqueKeepsItsColumnsInSourceOrder(string sql, string[] expected)
    {
        // Act
        var key = Build(sql).Constraints.Values.Single(static c => c.Name == "k");

        // Assert
        Assert.IsTrue(key.WithoutOverlaps);
        CollectionAssert.AreEqual(expected, key.UniqueColumns.ToArray());
    }

    [TestMethod]
    public void ATemporalForeignKeyUsesPeriodWithTheRangeLastOnBothSides()
    {
        // Arrange
        var sql = Room + " CREATE TABLE public.booking (room_id int, during tsrange, CONSTRAINT booking_fk FOREIGN KEY (room_id, PERIOD during) REFERENCES public.room (id, PERIOD during));";

        // Act
        var key = Build(sql).Constraints.Values.Single(static c => c.Name == "booking_fk");

        // Assert
        Assert.IsTrue(key.ForeignKeyUsesPeriod);
        Assert.IsFalse(key.WithoutOverlaps);
        CollectionAssert.AreEqual(new[] { "room_id", "during" }, key.ForeignKeyColumns.ToArray());
        CollectionAssert.AreEqual(new[] { "id", "during" }, key.ForeignKeyReferencedColumns.ToArray());
    }

    [TestMethod]
    public void AnOrdinaryKeyIsNotTemporal()
    {
        // Act
        var constraints = Build("CREATE TABLE public.p (id int PRIMARY KEY, a int UNIQUE); CREATE TABLE public.c (pid int REFERENCES public.p (id));").Constraints.Values;

        // Assert
        Assert.IsFalse(constraints.Any(static c => c.WithoutOverlaps || c.ForeignKeyUsesPeriod));
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
