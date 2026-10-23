using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-291: a default set on a view's column (<c>ALTER VIEW v ALTER COLUMN c SET DEFAULT x</c>, or <c>pg_dump</c>'s <c>ALTER TABLE ONLY v ...</c>) is part of the view. It was
/// warned about and ignored, so an insert through an updatable view got NULL.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ViewColumnDefaultTests
{
    private const string Base = """
        CREATE TABLE public.t (a int, b int, c text);
        CREATE VIEW public.v AS SELECT a, b, c FROM public.t;
        """;

    [TestMethod]
    public void BothFormsAreReadWithoutAWarning()
    {
        var model = Build(Base + """
            ALTER TABLE ONLY public.v ALTER COLUMN b SET DEFAULT 100;
            ALTER VIEW public.v ALTER COLUMN c SET DEFAULT 'x'::text;
            """);

        Assert.AreEqual(0, model.Diagnostics.Count, string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
        var view = model.Views.Values.Single(static v => v.Name == "v");
        Assert.AreEqual("100", view.ColumnDefaults["b"]);
        Assert.AreEqual("'x'::text", view.ColumnDefaults["c"]);
    }

    [TestMethod]
    public void ALaterDropDefaultRemovesTheDefaultAndNullIsNone()
    {
        var model = Build(Base + """
            ALTER VIEW public.v ALTER COLUMN b SET DEFAULT 100;
            ALTER VIEW public.v ALTER COLUMN b DROP DEFAULT;
            ALTER VIEW public.v ALTER COLUMN c SET DEFAULT NULL;
            """);

        var view = model.Views.Values.Single(static v => v.Name == "v");
        Assert.AreEqual(1, view.ColumnDefaults.Count, "only the NULL default is left, as written");
        Assert.AreEqual(0, view.NormalizedColumnDefaults.Count, "PostgreSQL stores no default for NULL");
    }

    [TestMethod]
    public void ADefaultOnAViewTheProjectDoesNotDeclareIsStillAWarning()
    {
        var model = Build(Base + "ALTER VIEW public.missing ALTER COLUMN b SET DEFAULT 1;");

        Assert.IsTrue(model.Diagnostics.Any(static d => d.Message.Contains("does not declare", StringComparison.Ordinal)), string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
    }

    [TestMethod]
    public void ANewViewIsCreatedAndThenGivenItsDefaults()
    {
        var script = Script(DatabaseModel.Empty, Build(Base + """
            ALTER VIEW public.v ALTER COLUMN b SET DEFAULT 100;
            ALTER VIEW public.v ALTER COLUMN c SET DEFAULT 'x'::text;
            """));

        StringAssert.Contains(script, "ALTER VIEW \"public\".\"v\" ALTER COLUMN \"b\" SET DEFAULT 100;");
        StringAssert.Contains(script, "ALTER VIEW \"public\".\"v\" ALTER COLUMN \"c\" SET DEFAULT 'x'::text;");
        Assert.IsLessThan(script.IndexOf("SET DEFAULT 100", StringComparison.Ordinal), script.IndexOf("CREATE OR REPLACE VIEW", StringComparison.Ordinal), "the view comes first");
    }

    [TestMethod]
    public void AChangedAndARemovedDefaultAreChangedInPlaceWithoutRecreatingTheView()
    {
        var live = Build(Base + """
            ALTER VIEW public.v ALTER COLUMN a SET DEFAULT 1;
            ALTER VIEW public.v ALTER COLUMN b SET DEFAULT 100;
            """);
        var project = Build(Base + "ALTER VIEW public.v ALTER COLUMN b SET DEFAULT 200;");

        var script = Script(live, project);

        StringAssert.Contains(script, "CREATE OR REPLACE VIEW");
        Assert.DoesNotContain("DROP VIEW", script);
        StringAssert.Contains(script, "ALTER VIEW \"public\".\"v\" ALTER COLUMN \"a\" DROP DEFAULT;");
        StringAssert.Contains(script, "ALTER VIEW \"public\".\"v\" ALTER COLUMN \"b\" SET DEFAULT 200;");
    }

    [TestMethod]
    public void AnUnchangedDefaultIsNotWrittenAgain()
    {
        var live = Build(Base + """
            ALTER VIEW public.v ALTER COLUMN a SET DEFAULT 1;
            ALTER VIEW public.v ALTER COLUMN b SET DEFAULT 100;
            """);
        var project = Build(Base + """
            ALTER VIEW public.v ALTER COLUMN a SET DEFAULT 1;
            ALTER VIEW public.v ALTER COLUMN b SET DEFAULT 200;
            """);

        var script = Script(live, project);

        Assert.DoesNotContain("COLUMN \"a\"", script);
        StringAssert.Contains(script, "COLUMN \"b\" SET DEFAULT 200;");
    }

    [TestMethod]
    public void TheSameModelAndACosmeticRewriteAreNoChange()
    {
        var one = Build(Base + """
            ALTER VIEW public.v ALTER COLUMN b SET DEFAULT 100;
            ALTER VIEW public.v ALTER COLUMN c SET DEFAULT 'x'::text;
            """);
        var other = Build(Base + """
            ALTER TABLE ONLY public.v ALTER COLUMN c SET DEFAULT 'x';
            ALTER TABLE ONLY public.v ALTER COLUMN b SET DEFAULT (100);
            """);

        Assert.AreEqual(0, ChangeCount(new DiffEngine().Diff(one, one)));
        Assert.AreEqual(0, ChangeCount(new DiffEngine().Diff(one, other)));
    }

    [TestMethod]
    public void AMeaningfulChangeIsAChange()
    {
        var one = Build(Base + "ALTER VIEW public.v ALTER COLUMN b SET DEFAULT 100;");
        var other = Build(Base + "ALTER VIEW public.v ALTER COLUMN b SET DEFAULT 101;");
        var none = Build(Base);

        Assert.AreEqual(1, ChangeCount(new DiffEngine().Diff(one, other)));
        Assert.AreEqual(1, ChangeCount(new DiffEngine().Diff(one, none)));
        Assert.AreEqual(1, ChangeCount(new DiffEngine().Diff(none, one)));
    }

    private static int ChangeCount(ChangeSet diff) => diff.Added.Count + diff.Removed.Count + diff.Modified.Count;

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);

    private static string Script(DatabaseModel live, DatabaseModel project)
    {
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(live, project), new DependencyAnalyzer().Analyze(live, project));
        return string.Join("\n", batch.Operations.Where(static operation => operation is not SchemaCreateOperation).Select(static operation => operation.GenerateSql().Replace("\r\n", "\n", StringComparison.Ordinal))).Trim();
    }
}
