using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core.Tests;

/// <summary>
/// G-121 part 2d: a live PostgreSQL 18 column whose NOT NULL is <c>NOT VALID</c> (a <c>pg_constraint</c> row of type <c>n</c> that is not validated)
/// is written to the snapshot and to <c>init</c>'s scaffold as a plain column plus <c>ALTER TABLE ... ADD CONSTRAINT name NOT NULL col NOT VALID</c>, and
/// read back as not valid with its name.
/// </summary>
[TestClass]
public sealed class LiveNotValidNotNullTests
{
    private static readonly LiveTableSnapshot Table = new("s", "t", [
        new LiveColumnSnapshot("a", "integer", false, null, false, null, null, NotValidNotNullName: "t_a_not_null"),
        new LiveColumnSnapshot("b", "integer", false, null, false, null, null),
        new LiveColumnSnapshot("c", "integer", true, null, false, null, null),
    ]);

    [TestMethod]
    public void ANotValidNotNullIsReadAsNotValidWithItsLiveName()
    {
        // Act
        var model = Load([Table]);

        // Assert
        var columns = model.Tables["s.t"].Columns.ToDictionary(static c => c.Name);
        Assert.IsFalse(columns["a"].AllowsNull);
        Assert.IsTrue(columns["a"].NotNullIsNotValid);
        Assert.AreEqual("t_a_not_null", columns["a"].NotNullConstraintName);
        Assert.IsFalse(columns["b"].AllowsNull);
        Assert.IsFalse(columns["b"].NotNullIsNotValid);
        Assert.IsNull(columns["b"].NotNullConstraintName);
        Assert.IsTrue(columns["c"].AllowsNull);
        Assert.IsFalse(columns["c"].NotNullIsNotValid);
    }

    [TestMethod]
    public void TheSnapshotWritesANotValidColumnPlainAndAddsTheConstraintAfterTheTable()
    {
        // Act
        var sql = LiveDatabaseModelLoader.GenerateSnapshotSql([new LiveSchemaSnapshot("s")], [Table], [], []);

        // Assert
        StringAssert.Contains(sql, "a integer,");
        Assert.IsFalse(sql.Contains("a integer NOT NULL", StringComparison.Ordinal), sql);
        StringAssert.Contains(sql, "b integer NOT NULL");
        StringAssert.Contains(sql, "ALTER TABLE s.t ADD CONSTRAINT t_a_not_null NOT NULL a NOT VALID;");
    }

    [TestMethod]
    public void TheScaffoldPutsTheNotValidConstraintInItsTablesFile()
    {
        // Act
        var file = LiveDatabaseModelLoader.BuildScaffoldFiles([new LiveSchemaSnapshot("s")], [Table], [], [], [], [], [], [], [], [], [])
            .Single(static f => f.RelativePath.Contains("Table", StringComparison.Ordinal) && f.RelativePath.EndsWith("t.sql", StringComparison.Ordinal));

        // Assert
        StringAssert.Contains(file.Content, "ALTER TABLE s.t ADD CONSTRAINT t_a_not_null NOT NULL a NOT VALID;");
        Assert.IsEmpty(new LiveDatabaseModelLoader(new PgQueryParser(), new SemanticBuilder()).FindUnparseableFiles([file]));
    }

    [TestMethod]
    public void ATablesNotNullThatIsValidIsWrittenOnItsColumnAsBefore()
    {
        // Act
        var sql = LiveDatabaseModelLoader.GenerateSnapshotSql(
            [new LiveSchemaSnapshot("s")],
            [new LiveTableSnapshot("s", "u", [new LiveColumnSnapshot("a", "integer", false, null, false, null, null)])],
            [],
            []);

        // Assert
        StringAssert.Contains(sql, "a integer NOT NULL");
        Assert.IsFalse(sql.Contains("NOT VALID", StringComparison.Ordinal), sql);
    }

    private static DatabaseModel Load(IReadOnlyList<LiveTableSnapshot> tables)
    {
        var snapshotSql = LiveDatabaseModelLoader.GenerateSnapshotSql([new LiveSchemaSnapshot("s")], tables, [], []);
        return new LiveDatabaseModelLoader(new PgQueryParser(), new SemanticBuilder()).BuildModelFromSnapshotSql(snapshotSql);
    }
}
