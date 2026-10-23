using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-128: a rule is compared by the server's rendering of it when both sides have one, and by its text in a normal form otherwise; the same rule spelled another
/// way (white space, <c>OR REPLACE</c>, a semicolon) is not a change, and a different rule is.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RuleDiffTests
{
    private const string Relations = "CREATE TABLE public.t (id int, n int); ";

    private static string Rule(string body) => Relations + "CREATE RULE r AS " + body + ";";

    [TestMethod]
    public void ARuleDiffedWithItselfIsEmpty()
    {
        var model = Build(Rule("ON DELETE TO public.t DO INSTEAD NOTHING"));

        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(model, model)));
    }

    [TestMethod]
    public void TheSameRuleSpelledAnotherWayReportsNothingWhenThereIsNoRendering()
    {
        var first = Build(Relations + "CREATE OR REPLACE RULE r AS ON DELETE TO public.t\n    DO INSTEAD NOTHING;");
        var second = Build(Rule("ON DELETE TO public.t DO INSTEAD NOTHING"));

        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(first, second)));
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(second, first)));
    }

    [TestMethod]
    [DataRow("ON DELETE TO public.t DO INSTEAD NOTHING", "ON DELETE TO public.t DO ALSO NOTHING")]
    [DataRow("ON DELETE TO public.t DO INSTEAD NOTHING", "ON INSERT TO public.t DO INSTEAD NOTHING")]
    [DataRow("ON UPDATE TO public.t WHERE new.n > 0 DO INSTEAD NOTHING", "ON UPDATE TO public.t WHERE new.n < 0 DO INSTEAD NOTHING")]
    public void ADifferentRuleIsReportedWithoutARendering(string before, string after)
    {
        var diff = new DiffEngine().Diff(Build(Rule(before)), Build(Rule(after)));

        Assert.AreEqual(1, diff.Modified.Count(static change => change.NodeKind == ChangeNodeKind.Rule));
    }

    [TestMethod]
    public void WhenBothHaveTheServerRenderingTheyAreComparedByThat()
    {
        // The two texts differ, but PostgreSQL prints them the same: not a change. And the reverse.
        var first = WithRendering(Build(Rule("ON UPDATE TO public.t WHERE new.n > 0 DO INSTEAD NOTHING")), "CREATE RULE r AS ON UPDATE TO public.t WHERE (new.n > 0) DO INSTEAD NOTHING;");
        var second = WithRendering(Build(Rule("ON UPDATE TO public.t WHERE (NEW.n > 0) DO INSTEAD NOTHING")), "CREATE RULE r AS ON UPDATE TO public.t WHERE (new.n > 0) DO INSTEAD NOTHING;");
        var third = WithRendering(Build(Rule("ON UPDATE TO public.t WHERE new.n > 0 DO INSTEAD NOTHING")), "CREATE RULE r AS ON UPDATE TO public.t WHERE (new.n > 1) DO INSTEAD NOTHING;");

        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(first, second)));
        Assert.AreEqual(1, new DiffEngine().Diff(first, third).Modified.Count);
    }

    [TestMethod]
    public void AnAddedRuleAndARemovedRuleAreReported()
    {
        var withRule = Build(Rule("ON DELETE TO public.t DO INSTEAD NOTHING"));
        var without = Build(Relations);

        Assert.AreEqual(1, new DiffEngine().Diff(without, withRule).Added.Count(static change => change.NodeKind == ChangeNodeKind.Rule));
        Assert.AreEqual(1, new DiffEngine().Diff(withRule, without).Removed.Count(static change => change.NodeKind == ChangeNodeKind.Rule));
    }

    private static DatabaseModel WithRendering(DatabaseModel model, string rendering)
    {
        model.Rules = model.Rules.ToDictionary(static pair => pair.Key, pair => pair.Value with { ServerRenderedDefinition = rendering }, StringComparer.OrdinalIgnoreCase);
        return model;
    }

    private static bool IsEmpty(ChangeSet diff)
        => diff.Added.Count + diff.Removed.Count + diff.Modified.Count == 0;

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
