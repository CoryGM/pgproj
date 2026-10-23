using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-245: a policy's expressions are compared through PostgreSQL's own rendering when both sides have one, because the server prints a subquery differently from how it was written
/// (qualified columns, spaces, added parentheses). Without a rendering on both sides the canonical text form is compared as before.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class PolicyServerRenderingDiffTests
{
    private const string Sql = "CREATE TABLE public.t (c integer); CREATE TABLE public.m (c1 integer); CREATE POLICY p ON public.t FOR UPDATE USING (true) WITH CHECK ((SELECT t.c <= m.c1 FROM public.m));";

    [TestMethod]
    public void TwoRenderingsThatAreEqualAreNoChangeWhateverTheTextSays()
    {
        var project = With(Build(Sql), usingRendering: "true", withCheckRendering: "( SELECT (t.c <= m.c1) FROM m)");
        var live = With(Build(Sql.Replace("(SELECT t.c <= m.c1 FROM public.m)", "(SELECT t.c < m.c1 FROM public.m)", StringComparison.Ordinal)), usingRendering: "true", withCheckRendering: "( SELECT (t.c <= m.c1) FROM m)");

        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(live, project)), "Equal renderings are the same policy; the text is not compared.");
    }

    [TestMethod]
    public void TwoRenderingsThatDifferAreAChangeEvenWhenTheTextIsTheSame()
    {
        var project = With(Build(Sql), usingRendering: "true", withCheckRendering: "( SELECT (t.c <= m.c1) FROM m)");
        var live = With(Build(Sql), usingRendering: "true", withCheckRendering: "( SELECT (t.c < m.c1) FROM m)");

        var detail = new DiffEngine().Diff(live, project).Modified.Single(static change => change.NodeId.EndsWith(".p", StringComparison.Ordinal)).Details.Single();
        Assert.AreEqual("WithCheckExpression", detail.PropertyName);
    }

    [TestMethod]
    public void WithoutARenderingOnBothSidesTheCanonicalTextIsComparedAsBefore()
    {
        var same = Build(Sql);
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(same, same)));

        var changed = Build(Sql.Replace("t.c <= m.c1", "t.c < m.c1", StringComparison.Ordinal));
        Assert.IsFalse(IsEmpty(new DiffEngine().Diff(With(same, "true", "x"), changed)), "A rendering on one side only falls back to the text, which differs.");
    }

    [TestMethod]
    public void EachExpressionIsComparedByItsOwnRendering()
    {
        var project = With(Build(Sql), usingRendering: "true", withCheckRendering: "a");
        var live = With(Build(Sql), usingRendering: "false", withCheckRendering: "a");

        var detail = new DiffEngine().Diff(live, project).Modified.Single(static change => change.NodeId.EndsWith(".p", StringComparison.Ordinal)).Details.Single();
        Assert.AreEqual("UsingExpression", detail.PropertyName);
    }

    private static bool IsEmpty(ChangeSet diff) => diff.Added.Count == 0 && diff.Removed.Count == 0 && diff.Modified.Count == 0;

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);

    private static DatabaseModel With(DatabaseModel model, string? usingRendering, string? withCheckRendering)
    {
        model.Policies = model.Policies.ToDictionary(
            static pair => pair.Key,
            pair => pair.Value with { ServerRenderedUsing = usingRendering, ServerRenderedWithCheck = withCheckRendering },
            PgProj.Parser.SqlIdentifiers.NameComparer);
        return model;
    }
}
