using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-216: <c>COMMENT ON COLUMN</c> on a view's column, a materialized view's column, or a composite type's field is modeled, compared, and deployed. It was
/// accepted without a warning and never deployed.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RelationColumnCommentTests
{
    private const string Table = "CREATE TABLE public.t (a int, b text);";

    [TestMethod]
    [DataRow("CREATE VIEW public.v AS SELECT a, b FROM public.t;", "v")]
    [DataRow("CREATE MATERIALIZED VIEW public.v AS SELECT a, b FROM public.t;", "v")]
    [DataRow("CREATE TYPE public.v AS (a int, b text);", "v")]
    public void TheCommentIsModeledAndIsNullRemovesIt(string declaration, string name)
    {
        var model = Build($"{Table} {declaration} COMMENT ON COLUMN public.{name}.a IS 'first'; COMMENT ON COLUMN public.{name}.b IS 'second'; COMMENT ON COLUMN public.{name}.b IS NULL;");

        var comments = model.Views.TryGetValue("public.v", out var view) ? view.ColumnComments : model.Types["public.v"].ColumnComments;
        Assert.AreEqual("first", comments["a"]);
        Assert.IsFalse(comments.ContainsKey("b"));
        Assert.IsNull(model.Tables["public.t"].Columns.Single(static column => column.Name == "a").Comment);
    }

    [TestMethod]
    public void ANewViewAndTypeGetTheirColumnComments()
    {
        var script = ScriptFor(DatabaseModel.Empty, Build($"""
            {Table}
            CREATE VIEW public.v AS SELECT a, b FROM public.t;
            COMMENT ON COLUMN public.v.a IS 'view a';
            CREATE TYPE public.ct AS (x int);
            COMMENT ON COLUMN public.ct.x IS 'field x';
            """));

        StringAssert.Contains(script, "COMMENT ON COLUMN \"public\".\"v\".\"a\" IS 'view a';", script);
        StringAssert.Contains(script, "COMMENT ON COLUMN \"public\".\"ct\".\"x\" IS 'field x';", script);
    }

    [TestMethod]
    public void AViewReplacedInPlaceChangesOnlyTheCommentsThatChanged()
    {
        var script = ScriptFor(
            Build($"{Table} CREATE VIEW public.v AS SELECT a, b FROM public.t; COMMENT ON COLUMN public.v.a IS 'same'; COMMENT ON COLUMN public.v.b IS 'old';"),
            Build($"{Table} CREATE VIEW public.v AS SELECT a, b FROM public.t; COMMENT ON COLUMN public.v.a IS 'same';"));

        StringAssert.Contains(script, "COMMENT ON COLUMN \"public\".\"v\".\"b\" IS NULL;", script);
        Assert.IsFalse(script.Contains("'same'", StringComparison.Ordinal), script);
        Assert.IsFalse(script.Contains("DROP VIEW", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void AMaterializedViewsColumnCommentIsChangedWithoutRebuildingIt()
    {
        var script = ScriptFor(
            Build($"{Table} CREATE MATERIALIZED VIEW public.v AS SELECT a FROM public.t; COMMENT ON COLUMN public.v.a IS 'old';"),
            Build($"{Table} CREATE MATERIALIZED VIEW public.v AS SELECT a FROM public.t; COMMENT ON COLUMN public.v.a IS 'new';"));

        StringAssert.Contains(script, "COMMENT ON COLUMN \"public\".\"v\".\"a\" IS 'new';", script);
        Assert.IsFalse(script.Contains("DROP MATERIALIZED VIEW", StringComparison.Ordinal), script);
        Assert.IsFalse(script.Contains("COMMENT ON MATERIALIZED VIEW", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void ACompositeTypesFieldCommentIsChangedWithoutRebuildingIt()
    {
        var script = ScriptFor(
            Build("CREATE TYPE public.ct AS (x int); COMMENT ON COLUMN public.ct.x IS 'old';"),
            Build("CREATE TYPE public.ct AS (x int, y text); COMMENT ON COLUMN public.ct.x IS 'new'; COMMENT ON COLUMN public.ct.y IS 'added';"));

        var add = script.IndexOf("ADD ATTRIBUTE \"y\"", StringComparison.Ordinal);
        Assert.IsTrue(add >= 0 && add < script.IndexOf("COMMENT ON COLUMN \"public\".\"ct\".\"y\" IS 'added';", StringComparison.Ordinal), script);
        StringAssert.Contains(script, "COMMENT ON COLUMN \"public\".\"ct\".\"x\" IS 'new';", script);
        Assert.IsFalse(script.Contains("DROP TYPE", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void AnUnchangedCommentIsNoChange()
    {
        const string Sql = Table + " CREATE VIEW public.v AS SELECT a FROM public.t; COMMENT ON COLUMN public.v.a IS 'x'; CREATE TYPE public.ct AS (x int); COMMENT ON COLUMN public.ct.x IS 'y';";

        var changes = new DiffEngine().Diff(Build(Sql), Build(Sql));

        Assert.IsEmpty(changes.Modified);
    }

    private static string ScriptFor(DatabaseModel oldModel, DatabaseModel newModel)
        => new ScriptGenerator().GenerateSqlScript(new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel)));

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
