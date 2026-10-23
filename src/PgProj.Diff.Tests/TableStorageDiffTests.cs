using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>G-24b: a table's UNLOGGED, TABLESPACE, and WITH storage options are diffed.</summary>
[TestClass]
[DoNotParallelize]
public sealed class TableStorageDiffTests
{
    [TestMethod]
    public void SelfDiffOfATableWithStorageDetailsIsEmpty()
    {
        var model = Build("CREATE UNLOGGED TABLE public.t (a integer) WITH (fillfactor=70) TABLESPACE fastspace;");

        var diff = new DiffEngine().Diff(model, model);
        Assert.AreEqual(0, diff.Added.Count + diff.Removed.Count + diff.Modified.Count);
    }

    [TestMethod]
    public void AStorageOptionWrittenInADifferentOrderReportsNothing()
    {
        var oldModel = Build("CREATE TABLE public.t (a integer) WITH (fillfactor=70, autovacuum_enabled=false);");
        var newModel = Build("CREATE TABLE public.t (a integer) WITH (autovacuum_enabled=false, fillfactor=70);");

        var diff = new DiffEngine().Diff(oldModel, newModel);
        Assert.AreEqual(0, diff.Added.Count + diff.Removed.Count + diff.Modified.Count);
    }

    [TestMethod]
    public void BecomingUnloggedIsReported()
    {
        var oldModel = Build("CREATE TABLE public.t (a integer);");
        var newModel = Build("CREATE UNLOGGED TABLE public.t (a integer);");

        var change = new DiffEngine().Diff(oldModel, newModel).Modified.Single();
        var detail = change.Details.Single(static d => d.PropertyName == nameof(TableDefinition.IsUnlogged));
        Assert.AreEqual("False", detail.OldValue);
        Assert.AreEqual("True", detail.NewValue);
    }

    [TestMethod]
    public void AChangedTablespaceIsReported()
    {
        var oldModel = Build("CREATE TABLE public.t (a integer) TABLESPACE fastspace;");
        var newModel = Build("CREATE TABLE public.t (a integer) TABLESPACE slowspace;");

        var change = new DiffEngine().Diff(oldModel, newModel).Modified.Single();
        var detail = change.Details.Single(static d => d.PropertyName == nameof(TableDefinition.TablespaceName));
        Assert.AreEqual("fastspace", detail.OldValue);
        Assert.AreEqual("slowspace", detail.NewValue);
    }

    [TestMethod]
    public void AnAddedRemovedOrChangedStorageOptionIsReported()
    {
        var added = new DiffEngine().Diff(
            Build("CREATE TABLE public.t (a integer);"),
            Build("CREATE TABLE public.t (a integer) WITH (fillfactor=70);"));
        Assert.IsTrue(added.Modified.Single().Details.Any(static d => d.PropertyName == nameof(TableDefinition.StorageOptions)));

        var removed = new DiffEngine().Diff(
            Build("CREATE TABLE public.t (a integer) WITH (fillfactor=70);"),
            Build("CREATE TABLE public.t (a integer);"));
        Assert.IsTrue(removed.Modified.Single().Details.Any(static d => d.PropertyName == nameof(TableDefinition.StorageOptions)));

        var changed = new DiffEngine().Diff(
            Build("CREATE TABLE public.t (a integer) WITH (fillfactor=70);"),
            Build("CREATE TABLE public.t (a integer) WITH (fillfactor=80);"));
        Assert.IsTrue(changed.Modified.Single().Details.Any(static d => d.PropertyName == nameof(TableDefinition.StorageOptions)));
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
