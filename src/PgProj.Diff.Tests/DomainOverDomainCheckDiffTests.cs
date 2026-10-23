using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-184: PostgreSQL stores a domain's CHECK with <c>VALUE</c> cast to the root base type, also for a domain over another domain
/// (<c>VALUE::int4</c>). The project's CHECK has no cast, so the two read as different after every publish. <c>VALUE</c> is now typed as the root
/// base type on both sides, and a real difference is still a change.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class DomainOverDomainCheckDiffTests
{
    private const string Parents = "CREATE DOMAIN public.posint AS int CHECK (VALUE > 0); ";

    [TestMethod]
    public void ADomainOverADomainDiffedWithItselfIsEmpty()
    {
        var model = BuildModel(Parents + "CREATE DOMAIN public.smallpos AS public.posint CHECK (VALUE < 100);");

        Assert.IsEmpty(new DiffEngine().Execute(model, model).ModifiedTypes);
    }

    [TestMethod]
    [DataRow("CHECK (VALUE < 100)", "CHECK ((VALUE)::integer < 100)")]
    [DataRow("CHECK (VALUE < 100)", "CHECK (((VALUE)::int4 < 100))")]
    public void PostgreSqlsCastOfValueToTheRootBaseTypeIsNotAChange(string written, string stored)
    {
        var project = BuildModel(Parents + $"CREATE DOMAIN public.smallpos AS public.posint {written};");
        var live = BuildModel(Parents + $"CREATE DOMAIN public.smallpos AS public.posint {stored};");

        var result = new DiffEngine().Execute(live, project);

        Assert.IsFalse(result.ModifiedTypes.Any(static change => change.NodeId.EndsWith("smallpos", StringComparison.OrdinalIgnoreCase)), Describe(result));
    }

    [TestMethod]
    public void TheCastIsFoundThroughAChainOfDomainsAndAcrossSchemas()
    {
        const string Chain = "CREATE SCHEMA s; CREATE DOMAIN s.a AS int; CREATE DOMAIN public.b AS s.a; CREATE DOMAIN public.c AS public.b {0};";
        var project = BuildModel(string.Format(Chain, "CHECK (VALUE < 100)"));
        var live = BuildModel(string.Format(Chain, "CHECK ((VALUE)::integer < 100)"));

        var result = new DiffEngine().Execute(live, project);

        Assert.IsFalse(result.ModifiedTypes.Any(static change => change.NodeId.EndsWith(".c", StringComparison.OrdinalIgnoreCase)), Describe(result));
    }

    [TestMethod]
    public void ARealDifferenceInTheCheckIsStillAChange()
    {
        var project = BuildModel(Parents + "CREATE DOMAIN public.smallpos AS public.posint CHECK (VALUE < 100);");
        var live = BuildModel(Parents + "CREATE DOMAIN public.smallpos AS public.posint CHECK ((VALUE)::integer < 101);");

        var result = new DiffEngine().Execute(live, project);

        Assert.IsTrue(result.ModifiedTypes.Any(static change => change.NodeId.EndsWith("smallpos", StringComparison.OrdinalIgnoreCase)));
    }

    private static string Describe(DiffResult result)
        => string.Join(Environment.NewLine, result.ModifiedTypes.Select(static change => change.NodeId + ": " + string.Join("; ", change.Details.Select(static detail => detail.PropertyName))));

    private static DatabaseModel BuildModel(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
