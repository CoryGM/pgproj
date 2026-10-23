using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-42: unnamed constraints on one table used to share the synthesized name <c>{table}_constraint</c>,
/// so all but one silently vanished from the model.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class UnnamedConstraintSemanticsTests
{
    [TestMethod]
    public void EveryUnnamedConstraintOnATableSurvivesWithItsPostgreSqlName()
    {
        var model = BuildModel("create table public.t (a int check (a > 0), b int check (b > 0), c int unique);");

        Assert.IsFalse(model.HasErrors, string.Join(Environment.NewLine, model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        CollectionAssert.AreEquivalent(
            new[] { "public.t.t_a_check", "public.t.t_b_check", "public.t.t_c_key" },
            model.Constraints.Keys.ToArray());
        CollectionAssert.AreEquivalent(
            new[] { "public.t.t_a_check", "public.t.t_b_check", "public.t.t_c_key" },
            model.Tables["public.t"].ConstraintKeys.ToArray());
    }

    [TestMethod]
    public void UnnamedConstraintsKeepTheirOwnDefinitions()
    {
        var model = BuildModel("create table public.t (a int check (a > 0), b int check (b > 100));");

        var checkA = model.Constraints["public.t.t_a_check"];
        var checkB = model.Constraints["public.t.t_b_check"];

        StringAssert.Contains(checkA.RawCheckExpression, "a");
        StringAssert.Contains(checkB.RawCheckExpression, "100");
        Assert.AreEqual("t", checkA.TableName);
    }

    [TestMethod]
    public void UnnamedConstraintsOnDifferentTablesDoNotCollide()
    {
        var model = BuildModel("""
            create table public.a (v int check (v > 0));
            create table public.b (v int check (v > 0));
            """);

        CollectionAssert.AreEquivalent(
            new[] { "public.a.a_v_check", "public.b.b_v_check" },
            model.Constraints.Keys.ToArray());
    }

    [TestMethod]
    public void UnnamedPrimaryKeyAndUniqueOnTheSameTableBothSurvive()
    {
        var model = BuildModel("create table public.t (id bigint primary key, code text unique);");

        CollectionAssert.AreEquivalent(
            new[] { "public.t.t_pkey", "public.t.t_code_key" },
            model.Constraints.Keys.ToArray());
    }

    private static DatabaseModel BuildModel(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
