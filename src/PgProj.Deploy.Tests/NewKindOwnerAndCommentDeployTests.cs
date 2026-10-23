using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-154: owners and comments of collations, extended statistics, and text search dictionaries and configurations. A comment is written with the object when it is created, a
/// comment-only change is a <c>COMMENT ON</c> and nothing else (it does not drop and create a collation or statistics object, nor trip the check for one in use), and an owner is
/// set after the object exists.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class NewKindOwnerAndCommentDeployTests
{
    private const string Objects =
        "CREATE SCHEMA app; CREATE TABLE app.t (a int, b int); "
        + "CREATE COLLATION app.ci (provider = icu, locale = 'und'); "
        + "CREATE STATISTICS app.st ON a, b FROM app.t; "
        + "CREATE TEXT SEARCH DICTIONARY app.d (TEMPLATE = simple); "
        + "CREATE TEXT SEARCH CONFIGURATION app.c (PARSER = pg_catalog.\"default\"); ";

    [TestMethod]
    public void ACommentIsWrittenAfterEachObjectAndAnOwnerAfterThat()
    {
        var statements = Statements(
            Build("CREATE SCHEMA app;"),
            Build(Objects
                + "COMMENT ON COLLATION app.ci IS 'c1'; COMMENT ON STATISTICS app.st IS 's1'; COMMENT ON TEXT SEARCH DICTIONARY app.d IS 'd''1'; COMMENT ON TEXT SEARCH CONFIGURATION app.c IS 'f1'; "
                + "ALTER COLLATION app.ci OWNER TO bob; ALTER STATISTICS app.st OWNER TO bob; ALTER TEXT SEARCH DICTIONARY app.d OWNER TO bob; ALTER TEXT SEARCH CONFIGURATION app.c OWNER TO bob;"));
        var script = string.Join("\n", statements);

        StringAssert.Contains(script, "COMMENT ON COLLATION \"app\".\"ci\" IS 'c1';");
        StringAssert.Contains(script, "COMMENT ON STATISTICS \"app\".\"st\" IS 's1';");
        StringAssert.Contains(script, "COMMENT ON TEXT SEARCH DICTIONARY \"app\".\"d\" IS 'd''1';");
        StringAssert.Contains(script, "COMMENT ON TEXT SEARCH CONFIGURATION \"app\".\"c\" IS 'f1';");
        foreach (var owned in new[] { "COLLATION \"app\".\"ci\"", "STATISTICS \"app\".\"st\"", "TEXT SEARCH DICTIONARY \"app\".\"d\"", "TEXT SEARCH CONFIGURATION \"app\".\"c\"" })
        {
            Assert.IsTrue(script.IndexOf($"CREATE {owned.Split(' ')[0]}", StringComparison.Ordinal) < script.IndexOf($"ALTER {owned} OWNER TO \"bob\";", StringComparison.Ordinal), owned + "\n" + script);
        }
    }

    [TestMethod]
    public void ACommentOnlyChangeIsJustACommentOn()
    {
        var statements = Statements(
            Build(Objects + "COMMENT ON COLLATION app.ci IS 'old'; COMMENT ON STATISTICS app.st IS 'old';"),
            Build(Objects + "COMMENT ON COLLATION app.ci IS 'new'; COMMENT ON TEXT SEARCH DICTIONARY app.d IS 'added';"));

        CollectionAssert.AreEquivalent(
            new[]
            {
                "COMMENT ON COLLATION \"app\".\"ci\" IS 'new';",
                "COMMENT ON STATISTICS \"app\".\"st\" IS NULL;",
                "COMMENT ON TEXT SEARCH DICTIONARY \"app\".\"d\" IS 'added';",
            },
            statements);
    }

    [TestMethod]
    public void ACommentOnlyChangeOnACollationInUseIsNotBlocked()
    {
        var oldModel = Build(Objects + "CREATE TABLE app.u (n text COLLATE app.ci); COMMENT ON COLLATION app.ci IS 'old';");
        var newModel = Build(Objects + "CREATE TABLE app.u (n text COLLATE app.ci); COMMENT ON COLLATION app.ci IS 'new';");

        var batch = Generate(oldModel, newModel);

        Assert.IsEmpty(batch.GetUnsupportedOperations());
        CollectionAssert.AreEqual(new[] { "COMMENT ON COLLATION \"app\".\"ci\" IS 'new';" }, Sql(batch));
    }

    [TestMethod]
    public void ARealChangeToACommentedCollationIsStillADropAndACreateWithTheCommentKept()
    {
        var statements = Statements(
            Build(Objects + "COMMENT ON COLLATION app.ci IS 'c';"),
            Build("CREATE SCHEMA app; CREATE COLLATION app.ci (provider = icu, locale = 'de'); COMMENT ON COLLATION app.ci IS 'c'; CREATE STATISTICS app.st ON a, b FROM app.t; CREATE TABLE app.t (a int, b int); CREATE TEXT SEARCH DICTIONARY app.d (TEMPLATE = simple); CREATE TEXT SEARCH CONFIGURATION app.c (PARSER = pg_catalog.\"default\");"));

        Assert.AreEqual("DROP COLLATION \"app\".\"ci\";", statements[0]);
        StringAssert.Contains(statements[1], "COMMENT ON COLLATION \"app\".\"ci\" IS 'c';");
    }

    [TestMethod]
    public void AChangedOwnerIsAnAlterOwnerAndIsDestructive()
    {
        var batch = Generate(
            Build(Objects + "ALTER COLLATION app.ci OWNER TO bob;"),
            Build(Objects + "ALTER COLLATION app.ci OWNER TO amy;"));

        CollectionAssert.AreEqual(new[] { "ALTER COLLATION \"app\".\"ci\" OWNER TO \"amy\";" }, Sql(batch));
        Assert.AreEqual(1, batch.GetDestructiveOperations().Count);
    }

    private static OperationBatch Generate(DatabaseModel oldModel, DatabaseModel newModel)
        => new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));

    private static List<string> Sql(OperationBatch batch)
        => batch.Operations
            .Where(static operation => operation is not SchemaCreateOperation)
            .Select(static operation => operation.GenerateSql().Trim().Replace("\r\n", "\n", StringComparison.Ordinal))
            .ToList();

    private static List<string> Statements(DatabaseModel oldModel, DatabaseModel newModel) => Sql(Generate(oldModel, newModel));

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
