using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// Regression coverage for G-38: same-named constraints on different tables diff independently,
/// without phantom changes and without hiding a real change on either table.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ConstraintIdentityDiffTests
{
    private const string TwoTables = """
        create table public.a (id integer, v integer, constraint v_positive check (v > 0));
        create table public.b (id integer, v integer, constraint v_positive check (v > 0));
        """;

    [TestMethod]
    public void SelfDiffOfSameNamedConstraintsIsEmpty()
    {
        var model = BuildModel(TwoTables);

        AssertNoConstraintChanges(new DiffEngine().Execute(model, model));
    }

    [TestMethod]
    public void CosmeticDifferencesAndDeclarationOrderProduceNoConstraintChanges()
    {
        var oldModel = BuildModel(TwoTables);
        var newModel = BuildModel("""
            CREATE TABLE Public.B (id integer, v integer, CONSTRAINT V_Positive CHECK (v   >   0));
            CREATE TABLE Public.A (id integer, v integer, CONSTRAINT V_POSITIVE CHECK (v>0));
            """);

        AssertNoConstraintChanges(new DiffEngine().Execute(oldModel, newModel));
    }

    [TestMethod]
    public void ChangingOneOfTwoSameNamedConstraintsReportsOnlyThatTable()
    {
        var oldModel = BuildModel(TwoTables);
        var newModel = BuildModel("""
            create table public.a (id integer, v integer, constraint v_positive check (v > 0));
            create table public.b (id integer, v integer, constraint v_positive check (v > 100));
            """);

        var result = new DiffEngine().Execute(oldModel, newModel);

        Assert.IsEmpty(result.AddedConstraints);
        Assert.IsEmpty(result.RemovedConstraints);
        Assert.HasCount(1, result.ModifiedConstraints);
        Assert.AreEqual("public.b.v_positive", result.ModifiedConstraints[0].NodeId);
    }

    [TestMethod]
    public void MissingSecondConstraintIsReportedAsRemovedNotHidden()
    {
        // Before G-38 the two constraints shared one key, so dropping one of them was invisible.
        var oldModel = BuildModel(TwoTables);
        var newModel = BuildModel("""
            create table public.a (id integer, v integer, constraint v_positive check (v > 0));
            create table public.b (id integer, v integer);
            """);

        var result = new DiffEngine().Execute(oldModel, newModel);

        Assert.HasCount(1, result.RemovedConstraints);
        Assert.AreEqual("public.b.v_positive", result.RemovedConstraints[0].Key);
    }

    [TestMethod]
    public void AddingTheSameNamedConstraintToASecondTableIsReportedAsAdded()
    {
        var oldModel = BuildModel("""
            create table public.a (id integer, v integer, constraint v_positive check (v > 0));
            create table public.b (id integer, v integer);
            """);
        var newModel = BuildModel(TwoTables);

        var result = new DiffEngine().Execute(oldModel, newModel);

        Assert.HasCount(1, result.AddedConstraints);
        Assert.AreEqual("public.b.v_positive", result.AddedConstraints[0].Key);
    }

    private static void AssertNoConstraintChanges(DiffResult result)
    {
        Assert.IsEmpty(result.AddedConstraints);
        Assert.IsEmpty(result.RemovedConstraints);
        Assert.IsEmpty(result.ModifiedConstraints);
    }

    private static DatabaseModel BuildModel(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
