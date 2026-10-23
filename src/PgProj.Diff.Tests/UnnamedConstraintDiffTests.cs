using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-42: an unnamed project constraint must compare equal to the deployed constraint PostgreSQL named
/// for it, and genuine changes must still be reported.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class UnnamedConstraintDiffTests
{
    private const string LiveShaped = """
        create table public.t (a integer, b integer, c integer);
        alter table public.t add constraint t_pkey primary key (a);
        alter table public.t add constraint t_b_check check (b > 0);
        alter table public.t add constraint t_c_key unique (c);
        """;

    private const string UnnamedProject = "create table public.t (a integer primary key, b integer check (b > 0), c integer unique);";

    [TestMethod]
    public void UnnamedProjectConstraintsMatchTheirDeployedNamedForm()
    {
        var result = new DiffEngine().Execute(BuildModel(LiveShaped), BuildModel(UnnamedProject));

        AssertNoConstraintChanges(result);
    }

    [TestMethod]
    public void SelfDiffIsEmpty()
    {
        var model = BuildModel(UnnamedProject);

        AssertNoConstraintChanges(new DiffEngine().Execute(model, model));
    }

    [TestMethod]
    public void AddingASecondUnnamedCheckOnTheSameColumnIsReportedAsAdded()
    {
        var project = BuildModel("create table public.t (a integer primary key, b integer check (b > 0) check (b < 100), c integer unique);");

        var result = new DiffEngine().Execute(BuildModel(LiveShaped), project);

        Assert.HasCount(1, result.AddedConstraints);
        Assert.AreEqual("public.t.t_b_check1", result.AddedConstraints[0].Key);
        Assert.IsEmpty(result.RemovedConstraints);
        Assert.IsEmpty(result.ModifiedConstraints);
    }

    [TestMethod]
    public void ChangingAnUnnamedCheckExpressionIsReportedAsModified()
    {
        var project = BuildModel("create table public.t (a integer primary key, b integer check (b > 5), c integer unique);");

        var result = new DiffEngine().Execute(BuildModel(LiveShaped), project);

        Assert.HasCount(1, result.ModifiedConstraints);
        Assert.AreEqual("public.t.t_b_check", result.ModifiedConstraints[0].NodeId);
    }

    [TestMethod]
    public void RemovingAnUnnamedConstraintIsReportedAsRemoved()
    {
        var project = BuildModel("create table public.t (a integer primary key, b integer check (b > 0), c integer);");

        var result = new DiffEngine().Execute(BuildModel(LiveShaped), project);

        Assert.HasCount(1, result.RemovedConstraints);
        Assert.AreEqual("public.t.t_c_key", result.RemovedConstraints[0].Key);
    }

    private static void AssertNoConstraintChanges(DiffResult result)
    {
        Assert.IsEmpty(result.AddedConstraints, string.Join(", ", result.AddedConstraints.Select(static constraint => constraint.Key)));
        Assert.IsEmpty(result.RemovedConstraints, string.Join(", ", result.RemovedConstraints.Select(static constraint => constraint.Key)));
        Assert.IsEmpty(result.ModifiedConstraints, string.Join(", ", result.ModifiedConstraints.Select(static change => change.NodeId)));
    }

    private static DatabaseModel BuildModel(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
