using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-288: a domain check PostgreSQL stores with a cast the written text lacks (<c>length(VALUE)</c> on a <c>varchar</c> domain is <c>length((VALUE)::text)</c>) never
/// converged. When the server rendered every check of both sides, the renderings are compared; otherwise the text, as before.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class DomainCheckRenderingDiffTests
{
    private const string Written = "CREATE DOMAIN public.v50 AS varchar(50) CHECK (length(VALUE) < 10);";
    private const string Stored = "CREATE DOMAIN public.v50 AS varchar(50) CONSTRAINT v50_check CHECK ((length((VALUE)::text) < 10));";

    [TestMethod]
    public void WithoutRenderingsTheTextsDifferAsBefore()
    {
        Assert.AreEqual(1, Changes(Build(Stored), Build(Written)).Count);
    }

    [TestMethod]
    public void EqualRenderingsAreNoChange()
    {
        var rendering = "(length((VALUE)::text) < 10)";

        Assert.AreEqual(0, Changes(Rendered(Build(Stored), rendering), Rendered(Build(Written), rendering)).Count);
    }

    [TestMethod]
    public void DifferentRenderingsAreAChangeShownAsRendered()
    {
        var modified = Changes(Rendered(Build(Stored), "(length((VALUE)::text) < 10)"), Rendered(Build(Written), "(length((VALUE)::text) < 20)")).Cast<ModifiedChange>().Single();

        var detail = modified.Details.Single(static detail => detail.PropertyName == nameof(TypeDefinition.DomainCheckConstraints));
        StringAssert.Contains(detail.NewValue, "< 20");
    }

    [TestMethod]
    public void ARenderingOnOneSideOnlyFallsBackToTheText()
    {
        Assert.AreEqual(1, Changes(Rendered(Build(Stored), "(length((VALUE)::text) < 10)"), Build(Written)).Count);
        Assert.AreEqual(0, Changes(Rendered(Build(Written), "(length((VALUE)::text) < 10)"), Build(Written)).Count, "the same text is still no change");
    }

    private static DatabaseModel Rendered(DatabaseModel model, string rendering)
    {
        model.Types = model.Types.ToDictionary(
            static pair => pair.Key,
            pair => pair.Value with { DomainCheckConstraints = pair.Value.DomainCheckConstraints.Select(check => check with { ServerRenderedCheck = rendering }).ToArray() },
            SqlIdentifiers.NameComparer);
        return model;
    }

    private static IReadOnlyList<ChangeRecord> Changes(DatabaseModel live, DatabaseModel project)
    {
        var changes = new DiffEngine().Diff(live, project);
        return changes.Added.Cast<ChangeRecord>().Concat(changes.Modified).Concat(changes.Removed).Where(static change => change.NodeId.EndsWith("v50", StringComparison.Ordinal)).ToArray();
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
