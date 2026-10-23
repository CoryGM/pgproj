using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-96: when PostgreSQL has rendered both sides of a view (<see cref="ViewDefinition.ServerRenderedBody"/>), the diff
/// compares those renderings and nothing derived from the body text, so PostgreSQL's own rewrites of a view no longer
/// read as a change, and a real change still does. Without a rendering on both sides, the body is compared as before.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ServerRenderedViewDiffTests
{
    private const string Tables = "CREATE TABLE public.c (id int, name text);";

    [TestMethod]
    public void SameRenderingIsNoChangeEvenWhenTheWrittenBodiesDiffer()
    {
        var live = WithRendering(BuildModel(Tables + "CREATE VIEW public.v AS SELECT id, name FROM c WHERE id > 5;"), " SELECT id,\n    name\n   FROM c\n  WHERE id > 5;");
        var project = WithRendering(BuildModel(Tables + "CREATE VIEW public.v AS SELECT c.id, c.name FROM public.c c WHERE c.id > 5;"), " SELECT id,\n    name\n   FROM c\n  WHERE id > 5;");

        var result = new DiffEngine().Execute(live, project);

        Assert.IsEmpty(result.ModifiedViews);
    }

    [TestMethod]
    public void DifferentRenderingIsAChangeEvenWhenTheWrittenBodiesAreIdentical()
    {
        const string Sql = Tables + "CREATE VIEW public.v AS SELECT id FROM public.c WHERE id > 5;";
        var live = WithRendering(BuildModel(Sql), "SELECT id FROM c WHERE id > 5");
        var project = WithRendering(BuildModel(Sql), "SELECT id FROM c WHERE id > 6");

        var result = new DiffEngine().Execute(live, project);

        Assert.HasCount(1, result.ModifiedViews);
        var detail = result.ModifiedViews[0].Details.Single();
        Assert.AreEqual(nameof(ViewDefinition.ServerRenderedBody), detail.PropertyName);
        Assert.AreEqual("SELECT id FROM c WHERE id > 5", detail.OldValue);
        Assert.AreEqual("SELECT id FROM c WHERE id > 6", detail.NewValue);
    }

    [TestMethod]
    public void AMaterializedFlagOrCommentChangeIsStillReportedAlongsideTheRendering()
    {
        var live = WithRendering(BuildModel(Tables + "CREATE VIEW public.v AS SELECT id FROM public.c;"), "SELECT id FROM c");
        var project = WithRendering(BuildModel(Tables + "CREATE VIEW public.v AS SELECT id FROM public.c; COMMENT ON VIEW public.v IS 'new';"), "SELECT id FROM c");

        var result = new DiffEngine().Execute(live, project);

        Assert.HasCount(1, result.ModifiedViews);
        Assert.AreEqual(nameof(ViewDefinition.Comment), result.ModifiedViews[0].Details.Single().PropertyName);
    }

    [TestMethod]
    public void WithoutARenderingOnBothSidesTheBodyIsComparedAsBefore()
    {
        var live = BuildModel(Tables + "CREATE VIEW public.v AS SELECT id FROM public.c WHERE id > 5;");
        var sameProject = WithRendering(BuildModel(Tables + "CREATE VIEW public.v AS SELECT id FROM public.c WHERE id > 5;"), "SELECT id FROM c WHERE id > 5");
        var changedProject = WithRendering(BuildModel(Tables + "CREATE VIEW public.v AS SELECT id FROM public.c WHERE id > 6;"), "SELECT id FROM c WHERE id > 6");

        Assert.IsEmpty(new DiffEngine().Execute(live, sameProject).ModifiedViews);
        Assert.HasCount(1, new DiffEngine().Execute(live, changedProject).ModifiedViews);
    }

    [TestMethod]
    public void DerivedReferenceListsAloneAreNotAChange()
    {
        // Same body; only the lists derived from it differ (as binding read the two spellings differently before G-96).
        var live = BuildModel(Tables + "CREATE VIEW public.v AS SELECT id FROM public.c;");
        var project = BuildModel(Tables + "CREATE VIEW public.v AS SELECT id FROM public.c;");
        project.Views = new Dictionary<string, ViewDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["public.v"] = project.Views["public.v"] with { ReferencedColumns = ["public.c.id", "public.c.name"], ReferencedTypes = ["text"] },
        };

        Assert.IsEmpty(new DiffEngine().Execute(live, project).ModifiedViews);
    }

    private static DatabaseModel WithRendering(DatabaseModel model, string rendering)
    {
        model.Views = model.Views.ToDictionary(
            static entry => entry.Key,
            entry => entry.Value with { ServerRenderedBody = rendering },
            StringComparer.OrdinalIgnoreCase);
        return model;
    }

    private static DatabaseModel BuildModel(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
