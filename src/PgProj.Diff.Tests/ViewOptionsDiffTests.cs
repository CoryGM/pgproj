using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-180: a view's options (<c>security_barrier</c>, <c>security_invoker</c>, the check option) are compared. A difference in them is a modified
/// view even when PostgreSQL's renderings of the two bodies are the same, which was the silent under-migration; spelling the same options
/// another way is not a change.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ViewOptionsDiffTests
{
    private const string Tables = "CREATE TABLE public.c (id int, name text);";

    [TestMethod]
    public void AModelDiffedAgainstItselfHasNoChange()
    {
        const string Sql = Tables + "CREATE VIEW public.v WITH (security_barrier = true, security_invoker = on) AS SELECT id FROM public.c WHERE id > 0 WITH LOCAL CHECK OPTION;";

        var result = new DiffEngine().Execute(BuildModel(Sql), BuildModel(Sql));

        Assert.IsEmpty(result.ModifiedViews);
    }

    [TestMethod]
    public void TheSameOptionsSpelledAndOrderedDifferentlyAreNotAChange()
    {
        var live = BuildModel(Tables + "CREATE VIEW public.v WITH (security_barrier = true, security_invoker = on) AS SELECT id FROM public.c;");
        var project = BuildModel(Tables + "CREATE VIEW public.v WITH (SECURITY_INVOKER, security_barrier = 't', security_invoker = 1) AS SELECT id FROM public.c;");

        Assert.IsEmpty(new DiffEngine().Execute(live, project).ModifiedViews);
    }

    [TestMethod]
    public void AnOptionStatedAsFalseIsTheSameAsNoOption()
    {
        var live = BuildModel(Tables + "CREATE VIEW public.v WITH (security_barrier = false) AS SELECT id FROM public.c;");
        var project = BuildModel(Tables + "CREATE VIEW public.v AS SELECT id FROM public.c;");

        Assert.IsEmpty(new DiffEngine().Execute(live, project).ModifiedViews);
    }

    [TestMethod]
    [DataRow("", "WITH (security_barrier = true)")]
    [DataRow("WITH (security_barrier = true)", "")]
    [DataRow("WITH (security_barrier = true)", "WITH (security_invoker = true)")]
    [DataRow("WITH (security_barrier = true)", "WITH (security_barrier = true, security_invoker = true)")]
    public void ADifferenceInTheOptionsIsAChange_EvenWhenPostgreSqlRendersTheBodiesTheSame(string liveWith, string projectWith)
    {
        var live = WithSameRendering(BuildModel($"{Tables}CREATE VIEW public.v {liveWith} AS SELECT id FROM public.c;"));
        var project = WithSameRendering(BuildModel($"{Tables}CREATE VIEW public.v {projectWith} AS SELECT id FROM public.c;"));

        var result = new DiffEngine().Execute(live, project);

        Assert.HasCount(1, result.ModifiedViews);
        Assert.AreEqual(nameof(ViewDefinition.Options), result.ModifiedViews[0].Details.Single().PropertyName);
    }

    [TestMethod]
    public void ACheckOptionAddedChangedOrRemovedIsAChange()
    {
        const string Local = Tables + "CREATE VIEW public.v AS SELECT id FROM public.c WITH LOCAL CHECK OPTION;";
        const string Cascaded = Tables + "CREATE VIEW public.v AS SELECT id FROM public.c WITH CASCADED CHECK OPTION;";
        const string None = Tables + "CREATE VIEW public.v AS SELECT id FROM public.c;";

        Assert.HasCount(1, new DiffEngine().Execute(WithSameRendering(BuildModel(None)), WithSameRendering(BuildModel(Local))).ModifiedViews);
        Assert.HasCount(1, new DiffEngine().Execute(WithSameRendering(BuildModel(Local)), WithSameRendering(BuildModel(Cascaded))).ModifiedViews);
        Assert.HasCount(1, new DiffEngine().Execute(WithSameRendering(BuildModel(Cascaded)), WithSameRendering(BuildModel(None))).ModifiedViews);
    }

    private static DatabaseModel WithSameRendering(DatabaseModel model)
    {
        model.Views = model.Views.ToDictionary(
            static entry => entry.Key,
            static entry => entry.Value with { ServerRenderedBody = "SELECT id FROM c" },
            StringComparer.OrdinalIgnoreCase);
        return model;
    }

    private static DatabaseModel BuildModel(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
