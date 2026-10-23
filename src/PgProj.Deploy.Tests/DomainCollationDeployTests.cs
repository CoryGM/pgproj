using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-196: a domain's <c>COLLATE</c> is written after its base type, and the domain's name of the collation is kept as written. PostgreSQL has no
/// <c>ALTER DOMAIN ... COLLATE</c>, so a changed collation drops and creates the domain; while a column uses the domain that is refused up front, naming it, as
/// G-190 does for an enum. Other domain changes keep their in-place form.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class DomainCollationDeployTests
{
    private const string Use = " CREATE TABLE public.t (id int, v public.d);";

    [TestMethod]
    public void TheCollationIsWrittenAfterTheBaseType()
    {
        var sql = Statements(Generate(Build(string.Empty), Build("""CREATE DOMAIN public.d AS text COLLATE "C" DEFAULT 'x' NOT NULL CHECK (length(VALUE) < 10);""")));

        StringAssert.StartsWith(sql.Single(), "CREATE DOMAIN \"public\".\"d\" AS text COLLATE \"C\" DEFAULT 'x' CONSTRAINT");
    }

    [TestMethod]
    public void ASchemaQualifiedCollationIsQuotedPartByPart()
    {
        var sql = Statements(Generate(Build(string.Empty), Build("""CREATE DOMAIN public.d AS varchar(20) COLLATE pg_catalog."POSIX";""")));

        Assert.AreEqual("CREATE DOMAIN \"public\".\"d\" AS varchar(20) COLLATE \"pg_catalog\".\"POSIX\";", sql.Single());
    }

    [TestMethod]
    [DataRow("CREATE DOMAIN public.d AS text;", """CREATE DOMAIN public.d AS text COLLATE "C";""")]
    [DataRow("""CREATE DOMAIN public.d AS text COLLATE "C";""", """CREATE DOMAIN public.d AS text COLLATE "POSIX";""")]
    public void AChangedCollationOfADomainNothingUsesDropsAndCreatesIt(string before, string after)
    {
        var batch = Generate(Build(before), Build(after));

        Assert.IsEmpty(batch.GetUnsupportedOperations());
        Assert.IsTrue(batch.Operations.Any(static operation => operation is DropOperation));
        Assert.IsTrue(batch.Operations.Any(static operation => operation is CreateOperation));
    }

    [TestMethod]
    public void AChangedCollationOfADomainInUseIsBlockedWithAMessageNamingWhatUsesIt()
    {
        var batch = Generate(Build("CREATE DOMAIN public.d AS text;" + Use), Build("""CREATE DOMAIN public.d AS text COLLATE "C";""" + Use));

        var blocked = batch.GetUnsupportedOperations().Single().GenerateSql();
        StringAssert.Contains(blocked, "The domain public.d changes its collation");
        StringAssert.Contains(blocked, "public.t");
        Assert.IsFalse(batch.Operations.Any(static operation => operation is DropOperation or CreateOperation));
    }

    [TestMethod]
    public void AChangedCheckOfADomainWithACollationIsStillInPlace()
    {
        var batch = Generate(
            Build("""CREATE DOMAIN public.d AS text COLLATE "C" CONSTRAINT short CHECK (length(VALUE) < 10);""" + Use),
            Build("""CREATE DOMAIN public.d AS text COLLATE "C" CONSTRAINT short CHECK (length(VALUE) < 20);""" + Use));

        Assert.IsEmpty(batch.GetUnsupportedOperations());
        Assert.IsFalse(batch.Operations.Any(static operation => operation is DropOperation), string.Join("\n", Statements(batch)));
        StringAssert.Contains(string.Join("\n", Statements(batch)), "ALTER DOMAIN");
    }

    [TestMethod]
    public void AProjectCollationIsCreatedBeforeTheDomain()
    {
        var sql = Statements(Generate(Build(string.Empty), Build("CREATE DOMAIN public.d AS text COLLATE public.ci; CREATE COLLATION public.ci (provider = icu, locale = 'und-u-ks-level2', deterministic = false);")));

        Assert.HasCount(2, sql, string.Join("\n", sql));
        StringAssert.StartsWith(sql[0], "CREATE COLLATION");
        StringAssert.StartsWith(sql[1], "CREATE DOMAIN");
    }

    private static List<string> Statements(OperationBatch batch)
        => batch.Operations.Where(static operation => operation is not SchemaCreateOperation).Select(static operation => operation.GenerateSql().Trim()).ToList();

    private static OperationBatch Generate(DatabaseModel oldModel, DatabaseModel newModel)
        => new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute(string.IsNullOrEmpty(sql) ? [] : [new PgQueryParser().Parse(sql)]);
}
