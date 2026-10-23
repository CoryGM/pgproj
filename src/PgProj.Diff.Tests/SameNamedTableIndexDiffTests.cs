using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// Regression coverage for G-89: an index belongs to the table in its own schema, so same-named tables in two
/// schemas each get exactly their own indexes, rather than every index of either table being reported once per
/// same-named table.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SameNamedTableIndexDiffTests
{
    private const string Schemas = """
        create schema a;
        create schema b;
        create table a.pp (id integer, emails jsonb);
        create table b.pp (id integer, emails jsonb);
        """;

    private const string AIndex = "create index a_pp_idx on a.pp using gin (emails);";

    private const string BIndex = "create index b_pp_idx on b.pp using gin (emails);";

    [TestMethod]
    public void AddingSameNamedTablesReportsEachIndexOnce()
    {
        var newModel = BuildModel(Schemas + AIndex + BIndex);

        var result = new DiffEngine().Execute(DatabaseModel.Empty, newModel);

        CollectionAssert.AreEquivalent(
            new[] { "a.a_pp_idx", "b.b_pp_idx" },
            result.AddedIndexes.Select(static index => index.Key).ToArray());
    }

    [TestMethod]
    public void SelfDiffOfSameNamedTablesWithIndexesIsEmpty()
    {
        var model = BuildModel(Schemas + AIndex + BIndex);

        var result = new DiffEngine().Execute(model, model);

        AssertNoIndexChanges(result);
    }

    [TestMethod]
    public void CosmeticDifferencesProduceNoIndexChanges()
    {
        var oldModel = BuildModel(Schemas + AIndex + BIndex);
        var newModel = BuildModel(Schemas
            + "CREATE   INDEX B_PP_IDX ON B.PP USING GIN (emails);\n"
            + "CREATE INDEX a_pp_idx   ON A.Pp USING gin (Emails);");

        var result = new DiffEngine().Execute(oldModel, newModel);

        AssertNoIndexChanges(result);
    }

    [TestMethod]
    public void ChangingTheIndexOfOneSameNamedTableReportsOnlyThatIndex()
    {
        var oldModel = BuildModel(Schemas + AIndex + BIndex);
        var newModel = BuildModel(Schemas + AIndex + "create index b_pp_idx on b.pp (id);");

        var result = new DiffEngine().Execute(oldModel, newModel);

        Assert.IsEmpty(result.AddedIndexes);
        Assert.IsEmpty(result.RemovedIndexes);
        Assert.HasCount(1, result.ModifiedIndexes);
        Assert.AreEqual("b.b_pp_idx", result.ModifiedIndexes[0].NodeId);
    }

    [TestMethod]
    public void RemovingOneSameNamedTableRemovesOnlyItsOwnIndex()
    {
        var oldModel = BuildModel(Schemas + AIndex + BIndex);
        var newModel = BuildModel("""
            create schema a;
            create schema b;
            create table a.pp (id integer, emails jsonb);
            create index a_pp_idx on a.pp using gin (emails);
            """);

        var result = new DiffEngine().Execute(oldModel, newModel);

        Assert.IsEmpty(result.AddedIndexes);
        Assert.IsEmpty(result.ModifiedIndexes);
        Assert.HasCount(1, result.RemovedIndexes);
        Assert.AreEqual("b.b_pp_idx", result.RemovedIndexes[0].Key);
    }

    [TestMethod]
    [DataRow("create table pp (id integer); create index pp_idx on pp (id);")]
    [DataRow("create table pp (id integer); create index pp_idx on public.pp (id);")]
    [DataRow("create table public.pp (id integer); create index pp_idx on pp (id);")]
    public void AnUnqualifiedTableOrIndexStillMatchesThePublicSchema(string sql)
    {
        var newModel = BuildModel(sql);

        var result = new DiffEngine().Execute(DatabaseModel.Empty, newModel);

        Assert.HasCount(1, result.AddedIndexes);
        Assert.IsEmpty(result.Diagnostics.Where(static diagnostic => diagnostic.Message.Contains("Index", StringComparison.Ordinal)).ToArray());
    }

    private static void AssertNoIndexChanges(DiffResult result)
    {
        Assert.IsEmpty(result.AddedIndexes);
        Assert.IsEmpty(result.RemovedIndexes);
        Assert.IsEmpty(result.ModifiedIndexes);
    }

    private static DatabaseModel BuildModel(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
