using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core.Tests;

/// <summary>
/// G-264 (PostgreSQL 18): a live partition whose NOT NULL constraint is not named as its parent's is written, by the snapshot and by <c>init</c>, as it was made (with its columns, then attached),
/// because <c>PARTITION OF</c> would give it the parent's name. A partition that has the parent's names is still <c>PARTITION OF</c>.
/// </summary>
[TestClass]
public sealed class LivePartitionNotNullNameTests
{
    private static readonly LiveTableSnapshot Parent = new("s", "parent", [
        new LiveColumnSnapshot("partid", "integer", false, null, false, null, null) { NotNullName = "parent_partid_not_null" },
        new LiveColumnSnapshot("b", "integer", true, null, false, null, null),
    ], PartitionKeyDef: "RANGE (partid)");

    private static LiveTableSnapshot Child(string name, string constraintName, bool differs) => new("s", name, [
        new LiveColumnSnapshot("partid", "integer", false, null, false, null, null, IsLocal: false) { NotNullIsInheritedOnly = true, PartitionNotNullName = constraintName, PartitionNotNullDiffers = differs },
        new LiveColumnSnapshot("b", "integer", true, null, false, null, null, IsLocal: false),
    ], PartitionOfSchemaName: "s", PartitionOfTableName: "parent", PartitionBoundRaw: "FOR VALUES FROM (1) TO (10)");

    [TestMethod]
    public void APartitionWithItsOwnNotNullNameIsWrittenWithItsColumnsAndAttached()
    {
        var sql = LiveDatabaseModelLoader.GenerateSnapshotSql([new LiveSchemaSnapshot("s")], [Parent, Child("child", "child_partid_not_null", differs: true)], [], []);

        StringAssert.Contains(sql, "CREATE TABLE s.child (");
        StringAssert.Contains(sql, "partid integer CONSTRAINT child_partid_not_null NOT NULL");
        StringAssert.Contains(sql, "ALTER TABLE s.parent ATTACH PARTITION s.child FOR VALUES FROM (1) TO (10);");
        Assert.IsFalse(sql.Contains("PARTITION OF", StringComparison.Ordinal), sql);
    }

    [TestMethod]
    public void APartitionWithTheParentsNotNullNameIsStillPartitionOf()
    {
        var sql = LiveDatabaseModelLoader.GenerateSnapshotSql([new LiveSchemaSnapshot("s")], [Parent, Child("child", "parent_partid_not_null", differs: false)], [], []);

        StringAssert.Contains(sql, "CREATE TABLE s.child PARTITION OF s.parent FOR VALUES FROM (1) TO (10);");
        Assert.IsFalse(sql.Contains("ATTACH PARTITION", StringComparison.Ordinal), sql);
    }

    [TestMethod]
    public void TheScaffoldStatesTheNameEvenWhenItIsTheOneTheTableWouldGetByItself()
    {
        var file = LiveDatabaseModelLoader.BuildScaffoldFiles([new LiveSchemaSnapshot("s")], [Parent, Child("child", "child_partid_not_null", differs: true)], [], [], [], [], [], [], [], [], [])
            .Single(static f => f.RelativePath.Contains("Table", StringComparison.Ordinal) && f.RelativePath.EndsWith("child.sql", StringComparison.Ordinal));

        StringAssert.Contains(file.Content, "partid integer CONSTRAINT child_partid_not_null NOT NULL");
        StringAssert.Contains(file.Content, "ATTACH PARTITION s.child");
        Assert.IsEmpty(new LiveDatabaseModelLoader(new PgQueryParser(), new SemanticBuilder()).FindUnparseableFiles([file]));
    }

    [TestMethod]
    public void TheSnapshotIsReadBackWithThePartitionsOwnName()
    {
        var sql = LiveDatabaseModelLoader.GenerateSnapshotSql([new LiveSchemaSnapshot("s")], [Parent, Child("child", "child_partid_not_null", differs: true)], [], []);

        var model = new SemanticBuilder().Build([new PgQueryParser().Parse(sql)]);

        var child = model.Tables["s.child"];
        Assert.AreEqual("s", child.PartitionOfSchemaName);
        Assert.AreEqual("parent", child.PartitionOfTableName);
        Assert.IsTrue(child.PartitionKeepsOwnColumns);
        Assert.AreEqual("child_partid_not_null", child.Columns.Single(static column => column.Name == "partid").NotNullName);
    }
}
