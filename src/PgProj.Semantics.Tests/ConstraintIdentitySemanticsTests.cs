using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// Regression coverage for G-38: PostgreSQL scopes constraint names to their table, so the same
/// constraint name on different tables in one schema must produce distinct model identities instead
/// of silently collapsing into one.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ConstraintIdentitySemanticsTests
{
    [TestMethod]
    public void SameCheckConstraintNameOnDifferentTablesKeepsBothConstraints()
    {
        var model = BuildModel("""
            create table public.a (id integer, v integer, constraint v_positive check (v > 0));
            create table public.b (id integer, v integer, constraint v_positive check (v > 0));
            """);

        Assert.IsFalse(model.HasErrors, string.Join(Environment.NewLine, model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.HasCount(2, model.Constraints);
        Assert.IsTrue(model.Constraints.ContainsKey("public.a.v_positive"));
        Assert.IsTrue(model.Constraints.ContainsKey("public.b.v_positive"));
        CollectionAssert.AreEqual(new[] { "public.a.v_positive" }, model.Tables["public.a"].ConstraintKeys.ToArray());
        CollectionAssert.AreEqual(new[] { "public.b.v_positive" }, model.Tables["public.b"].ConstraintKeys.ToArray());
    }

    [TestMethod]
    public void SameForeignKeyNameOnDifferentTablesKeepsBothConstraints()
    {
        var model = BuildModel("""
            create table public.parent (id integer primary key);
            create table public.a (parent_id integer, constraint fk_parent foreign key (parent_id) references public.parent (id));
            create table public.b (parent_id integer, constraint fk_parent foreign key (parent_id) references public.parent (id));
            """);

        Assert.IsTrue(model.Constraints.ContainsKey("public.a.fk_parent"));
        Assert.IsTrue(model.Constraints.ContainsKey("public.b.fk_parent"));
        Assert.AreEqual("a", model.Constraints["public.a.fk_parent"].TableName);
        Assert.AreEqual("b", model.Constraints["public.b.fk_parent"].TableName);
    }

    [TestMethod]
    public void ConstraintAddedByAlterTableIsKeyedByItsTable()
    {
        var model = BuildModel("""
            create table public.a (v integer);
            create table public.b (v integer);
            alter table public.a add constraint v_positive check (v > 0);
            alter table public.b add constraint v_positive check (v > 1);
            """);

        Assert.HasCount(2, model.Constraints);
        Assert.IsTrue(model.Constraints.ContainsKey("public.a.v_positive"));
        Assert.IsTrue(model.Constraints.ContainsKey("public.b.v_positive"));
        Assert.AreNotEqual(
            model.Constraints["public.a.v_positive"].RawCheckExpression,
            model.Constraints["public.b.v_positive"].RawCheckExpression);
    }

    [TestMethod]
    public void ConstraintKeyIsCaseCanonicalized()
    {
        var model = BuildModel("create table Public.Orders (v integer, constraint V_Positive check (v > 0));");

        Assert.IsTrue(model.Constraints.ContainsKey("public.orders.v_positive"));
    }

    [TestMethod]
    public void ConstraintKeyDegradesWithoutTable()
    {
        Assert.AreEqual("public.orders.v_positive", DatabaseModel.CreateConstraintKey("public", "orders", "v_positive"));
        Assert.AreEqual("public.v_positive", DatabaseModel.CreateConstraintKey("public", null, "v_positive"));
        Assert.AreEqual("public.orders.orders_constraint", DatabaseModel.CreateConstraintKey("public", "orders", null));
        Assert.AreEqual("unknown_constraint", DatabaseModel.CreateConstraintKey(null, null, null));
    }

    private static DatabaseModel BuildModel(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
