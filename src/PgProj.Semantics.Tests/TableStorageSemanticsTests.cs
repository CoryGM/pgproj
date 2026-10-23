using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>G-24b: a table's UNLOGGED/WITH/TABLESPACE reach the semantic model, and storage options get a canonical order.</summary>
[TestClass]
[DoNotParallelize]
public sealed class TableStorageSemanticsTests
{
    [TestMethod]
    public void UnloggedTablespaceAndOptionsAreCarriedOntoTheDefinition()
    {
        var model = Build("CREATE UNLOGGED TABLE public.t (a integer) WITH (fillfactor=70, autovacuum_enabled=false) TABLESPACE fastspace;");

        var table = model.Tables["public.t"];
        Assert.IsTrue(table.IsUnlogged);
        Assert.AreEqual("fastspace", table.TablespaceName);
        CollectionAssert.AreEqual(new[] { "fillfactor=70", "autovacuum_enabled=false" }, table.StorageOptions.Select(static o => o.RawText).ToArray());
    }

    [TestMethod]
    public void NormalizedStorageOptionsAreSortedByNameRegardlessOfWrittenOrder()
    {
        var writtenOneWay = Build("CREATE TABLE public.t (a integer) WITH (fillfactor=70, autovacuum_enabled=false);").Tables["public.t"];
        var writtenTheOtherWay = Build("CREATE TABLE public.t (a integer) WITH (autovacuum_enabled=false, fillfactor=70);").Tables["public.t"];

        CollectionAssert.AreEqual(
            writtenOneWay.NormalizedStorageOptions.Select(static o => o.RawText).ToArray(),
            writtenTheOtherWay.NormalizedStorageOptions.Select(static o => o.RawText).ToArray());
        // The written order is preserved for deploy output.
        Assert.AreEqual("fillfactor=70", writtenOneWay.StorageOptions[0].RawText);
        Assert.AreEqual("autovacuum_enabled=false", writtenTheOtherWay.StorageOptions[0].RawText);
    }

    [TestMethod]
    public void AnOrdinaryTableHasNoStorageDetails()
    {
        var table = Build("CREATE TABLE public.t (a integer);").Tables["public.t"];

        Assert.IsFalse(table.IsUnlogged);
        Assert.IsNull(table.TablespaceName);
        Assert.IsEmpty(table.StorageOptions);
        Assert.IsEmpty(table.NormalizedStorageOptions);
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
