using PgProj.Core;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core.Tests;

/// <summary>
/// G-133: the statement the live loader writes for a foreign table reads back as the project writes it (its server, its options, its columns' options, its comment), and
/// <c>init</c> writes it to <c>Schema/&lt;schema&gt;/ForeignTable/&lt;name&gt;.sql</c>. (The catalog queries are checked against a real server by the integration tests.)
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class LiveForeignTableSnapshotTests
{
    private static readonly LiveForeignDataSnapshot ForeignData = new([], [new LiveForeignServerSnapshot("remote", "postgres_fdw", null, null, [], null)], []);

    private static readonly LiveTableSnapshot Table = new(
        "app",
        "remote_orders",
        [
            new LiveColumnSnapshot("id", "integer", false, null, false, null, null) { ForeignOptions = [new("column_name", "order_id")], Storage = "plain", IsDefaultStorage = true },
            new LiveColumnSnapshot("note", "text", true, "'none'::text", false, null, null) { Storage = "extended", IsDefaultStorage = true },
        ],
        Comment: "remote rows")
    {
        ForeignServer = "remote",
        ForeignOptions = [new("schema_name", "sales"), new("table_name", "orders")],
    };

    [TestMethod]
    public void TheSnapshotReadsBackAsAForeignTable()
    {
        var sql = LiveDatabaseModelLoader.GenerateSnapshotSql([new LiveSchemaSnapshot("app")], [Table], [], [], foreignData: ForeignData);
        var live = new LiveDatabaseModelLoader(new PgQueryParser(), new SemanticBuilder()).BuildModelFromSnapshotSql(sql);

        var table = live.Tables["app.remote_orders"];
        Assert.AreEqual("remote", table.ForeignServer);
        Assert.AreEqual("schema_name=sales, table_name=orders", string.Join(", ", ForeignOptions.Describe(table.ForeignOptions)));
        Assert.AreEqual("column_name=order_id", string.Join(", ", ForeignOptions.Describe(table.Columns.Single(static column => column.Name == "id").ForeignOptions)));
        Assert.AreEqual("remote rows", table.Comment);
        StringAssert.Contains(sql, "COMMENT ON FOREIGN TABLE app.remote_orders IS 'remote rows';");
        Assert.IsFalse(live.Diagnostics.Any(static diagnostic => diagnostic.Severity == SemanticDiagnosticSeverity.Error), string.Join(" | ", live.Diagnostics.Select(static d => d.Message)));
    }

    [TestMethod]
    public void InitWritesItInAForeignTableFolder()
    {
        var files = LiveDatabaseModelLoader.BuildScaffoldFiles([new LiveSchemaSnapshot("app")], [Table], [], [], [], [], [], [new LiveGrantSnapshot("app", "table:app.remote_orders()", "reader", "GRANT SELECT ON TABLE app.remote_orders TO reader")], [], [], [], foreignData: ForeignData);

        var content = files.Single(static file => file.RelativePath == "Schema/app/ForeignTable/remote_orders.sql").Content.Replace("\r\n", "\n", StringComparison.Ordinal);
        StringAssert.StartsWith(content, "CREATE FOREIGN TABLE app.remote_orders (\n    id integer OPTIONS (column_name 'order_id') NOT NULL,\n    note text DEFAULT 'none'::text\n)\nSERVER remote OPTIONS (schema_name 'sales', table_name 'orders');\n");
        StringAssert.Contains(content, "GRANT SELECT ON TABLE app.remote_orders TO reader;");
        Assert.DoesNotContain("STORAGE", content);
    }
}
