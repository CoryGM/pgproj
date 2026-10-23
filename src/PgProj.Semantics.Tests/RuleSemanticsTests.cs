using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-128: a rule is a model object of its own, keyed by its relation and name; the relation it is on (a table or a view) and whatever its condition and actions use
/// are dependencies, and its text has a normal form for when there is no server rendering to compare.
/// </summary>
[TestClass]
public sealed class RuleSemanticsTests
{
    private const string Relations = "CREATE TABLE public.t (id int, n int); CREATE TABLE public.log (id int); CREATE VIEW public.v AS SELECT id, n FROM public.t; ";

    [TestMethod]
    public void ARuleIsInTheModelKeyedByItsRelationAndName()
    {
        var model = Build(Relations + "CREATE RULE r AS ON DELETE TO public.t DO INSTEAD NOTHING;");

        var rule = model.Rules["rule:public.t.r"];
        Assert.AreEqual("delete", rule.Event);
        Assert.IsTrue(rule.IsInstead);
        Assert.AreEqual("public", rule.SchemaName);
        Assert.AreEqual("t", rule.TableName);
        Assert.AreEqual(0, model.Diagnostics.Count, string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
    }

    [TestMethod]
    public void TheTablesTheActionsUseAreDependenciesAsIsTheTableTheRuleIsOn()
    {
        var model = Build(Relations + "CREATE RULE r AS ON UPDATE TO public.t WHERE new.n <> old.n DO ALSO INSERT INTO public.log VALUES (old.id);");

        var rule = model.Rules.Values.Single();
        CollectionAssert.AreEquivalent(new[] { "public.t", "public.log" }, rule.ReferencedTables.ToArray());
        Assert.IsEmpty(rule.ReferencedViews);
    }

    [TestMethod]
    public void ARuleOnAViewDependsOnTheView()
    {
        var model = Build(Relations + "CREATE RULE r AS ON INSERT TO public.v DO INSTEAD INSERT INTO public.t VALUES (new.id, new.n);");

        var rule = model.Rules.Values.Single();
        CollectionAssert.AreEqual(new[] { "public.v" }, rule.ReferencedViews.ToArray());
        CollectionAssert.AreEqual(new[] { "public.t" }, rule.ReferencedTables.ToArray());
    }

    [TestMethod]
    public void NewAndOldAreNotRelations()
    {
        var model = Build(Relations + "CREATE RULE r AS ON UPDATE TO public.t DO ALSO INSERT INTO public.log VALUES (old.id);");

        Assert.IsFalse(model.Diagnostics.Any(static d => d.Message.Contains("'old'", StringComparison.OrdinalIgnoreCase) || d.Message.Contains("'new'", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void TwoRulesOfTheSameNameOnTheSameRelationAreAnError()
    {
        var model = Build(Relations + "CREATE RULE r AS ON DELETE TO public.t DO INSTEAD NOTHING; CREATE RULE r AS ON INSERT TO public.t DO INSTEAD NOTHING;");

        Assert.AreEqual(1, model.Diagnostics.Count(static d => d.Severity == SemanticDiagnosticSeverity.Error && d.Message.Contains("Duplicate Rule", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void TheSameNameOnTwoRelationsIsTwoRules()
    {
        var model = Build(Relations + "CREATE RULE r AS ON DELETE TO public.t DO INSTEAD NOTHING; CREATE RULE r AS ON DELETE TO public.log DO INSTEAD NOTHING;");

        Assert.AreEqual(2, model.Rules.Count);
    }

    [TestMethod]
    public void TheNormalFormIgnoresOrReplaceTheSemicolonAndWhiteSpace()
    {
        var first = Build(Relations + "CREATE OR REPLACE RULE r AS ON DELETE TO public.t\n    DO INSTEAD NOTHING;").Rules.Values.Single();
        var second = Build(Relations + "CREATE RULE r AS ON DELETE TO public.t DO INSTEAD NOTHING").Rules.Values.Single();

        Assert.AreEqual(second.NormalizedDefinition, first.NormalizedDefinition);
    }

    [TestMethod]
    public void ARuleIsAModelNodeSoTheGraphSeesIt()
    {
        var model = Build(Relations + "CREATE RULE r AS ON DELETE TO public.t DO INSTEAD NOTHING;");

        Assert.IsTrue(model.GetAllNodes().Any(static node => node is RuleDefinition));
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
