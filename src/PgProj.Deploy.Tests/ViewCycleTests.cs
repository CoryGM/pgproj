using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-254: two views that refer to each other, which <c>CREATE OR REPLACE VIEW</c> makes possible, cannot be created in any order. The first one is created from its first definition (its
/// earlier body, or the placeholder <c>pg_dump</c> writes), the others follow, and the first is replaced by its real body.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ViewCycleTests
{
    private const string Tables = "CREATE TABLE public.t1 (a bigint); CREATE TABLE public.t1a (b bigint);";

    [TestMethod]
    public void AViewReplacedByABodyThatReadsAViewThatReadsItIsCreatedFromItsFirstDefinition()
    {
        var script = Script(Build(Tables + """
            CREATE VIEW public.v2(a, b) AS SELECT * FROM public.t1, public.t1a;
            CREATE VIEW public.v3 AS SELECT * FROM public.v2;
            CREATE OR REPLACE VIEW public.v2 AS SELECT * FROM public.v3;
            """));

        var first = script.IndexOf("SELECT * FROM public.t1, public.t1a", StringComparison.Ordinal);
        var partner = script.IndexOf("CREATE OR REPLACE VIEW \"public\".\"v3\"", StringComparison.Ordinal);
        var real = script.IndexOf("SELECT * FROM public.v3", StringComparison.Ordinal);
        Assert.IsTrue(first >= 0 && first < partner && partner < real, script);
    }

    [TestMethod]
    public void APgDumpPlaceholderIsTheFirstDefinition()
    {
        var script = Script(Build("""
            CREATE VIEW public.v2 AS SELECT NULL::bigint AS a, NULL::bigint AS b;
            CREATE VIEW public.v3 AS SELECT a, b FROM public.v2;
            CREATE OR REPLACE VIEW public.v2 AS SELECT a, b FROM public.v3;
            """));

        var placeholder = script.IndexOf("NULL::bigint AS a", StringComparison.Ordinal);
        var partner = script.IndexOf("CREATE OR REPLACE VIEW \"public\".\"v3\"", StringComparison.Ordinal);
        var real = script.IndexOf("SELECT a, b FROM public.v3", StringComparison.Ordinal);
        Assert.IsTrue(placeholder >= 0 && placeholder < partner && partner < real, script);
    }

    [TestMethod]
    public void TheRealBodyComesAfterEveryViewOfALongerCycle()
    {
        var script = Script(Build(Tables + """
            CREATE VIEW public.v1 AS SELECT a FROM public.t1;
            CREATE VIEW public.v2 AS SELECT a FROM public.v1;
            CREATE VIEW public.v3 AS SELECT a FROM public.v2;
            CREATE OR REPLACE VIEW public.v1 AS SELECT a FROM public.v3;
            """));

        var v3 = script.IndexOf("CREATE OR REPLACE VIEW \"public\".\"v3\"", StringComparison.Ordinal);
        var real = script.LastIndexOf("CREATE OR REPLACE VIEW \"public\".\"v1\"", StringComparison.Ordinal);
        Assert.IsTrue(v3 >= 0 && real > v3, script);
        Assert.AreEqual(2, CountOf(script, "CREATE OR REPLACE VIEW \"public\".\"v1\""), script);
    }

    [TestMethod]
    public void TheCommentIsOnTheRealBodyAndAViewOutsideTheCycleIsCreatedOnce()
    {
        var script = Script(Build(Tables + """
            CREATE VIEW public.v2(a, b) AS SELECT * FROM public.t1, public.t1a;
            CREATE VIEW public.v3 AS SELECT * FROM public.v2;
            CREATE OR REPLACE VIEW public.v2 AS SELECT * FROM public.v3;
            CREATE VIEW public.v4 AS SELECT * FROM public.v3;
            COMMENT ON VIEW public.v2 IS 'in a cycle';
            """));

        Assert.AreEqual(1, CountOf(script, "COMMENT ON VIEW"), script);
        Assert.IsTrue(script.IndexOf("SELECT * FROM public.v3\n", StringComparison.Ordinal) < script.IndexOf("COMMENT ON VIEW", StringComparison.Ordinal), script);
        Assert.AreEqual(1, CountOf(script, "CREATE OR REPLACE VIEW \"public\".\"v4\""), script);
    }

    [TestMethod]
    public void AViewReplacedWithoutACycleIsCreatedOnce()
    {
        var script = Script(Build(Tables + """
            CREATE VIEW public.v AS SELECT a FROM public.t1;
            CREATE OR REPLACE VIEW public.v AS SELECT a FROM public.t1 WHERE a > 0;
            """));

        Assert.AreEqual(1, CountOf(script, "CREATE OR REPLACE VIEW \"public\".\"v\""), script);
        StringAssert.Contains(script, "WHERE a > 0");
    }

    [TestMethod]
    public void ACycleNoViewWasReplacedInStillFails()
    {
        // Two views that read each other cannot be written without a replace, but a model built in code can have them.
        var model = Build(Tables + "CREATE VIEW public.v2 AS SELECT a FROM public.t1;");
        var cyclic = model with
        {
            Views = new Dictionary<string, ViewDefinition>(model.Views, PgProj.Parser.SqlIdentifiers.NameComparer)
            {
                ["public.v2"] = model.Views["public.v2"] with { ReferencedViews = ["public.v3"] },
                ["public.v3"] = model.Views["public.v2"] with { Key = "public.v3", Name = "v3", ReferencedViews = ["public.v2"] },
            },
        };

        Assert.ThrowsExactly<GraphCycleException>(() => new DependencyAnalyzer().Analyze(DatabaseModel.Empty, cyclic));
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static string Script(DatabaseModel newModel)
    {
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(DatabaseModel.Empty, newModel), new DependencyAnalyzer().Analyze(DatabaseModel.Empty, newModel));
        return string.Join("\n", batch.Operations.Where(static operation => operation is not SchemaCreateOperation).Select(static operation => operation.GenerateSql().Replace("\r\n", "\n", StringComparison.Ordinal))).Trim();
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
