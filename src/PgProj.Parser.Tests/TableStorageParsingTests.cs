using PgProj.Parser;

namespace PgProj.Parser.Tests;

/// <summary>
/// G-24b: a table's <c>UNLOGGED</c> keyword, <c>WITH (...)</c> storage parameters, and <c>TABLESPACE</c> are captured. Each
/// storage parameter is kept as written (verbatim, not re-rendered), the same reasoning as <c>ConstraintIndexTextReader</c> (G-73).
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class TableStorageParsingTests
{
    [TestMethod]
    public void AnOrdinaryTableHasNoStorageDetails()
    {
        var table = new PgQueryParser().Parse("CREATE TABLE public.t (a integer);").Tables.Single();

        Assert.IsFalse(table.IsUnlogged);
        Assert.IsNull(table.TablespaceName);
        Assert.IsEmpty(table.StorageOptions);
    }

    [TestMethod]
    public void AnUnloggedTableIsCaptured()
    {
        var table = new PgQueryParser().Parse("CREATE UNLOGGED TABLE public.t (a integer);").Tables.Single();

        Assert.IsTrue(table.IsUnlogged);
    }

    [TestMethod]
    public void ATablespaceIsCapturedAsWritten()
    {
        var table = new PgQueryParser().Parse("CREATE TABLE public.t (a integer) TABLESPACE fastspace;").Tables.Single();

        Assert.AreEqual("fastspace", table.TablespaceName);
    }

    [TestMethod]
    public void StorageOptionsAreKeptAsWrittenInOrder()
    {
        var table = new PgQueryParser().Parse("CREATE TABLE public.t (a integer) WITH (fillfactor = 70, autovacuum_enabled=false);").Tables.Single();

        CollectionAssert.AreEqual(
            new[] { "fillfactor = 70", "autovacuum_enabled=false" },
            table.StorageOptions.Select(static option => option.RawText).ToArray());
        CollectionAssert.AreEqual(new[] { "fillfactor", "autovacuum_enabled" }, table.StorageOptions.Select(static option => option.Name).ToArray());
    }

    [TestMethod]
    public void ANamespacedStorageOptionKeepsItsDot()
    {
        var table = new PgQueryParser().Parse("CREATE TABLE public.t (a integer) WITH (toast.autovacuum_enabled = true);").Tables.Single();

        Assert.AreEqual("toast.autovacuum_enabled", table.StorageOptions.Single().Name);
        Assert.AreEqual("toast.autovacuum_enabled = true", table.StorageOptions.Single().RawText);
    }

    [TestMethod]
    public void UnloggedWithAndTablespaceAllAppearTogether()
    {
        var table = new PgQueryParser().Parse("CREATE UNLOGGED TABLE public.t (a integer) WITH (fillfactor=70) TABLESPACE fastspace;").Tables.Single();

        Assert.IsTrue(table.IsUnlogged);
        Assert.AreEqual("fastspace", table.TablespaceName);
        Assert.AreEqual("fillfactor=70", table.StorageOptions.Single().RawText);
    }

    [TestMethod]
    public void EachTableOfAMultiStatementScriptReadsItsOwnOptions()
    {
        var tree = new PgQueryParser().Parse(
            "CREATE SCHEMA s;\n\n-- a comment mentioning WITH (x)\nCREATE TABLE s.t (a integer) WITH (fillfactor=50);\nCREATE TABLE s.u (b integer) WITH (fillfactor=60);");

        Assert.AreEqual("fillfactor=50", tree.Tables.Single(static t => t.Name == "t").StorageOptions.Single().RawText);
        Assert.AreEqual("fillfactor=60", tree.Tables.Single(static t => t.Name == "u").StorageOptions.Single().RawText);
    }

    [TestMethod]
    public void AQuotedStringOptionValueIsKeptAsWritten()
    {
        var table = new PgQueryParser().Parse("CREATE TABLE public.t (a integer) WITH (autovacuum_vacuum_scale_factor = 0.1, fillfactor = 70);").Tables.Single();

        CollectionAssert.AreEqual(
            new[] { "autovacuum_vacuum_scale_factor = 0.1", "fillfactor = 70" },
            table.StorageOptions.Select(static option => option.RawText).ToArray());
    }
}
