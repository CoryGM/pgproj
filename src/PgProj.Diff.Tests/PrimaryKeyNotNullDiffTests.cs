using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-39: a primary-key column written without NOT NULL must not diff against its deployed form
/// (where PostgreSQL made it NOT NULL), while genuine nullability differences must still be reported.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class PrimaryKeyNotNullDiffTests
{
    [TestMethod]
    public void InlinePrimaryKeyWithoutNotNullDoesNotDiffAgainstDeployedForm()
    {
        var project = BuildModel("create table public.t (id bigint primary key, name text);");
        var live = BuildModel("""
            create table public.t (id bigint not null, name text);
            alter table public.t add constraint t_pkey primary key (id);
            """);

        AssertNoColumnChanges(new DiffEngine().Execute(live, project));
    }

    [TestMethod]
    public void TableLevelCompositePrimaryKeyDoesNotDiffAgainstDeployedForm()
    {
        var project = BuildModel("create table public.t (a integer, b integer, constraint t_pkey primary key (a, b));");
        var live = BuildModel("""
            create table public.t (a integer not null, b integer not null);
            alter table public.t add constraint t_pkey primary key (a, b);
            """);

        AssertNoColumnChanges(new DiffEngine().Execute(live, project));
    }

    [TestMethod]
    public void SelfDiffIsEmpty()
    {
        var model = BuildModel("create table public.t (id bigint primary key, name text);");

        AssertNoColumnChanges(new DiffEngine().Execute(model, model));
    }

    [TestMethod]
    public void NonKeyColumnNullabilityDifferenceIsStillReported()
    {
        var project = BuildModel("create table public.t (id bigint primary key, name text);");
        var live = BuildModel("""
            create table public.t (id bigint not null, name text not null);
            alter table public.t add constraint t_pkey primary key (id);
            """);

        var result = new DiffEngine().Execute(live, project);

        Assert.HasCount(1, result.ModifiedColumns);
        Assert.AreEqual("public.t.name", result.ModifiedColumns[0].NodeId);
    }

    [TestMethod]
    public void DroppingThePrimaryKeyFromAProjectColumnIsStillReportedAsNullabilityChange()
    {
        var project = BuildModel("create table public.t (id bigint, name text);");
        var live = BuildModel("""
            create table public.t (id bigint not null, name text);
            alter table public.t add constraint t_pkey primary key (id);
            """);

        var result = new DiffEngine().Execute(live, project);

        Assert.IsTrue(result.ModifiedColumns.Any(static change => change.NodeId == "public.t.id"));
    }

    private static void AssertNoColumnChanges(DiffResult result)
    {
        Assert.IsEmpty(result.AddedColumns);
        Assert.IsEmpty(result.RemovedColumns);
        Assert.IsEmpty(result.ModifiedColumns, string.Join(", ", result.ModifiedColumns.Select(static change => change.NodeId)));
    }

    private static DatabaseModel BuildModel(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
