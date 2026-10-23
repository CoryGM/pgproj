using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-237: a child's own default for a column it only inherits, and a partition's index or key that was made on the partition first and adopted by the parent's.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class InheritedDefaultAndPartitionIndexNameTests
{
    private const string Inherit = """
        CREATE TABLE public.c0 (f1 integer, f3 integer DEFAULT 1);
        CREATE TABLE public.c1 () INHERITS (public.c0);
        """;

    private const string Partitioned = """
        CREATE TABLE public.p (a integer, b integer) PARTITION BY RANGE (a);
        CREATE TABLE public.p0 PARTITION OF public.p FOR VALUES FROM (0) TO (10);
        """;

    [TestMethod]
    public void AChildsDefaultOnAnInheritedColumnIsAStatementAfterTheTable()
    {
        var script = Script(DatabaseModel.Empty, Build(Inherit + "ALTER TABLE ONLY public.c1 ALTER COLUMN f3 SET DEFAULT 42;"));

        StringAssert.Contains(script, "ALTER TABLE ONLY \"public\".\"c1\" ALTER COLUMN \"f3\" SET DEFAULT 42;");
        Assert.IsTrue(script.IndexOf("CREATE TABLE \"public\".\"c1\"", StringComparison.Ordinal) < script.IndexOf("SET DEFAULT 42", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void AChildsDropDefaultOnAnInheritedColumnIsDeployed()
    {
        var script = Script(DatabaseModel.Empty, Build(Inherit + "ALTER TABLE ONLY public.c1 ALTER COLUMN f3 DROP DEFAULT;"));

        StringAssert.Contains(script, "ALTER TABLE ONLY \"public\".\"c1\" ALTER COLUMN \"f3\" DROP DEFAULT;");
    }

    [TestMethod]
    public void AChildWithoutItsOwnDefaultStatesNothing()
    {
        Assert.IsFalse(Script(DatabaseModel.Empty, Build(Inherit)).Contains("ONLY", StringComparison.Ordinal));
    }

    [TestMethod]
    public void TheDefaultAppliesWhateverFileTheStatementIsIn()
    {
        var model = new SemanticPipeline().Execute(
        [
            new PgQueryParser().Parse("ALTER TABLE ONLY public.c1 ALTER COLUMN f3 SET DEFAULT 42;"),
            new PgQueryParser().Parse(Inherit),
        ]);

        StringAssert.Contains(Script(DatabaseModel.Empty, model), "SET DEFAULT 42;");
        Assert.IsFalse(model.Diagnostics.Any(static d => d.Message.Contains("ignored", StringComparison.Ordinal)), string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
    }

    [TestMethod]
    public void APartitionsDefaultOnAnInheritedColumnIsDeployed()
    {
        var script = Script(DatabaseModel.Empty, Build(Partitioned + "ALTER TABLE ONLY public.p0 ALTER COLUMN b SET DEFAULT 7;"));

        StringAssert.Contains(script, "ALTER TABLE ONLY \"public\".\"p0\" ALTER COLUMN \"b\" SET DEFAULT 7;");
    }

    [TestMethod]
    public void ADefaultForAColumnTheTableDoesNotInheritIsAWarning()
    {
        var model = Build(Inherit + "ALTER TABLE ONLY public.c1 ALTER COLUMN nope SET DEFAULT 1;");

        Assert.IsTrue(model.Diagnostics.Any(static d => d.Message.Contains("does not inherit that column", StringComparison.Ordinal)), string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
    }

    [TestMethod]
    public void AChangedChildDefaultIsMadeInPlace()
    {
        var script = Script(
            Build(Inherit + "ALTER TABLE ONLY public.c1 ALTER COLUMN f3 SET DEFAULT 7;"),
            Build(Inherit + "ALTER TABLE ONLY public.c1 ALTER COLUMN f3 SET DEFAULT 42;"));

        StringAssert.Contains(script, "ALTER TABLE \"public\".\"c1\" ALTER COLUMN \"f3\" SET DEFAULT 42;");
        Assert.IsFalse(script.Contains("DROP", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void RemovingTheChildDefaultGivesTheParentsBack()
    {
        var script = Script(
            Build(Inherit + "ALTER TABLE ONLY public.c1 ALTER COLUMN f3 SET DEFAULT 7;"),
            Build(Inherit));

        StringAssert.Contains(script, "ALTER TABLE \"public\".\"c1\" ALTER COLUMN \"f3\" SET DEFAULT 1;");
    }

    [TestMethod]
    public void TheSameChildDefaultOnBothSidesIsNoChange()
    {
        var model = Build(Inherit + "ALTER TABLE ONLY public.c1 ALTER COLUMN f3 SET DEFAULT 42;");

        Assert.AreEqual(string.Empty, Script(model, model));
    }

    [TestMethod]
    public void APartitionIndexWithACustomNameIsKeptAndCreatedBeforeTheParentsIndex()
    {
        var script = Script(DatabaseModel.Empty, Build(Partitioned + """
            CREATE INDEX p0_custom ON public.p0 (b);
            CREATE INDEX p_b ON ONLY public.p (b);
            ALTER INDEX public.p_b ATTACH PARTITION public.p0_custom;
            """));

        StringAssert.Contains(script, "CREATE INDEX \"p0_custom\" ON \"public\".\"p0\"");
        Assert.IsTrue(script.IndexOf("\"p0_custom\"", StringComparison.Ordinal) < script.IndexOf("CREATE INDEX \"p_b\"", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void APartitionIndexWithTheGeneratedNameIsStillTheParents()
    {
        var script = Script(DatabaseModel.Empty, Build(Partitioned + """
            CREATE INDEX p0_b_idx ON public.p0 (b);
            CREATE INDEX p_b ON ONLY public.p (b);
            ALTER INDEX public.p_b ATTACH PARTITION public.p0_b_idx;
            """));

        Assert.IsFalse(script.Contains("p0_b_idx", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void APartitionPrimaryKeyWithACustomNameIsKept()
    {
        var script = Script(DatabaseModel.Empty, Build("""
            CREATE TABLE public.p (a integer NOT NULL, b integer) PARTITION BY RANGE (a);
            CREATE TABLE public.p0 PARTITION OF public.p FOR VALUES FROM (0) TO (10);
            ALTER TABLE ONLY public.p0 ADD CONSTRAINT p0_custom_pk PRIMARY KEY (a);
            ALTER TABLE ONLY public.p ADD CONSTRAINT p_pkey PRIMARY KEY (a);
            ALTER INDEX public.p_pkey ATTACH PARTITION public.p0_custom_pk;
            """));

        StringAssert.Contains(script, "\"p0_custom_pk\"");
        Assert.IsTrue(script.IndexOf("\"p0_custom_pk\"", StringComparison.Ordinal) < script.IndexOf("\"p_pkey\"", StringComparison.Ordinal), script);
    }

    private static string Script(DatabaseModel oldModel, DatabaseModel newModel)
    {
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));
        return string.Join("\n", batch.Operations.Where(static operation => operation is not SchemaCreateOperation).Select(static operation => operation.GenerateSql().Replace("\r\n", "\n", StringComparison.Ordinal))).Trim();
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
