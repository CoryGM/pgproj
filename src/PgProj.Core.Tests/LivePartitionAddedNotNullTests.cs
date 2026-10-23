using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core.Tests;

/// <summary>
/// G-266: a live partition column that is NOT NULL while its parent's is not was read as nullable before PostgreSQL 18 (no constraint name to make the partition be written with
/// its columns, G-264), so a partition published from a <c>PARTITION OF parent (a NOT NULL)</c> list never converged. The snapshot and <c>init</c> now write such a column in the
/// partition's element list, which the parser reads (G-266).
/// </summary>
[TestClass]
public sealed class LivePartitionAddedNotNullTests
{
    private static readonly LiveTableSnapshot Parent = new("s", "parent", [
        new LiveColumnSnapshot("partid", "integer", false, null, false, null, null),
        new LiveColumnSnapshot("a", "integer", true, null, false, null, null),
    ], PartitionKeyDef: "RANGE (partid)");

    private static readonly LiveTableSnapshot Child = new("s", "child", [
        new LiveColumnSnapshot("partid", "integer", false, null, false, null, null, IsLocal: false),
        new LiveColumnSnapshot("a", "integer", false, null, false, null, null, IsLocal: false) { PartitionAddsNotNull = true },
    ], PartitionOfSchemaName: "s", PartitionOfTableName: "parent", PartitionBoundRaw: "FOR VALUES FROM (1) TO (10)");

    [TestMethod]
    public void TheColumnIsInThePartitionsElementListAndReadBackNotNull()
    {
        var sql = LiveDatabaseModelLoader.GenerateSnapshotSql([new LiveSchemaSnapshot("s")], [Parent, Child], [], []);

        StringAssert.Contains(sql, "CREATE TABLE s.child PARTITION OF s.parent (a NOT NULL) FOR VALUES FROM (1) TO (10);");
        var model = new SemanticBuilder().Build([new PgQueryParser().Parse(sql)]);
        Assert.IsFalse(model.Tables["s.child"].Columns.Single(static column => column.Name == "a").AllowsNull);
        Assert.IsTrue(model.Tables["s.parent"].Columns.Single(static column => column.Name == "a").AllowsNull);
    }

    [TestMethod]
    public void APartitionThatAddsNothingIsWrittenAsBefore()
    {
        var plain = Child with { Columns = [.. Child.Columns.Select(static column => column with { IsNullable = true, PartitionAddsNotNull = false })] };

        var sql = LiveDatabaseModelLoader.GenerateSnapshotSql([new LiveSchemaSnapshot("s")], [Parent, plain], [], []);

        StringAssert.Contains(sql, "CREATE TABLE s.child PARTITION OF s.parent FOR VALUES FROM (1) TO (10);");
    }
}
