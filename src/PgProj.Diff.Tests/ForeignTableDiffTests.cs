using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-133: a foreign table compared: the same written another way is no change (options in another order, pg_dump's <c>ALTER ... OPTIONS</c> form); a changed server,
/// table option, or column option is reported by name; an ordinary table and a foreign one of the same name differ.
/// </summary>
[TestClass]
public sealed class ForeignTableDiffTests
{
    private const string Server = "CREATE FOREIGN DATA WRAPPER w; CREATE SERVER remote FOREIGN DATA WRAPPER w; CREATE SERVER other FOREIGN DATA WRAPPER w;\n";
    private const string Table = "CREATE FOREIGN TABLE public.ft (a integer OPTIONS (column_name 'x', quoted 'y'), b text) SERVER remote OPTIONS (schema_name 's', table_name 't');\n";

    [TestMethod]
    public void TheSameWrittenAnotherWayIsNoChange()
    {
        Assert.IsEmpty(Changes(Table, Table).Modified);
        Assert.IsEmpty(Changes(Table, Table.Replace("schema_name 's', table_name 't'", "table_name 't', schema_name 's'", StringComparison.Ordinal)).Modified, "Option order means nothing.");
        Assert.IsEmpty(
            Changes(Table, "CREATE FOREIGN TABLE public.ft (a integer, b text) SERVER remote OPTIONS (schema_name 's', table_name 't');\nALTER FOREIGN TABLE ONLY public.ft ALTER COLUMN a OPTIONS (quoted 'y', column_name 'x');\n").Modified,
            "pg_dump's form is the same table.");
    }

    [TestMethod]
    public void AChangedServerIsReported()
    {
        var detail = Changes(Table, Table.Replace("SERVER remote", "SERVER other", StringComparison.Ordinal)).Modified.Single().Details.Single();

        Assert.AreEqual(nameof(TableDefinition.ForeignServer), detail.PropertyName);
        Assert.AreEqual("remote -> other", $"{detail.OldValue} -> {detail.NewValue}");
    }

    [TestMethod]
    public void AChangedTableOptionIsReported()
    {
        var detail = Changes(Table, Table.Replace("table_name 't'", "table_name 'u'", StringComparison.Ordinal)).Modified.Single().Details.Single();

        Assert.AreEqual("ForeignOptions", detail.PropertyName);
        StringAssert.Contains(detail.NewValue, "table_name=u");
    }

    [TestMethod]
    public void AChangedColumnOptionIsReported()
    {
        var changes = Changes(Table, Table.Replace("quoted 'y'", "quoted 'z'", StringComparison.Ordinal)).Modified;
        var details = changes.Single(static change => change.NodeId == "public.ft.a").Details;

        var detail = details.Single();
        StringAssert.Contains(detail.PropertyName, "ForeignOptions");
        StringAssert.Contains(detail.NewValue, "quoted=z");
    }

    [TestMethod]
    public void AnOrdinaryTableBecomingForeignIsAChangedServer()
    {
        var change = Changes("CREATE TABLE public.ft (a integer, b text);\n", Table).Modified.Single(static change => change.NodeId == "public.ft");

        Assert.IsTrue(change.Details.Any(static detail => detail.PropertyName == nameof(TableDefinition.ForeignServer)), string.Join(", ", change.Details.Select(static detail => detail.PropertyName)));
    }

    private static ChangeSet Changes(string live, string project) => new DiffEngine().Diff(Build(Server + live), Build(Server + project));

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([AlterTableColumnApplier.Apply([new PgQueryParser().Parse(sql)]).Single()]);
}
