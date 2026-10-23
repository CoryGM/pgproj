using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>G-205: <c>SUPPORT</c> and <c>TRANSFORM</c> are compared both ways; the support function's <c>pg_catalog</c> schema and the transforms' order are not differences.</summary>
[TestClass]
[DoNotParallelize]
public sealed class RoutineSupportTransformDiffTests
{
    private const string F = "create function public.f(text, text) returns bool language internal immutable strict {0} as 'textlike';";
    private const string P = "create function public.g(int) returns int language plpgsql {0} as $$ begin return 1; end $$;";

    [TestMethod]
    [DataRow("", "support textlike_support")]
    [DataRow("support textlike_support", "support other_support")]
    public void AChangedSupportIsReportedBothWays(string before, string after)
    {
        Assert.AreEqual(1, ModifiedCount(string.Format(F, before), string.Format(F, after)));
        Assert.AreEqual(1, ModifiedCount(string.Format(F, after), string.Format(F, before)));
    }

    [TestMethod]
    public void ASupportFunctionsSchemaOnTheSearchPathIsNoChange()
    {
        Assert.AreEqual(0, ModifiedCount(string.Format(F, "support pg_catalog.textlike_support"), string.Format(F, "SUPPORT textlike_support")));
    }

    [TestMethod]
    public void AChangedTransformIsReportedBothWays()
    {
        Assert.AreEqual(1, ModifiedCount(string.Format(P, string.Empty), string.Format(P, "transform for type int")));
        Assert.AreEqual(1, ModifiedCount(string.Format(P, "transform for type int"), string.Format(P, string.Empty)));
    }

    [TestMethod]
    public void TheTransformsOrderIsNoChange()
    {
        Assert.AreEqual(0, ModifiedCount(string.Format(P, "transform for type int, for type text"), string.Format(P, "TRANSFORM FOR TYPE text, FOR TYPE integer")));
    }

    private static int ModifiedCount(string oldSql, string newSql)
        => new DiffEngine().Diff(Build(oldSql), Build(newSql)).Modified.Count;

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
