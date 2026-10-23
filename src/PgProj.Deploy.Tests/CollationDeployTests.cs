using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-148: a collation is created before the columns and indexes that use it and dropped after them; a column's qualified collation is two identifiers; a change is a drop and a
/// create, except that one something uses is not carried out (PostgreSQL refuses it), so the script names it instead.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class CollationDeployTests
{
    private const string Collation = "CREATE COLLATION app.ci (provider = icu, locale = 'und-u-ks-level2', deterministic = false); ";

    [TestMethod]
    public void ACollationIsCreatedBeforeTheColumnThatUsesIt()
    {
        var statements = Statements(Build("CREATE SCHEMA app;"), Build("CREATE SCHEMA app; " + Collation + "CREATE TABLE app.t (n text COLLATE app.ci);"));

        var collationAt = statements.FindIndex(static s => s.StartsWith("CREATE COLLATION", StringComparison.Ordinal));
        var tableAt = statements.FindIndex(static s => s.StartsWith("CREATE TABLE", StringComparison.Ordinal));
        Assert.IsTrue(collationAt >= 0 && collationAt < tableAt, string.Join("\n", statements));
        StringAssert.Contains(statements[tableAt], "COLLATE \"app\".\"ci\"");
    }

    [TestMethod]
    public void ACollationIsWrittenWithItsProviderLocaleAndDeterminism()
    {
        var sql = Statements(Build("CREATE SCHEMA app;"), Build("CREATE SCHEMA app; " + Collation)).Single(static s => s.StartsWith("CREATE COLLATION", StringComparison.Ordinal));

        Assert.AreEqual("CREATE COLLATION \"app\".\"ci\" (\n    PROVIDER = icu,\n    LOCALE = 'und-u-ks-level2',\n    DETERMINISTIC = false\n);", sql);
    }

    [TestMethod]
    public void ALibcCollationIsWrittenWithBothLocales()
    {
        var sql = Statements(Build("CREATE SCHEMA app;"), Build("CREATE SCHEMA app; CREATE COLLATION app.c (locale = 'C');")).Single(static s => s.StartsWith("CREATE COLLATION", StringComparison.Ordinal));

        Assert.AreEqual("CREATE COLLATION \"app\".\"c\" (\n    PROVIDER = libc,\n    LC_COLLATE = 'C',\n    LC_CTYPE = 'C'\n);", sql);
    }

    [TestMethod]
    public void AnIndexKeyKeepsItsQualifiedCollationAsTwoIdentifiers()
    {
        var statements = Statements(
            Build("CREATE SCHEMA app; CREATE TABLE app.t (n text);"),
            Build("CREATE SCHEMA app; " + Collation + "CREATE TABLE app.t (n text); CREATE INDEX t_n ON app.t (n COLLATE app.ci);"));

        StringAssert.Contains(statements.Single(static s => s.StartsWith("CREATE INDEX", StringComparison.Ordinal)), "COLLATE \"app\".\"ci\"");
    }

    [TestMethod]
    public void ARemovedCollationThatNothingUsesIsDropped()
    {
        var statements = Statements(Build("CREATE SCHEMA app; " + Collation), Build("CREATE SCHEMA app;"));

        CollectionAssert.Contains(statements, "DROP COLLATION \"app\".\"ci\";");
    }

    [TestMethod]
    public void AChangedCollationNothingUsesIsDroppedAndCreatedAgain()
    {
        var statements = Statements(
            Build("CREATE SCHEMA app; " + Collation),
            Build("CREATE SCHEMA app; CREATE COLLATION app.ci (provider = icu, locale = 'de');"));

        Assert.AreEqual(2, statements.Count, string.Join("\n", statements));
        Assert.AreEqual("DROP COLLATION \"app\".\"ci\";", statements[0]);
        StringAssert.StartsWith(statements[1], "CREATE COLLATION");
    }

    [TestMethod]
    public void AChangedCollationInUseIsBlockedWithAMessageNamingWhatUsesIt()
    {
        var oldModel = Build("CREATE SCHEMA app; " + Collation + "CREATE TABLE app.t (n text COLLATE app.ci);");
        var newModel = Build("CREATE SCHEMA app; CREATE COLLATION app.ci (provider = icu, locale = 'de'); CREATE TABLE app.t (n text COLLATE app.ci);");

        var batch = Generate(oldModel, newModel);

        var blocked = batch.GetUnsupportedOperations().Single();
        StringAssert.Contains(blocked.GenerateSql(), "app.t");
        Assert.IsFalse(Sql(batch).Any(static s => s.StartsWith("DROP COLLATION", StringComparison.Ordinal)));
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
