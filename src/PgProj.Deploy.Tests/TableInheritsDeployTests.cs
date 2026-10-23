using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-194: a child is created with only its own columns and <c>INHERITS</c>, after its parents. A parent removed is <c>NO INHERIT</c>, before the columns change;
/// a parent added is <c>INHERIT</c>, after the child is given the parent's columns. A change to a column the child inherits from a parent it keeps is made on the
/// parent alone, since PostgreSQL carries it to the child.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class TableInheritsDeployTests
{
    [TestMethod]
    public void AChildIsCreatedAfterItsParentsWithOnlyItsOwnColumns()
    {
        var sql = Statements(Generate(Build(string.Empty), Build("CREATE TABLE c (extra int, name text) INHERITS (p, q); CREATE TABLE p (id int, name text); CREATE TABLE q (o int);")));

        var child = sql.Single(static statement => statement.StartsWith("CREATE TABLE \"public\".\"c\"", StringComparison.Ordinal));
        // The merged column keeps the parent's position, as PostgreSQL gives it.
        StringAssert.StartsWith(child, "CREATE TABLE \"public\".\"c\" (\n    \"name\" text,\n    \"extra\" int\n)\nINHERITS (\"public\".\"p\", \"public\".\"q\");");
        Assert.IsTrue(sql.FindIndex(static statement => statement.StartsWith("CREATE TABLE \"public\".\"p\"", StringComparison.Ordinal)) < sql.IndexOf(child));
        Assert.IsTrue(sql.FindIndex(static statement => statement.StartsWith("CREATE TABLE \"public\".\"q\"", StringComparison.Ordinal)) < sql.IndexOf(child));
    }

    [TestMethod]
    public void AChildOfNothingButItsParentHasAnEmptyColumnList()
    {
        var sql = Statements(Generate(Build("CREATE TABLE p (id int);"), Build("CREATE TABLE p (id int); CREATE TABLE c () INHERITS (p);")));

        StringAssert.StartsWith(sql.Single(), "CREATE TABLE \"public\".\"c\" (\n\n)\nINHERITS (\"public\".\"p\");");
    }

    [TestMethod]
    public void AnAddedParentGivesTheChildItsColumnsThenInherits()
    {
        var sql = Statements(Generate(
            Build("CREATE TABLE p (id int NOT NULL, name text); CREATE TABLE c (x int);"),
            Build("CREATE TABLE p (id int NOT NULL, name text); CREATE TABLE c (x int) INHERITS (p);")));

        Assert.AreEqual(
            "ALTER TABLE \"public\".\"c\" ADD COLUMN \"id\" int NOT NULL;\nALTER TABLE \"public\".\"c\" ADD COLUMN \"name\" text;\nALTER TABLE \"public\".\"c\" INHERIT \"public\".\"p\";",
            sql.Single());
    }

    [TestMethod]
    public void ARemovedParentIsNoInheritBeforeItsColumnsAreDropped()
    {
        var sql = Statements(Generate(
            Build("CREATE TABLE p (id int, name text); CREATE TABLE c (x int) INHERITS (p);"),
            Build("CREATE TABLE p (id int, name text); CREATE TABLE c (x int);")));

        Assert.AreEqual(
            "ALTER TABLE \"public\".\"c\" NO INHERIT \"public\".\"p\";\nALTER TABLE \"public\".\"c\" DROP COLUMN \"id\";\nALTER TABLE \"public\".\"c\" DROP COLUMN \"name\";",
            sql.Single());
    }

    [TestMethod]
    public void ARemovedParentWhoseColumnsTheChildKeepsIsOnlyNoInherit()
    {
        var sql = Statements(Generate(
            Build("CREATE TABLE p (id int, name text); CREATE TABLE c (x int) INHERITS (p);"),
            Build("CREATE TABLE p (id int, name text); CREATE TABLE c (id int, name text, x int);")));

        Assert.AreEqual("ALTER TABLE \"public\".\"c\" NO INHERIT \"public\".\"p\";", sql.Single());
    }

    [TestMethod]
    [DataRow("CREATE TABLE p (id int, name text, added date);")]
    [DataRow("CREATE TABLE p (id int);")]
    [DataRow("CREATE TABLE p (id bigint, name text);")]
    public void AParentsColumnChangeIsMadeOnTheParentAlone(string newParent)
    {
        var sql = Statements(Generate(
            Build("CREATE TABLE p (id int, name text); CREATE TABLE c (x int) INHERITS (p);"),
            Build(newParent + " CREATE TABLE c (x int) INHERITS (p);")));

        Assert.IsFalse(sql.Any(static statement => statement.Contains("\"public\".\"c\"", StringComparison.Ordinal)), string.Join("\n", sql));
        Assert.IsTrue(sql.Any(static statement => statement.Contains("ALTER TABLE \"public\".\"p\"", StringComparison.Ordinal)), string.Join("\n", sql));
    }

    [TestMethod]
    public void ACheckThatIsNoInheritIsWrittenAsSuch()
    {
        var sql = string.Join("\n", Statements(Generate(Build(string.Empty), Build("CREATE TABLE p (id int, CONSTRAINT p_pos CHECK (id > 0) NO INHERIT);"))));

        StringAssert.Contains(sql, "CHECK (id > 0) NO INHERIT;");
    }

    private static List<string> Statements(OperationBatch batch)
        => batch.Operations
            .Where(static operation => operation is not SchemaCreateOperation)
            .Select(static operation => operation.GenerateSql().Trim())
            .Where(static sql => !sql.StartsWith("-- No column-level changes", StringComparison.Ordinal))
            .Select(static sql => sql.Replace("\n-- NOTE: Constraints and indexes deploy as separate ALTER TABLE/CREATE INDEX statements.", string.Empty, StringComparison.Ordinal))
            .ToList();

    private static OperationBatch Generate(DatabaseModel oldModel, DatabaseModel newModel)
        => new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute(string.IsNullOrEmpty(sql) ? [] : [new PgQueryParser().Parse(sql)]);
}
