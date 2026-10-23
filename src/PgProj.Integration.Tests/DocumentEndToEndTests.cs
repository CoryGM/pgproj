using System.Text.Json;
using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-159 against a real server, on every version: <c>pgproj document --source-connection</c> documents the live database, a project scaffolded from it documents to the same dictionary,
/// the comments' tags come through the database (<c>pg_description</c>) as they do from files, and all three formats are written.
/// </summary>
[TestClass]
public sealed class DocumentEndToEndTests
{
    private const string Sql = """
        CREATE SCHEMA app;
        CREATE SCHEMA audit;
        CREATE TABLE app.customers (id bigint PRIMARY KEY, email text NOT NULL UNIQUE, born date, price numeric(10,2) DEFAULT 0, note varchar(40));
        CREATE TABLE app.orders (id bigint PRIMARY KEY, customer_id bigint NOT NULL REFERENCES app.customers (id) ON DELETE CASCADE, total numeric(10,2) CHECK (total >= 0));
        CREATE INDEX orders_customer ON app.orders (customer_id);
        CREATE TABLE audit.entry (id bigint PRIMARY KEY, order_id bigint REFERENCES app.orders (id));
        CREATE VIEW app.v_customers AS SELECT id, email FROM app.customers;
        CREATE TYPE app.mood AS ENUM ('sad', 'ok');
        CREATE FUNCTION app.add(a int, b int) RETURNS int LANGUAGE sql IMMUTABLE AS 'SELECT a + b';
        COMMENT ON TABLE app.customers IS $$A person or company that buys from us.

        @business-name: Customer
        @owner: Sales Ops
        @retention-months: 84
        @see-also: app.orders
        @team: growth$$;
        COMMENT ON COLUMN app.customers.email IS $$Where receipts go.

        @business-name: Customer Email
        @pii: true
        @classification: confidential
        @example: jane@example.com$$;
        COMMENT ON COLUMN app.customers.born IS $$@retention-months: 12
        @retention-date: 2030-01-01$$;
        COMMENT ON VIEW app.v_customers IS 'The customers, without the extras.';
        """;

    [TestMethod]
    public async Task ALiveDatabaseAndTheProjectScaffoldedFromItDocumentTheSame()
    {
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            var live = await RunAsync("document", "--source-connection", connectionString, "--output", Path.Combine(directory, "live"), "--format", "json,markdown,html", "--no-fail-on-doc-errors");
            Assert.AreEqual(0, live.ExitCode, live.Output);

            var project = Path.Combine(directory, "pgproj.json");
            var init = await RunAsync("init", "--source-connection", connectionString, "--project", project);
            Assert.AreEqual(0, init.ExitCode, init.Output);
            var fromProject = await RunAsync("document", "--project", project, "--output", Path.Combine(directory, "project"), "--format", "json", "--no-fail-on-doc-errors");
            Assert.AreEqual(0, fromProject.ExitCode, fromProject.Output);

            foreach (var extension in new[] { "html", "md", "json" })
            {
                Assert.IsTrue(File.Exists(Path.Combine(directory, "live", $"database-documentation.{extension}")), extension);
            }

            using var liveJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "live", "database-documentation.json")));
            using var projectJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "project", "database-documentation.json")));
            Assert.AreEqual("database", liveJson.RootElement.GetProperty("source").GetProperty("kind").GetString());
            Assert.AreEqual("project", projectJson.RootElement.GetProperty("source").GetProperty("kind").GetString());

            var liveTables = Tables(liveJson);
            var projectTables = Tables(projectJson);
            CollectionAssert.AreEqual(new[] { "app.customers", "app.orders", "audit.entry" }, liveTables.Keys.Order().ToArray());
            CollectionAssert.AreEqual(liveTables.Keys.Order().ToArray(), projectTables.Keys.Order().ToArray());

            foreach (var (name, liveTable) in liveTables)
            {
                var projectTable = projectTables[name];
                CollectionAssert.AreEqual(Columns(liveTable), Columns(projectTable), name);
                CollectionAssert.AreEqual(Constraints(liveTable), Constraints(projectTable), name);
            }

            // The tags came through the database's comments.
            var customers = liveTables["app.customers"];
            Assert.AreEqual("A person or company that buys from us.", customers.GetProperty("description").GetString());
            Assert.AreEqual("Customer", customers.GetProperty("businessName").GetString());
            Assert.AreEqual("Sales Ops", customers.GetProperty("owner").GetString());
            Assert.AreEqual(84, customers.GetProperty("retentionMonths").GetInt32());
            Assert.AreEqual("growth", customers.GetProperty("custom").GetProperty("team")[0].GetString());
            Assert.AreEqual("app.orders", customers.GetProperty("seeAlso")[0].GetProperty("target").GetString());
            var email = customers.GetProperty("columns").EnumerateArray().Single(static c => c.GetProperty("name").GetString() == "email");
            Assert.IsTrue(email.GetProperty("pii").GetBoolean());
            Assert.AreEqual("confidential", email.GetProperty("classification").GetString());
            Assert.AreEqual("jane@example.com", email.GetProperty("examples")[0].GetString());
            Assert.AreEqual(1, liveJson.RootElement.GetProperty("counts").GetProperty("errors").GetInt32(), "born has both retention tags.");
            Assert.AreEqual(1, projectJson.RootElement.GetProperty("counts").GetProperty("errors").GetInt32());

            var orders = liveTables["app.orders"];
            Assert.AreEqual("PRIMARY KEY", orders.GetProperty("constraints")[0].GetProperty("type").GetString());
            var foreignKey = orders.GetProperty("constraints").EnumerateArray().Single(static c => c.GetProperty("type").GetString() == "FOREIGN KEY");
            Assert.AreEqual("CASCADE", foreignKey.GetProperty("onDelete").GetString(), "A database stores the action as a letter; the document says the words.");
            Assert.AreEqual("The customers, without the extras.", liveJson.RootElement.GetProperty("schemas")[0].GetProperty("views")[0].GetProperty("description").GetString());
            StringAssert.StartsWith(liveJson.RootElement.GetProperty("erd").GetString(), "erDiagram");
        });
    }

    [TestMethod]
    public async Task ADatabaseWithTagErrorsExitsWithOneUnlessToldNotTo()
    {
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            var failing = await RunAsync("document", "--source-connection", connectionString, "--output", Path.Combine(directory, "a"), "--format", "json");
            var lenient = await RunAsync("document", "--source-connection", connectionString, "--output", Path.Combine(directory, "b"), "--format", "json", "--no-fail-on-doc-errors");

            Assert.AreEqual(1, failing.ExitCode, failing.Output);
            StringAssert.Contains(failing.Output, "app.customers.born");
            Assert.AreEqual(0, lenient.ExitCode, lenient.Output);
            Assert.IsTrue(File.Exists(Path.Combine(directory, "a", "database-documentation.json")), "The files are written either way.");
        });
    }

    [TestMethod]
    public async Task TheSchemaOptionsLimitALiveDocument()
    {
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            var run = await RunAsync("document", "--source-connection", connectionString, "--output", directory, "--format", "json", "--schemas", "audit", "--no-fail-on-doc-errors");

            Assert.AreEqual(0, run.ExitCode, run.Output);
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "database-documentation.json")));
            Assert.AreEqual(1, json.RootElement.GetProperty("schemas").GetArrayLength());
            Assert.AreEqual("audit", json.RootElement.GetProperty("schemas")[0].GetProperty("name").GetString());
            StringAssert.Contains(json.RootElement.GetProperty("erd").GetString(), "(external)");
        });
    }

    private static Dictionary<string, JsonElement> Tables(JsonDocument document)
        => document.RootElement.GetProperty("schemas").EnumerateArray()
            .SelectMany(static schema => schema.GetProperty("tables").EnumerateArray())
            .ToDictionary(static table => table.GetProperty("qualifiedName").GetString()!, static table => table.Clone(), StringComparer.Ordinal);

    private static string[] Columns(JsonElement table)
        => table.GetProperty("columns").EnumerateArray()
            .Select(static column => string.Join("|", column.GetProperty("name").GetString(), column.GetProperty("type").GetString(), column.GetProperty("nullable").GetBoolean(), column.GetProperty("primaryKey").GetBoolean(), column.GetProperty("foreignKey").GetBoolean(), column.GetProperty("unique").GetBoolean()))
            .ToArray();

    private static string[] Constraints(JsonElement table)
        => table.GetProperty("constraints").EnumerateArray()
            .Select(static constraint => constraint.GetProperty("type").GetString() + ":" + string.Join(",", constraint.GetProperty("columns").EnumerateArray().Select(static column => column.GetString())))
            .Order()
            .ToArray();

    private static async Task WithDatabaseAsync(Func<string, string, Task> test)
    {
        var databaseName = await CreateDatabaseAsync("g159");
        var directory = Path.Combine(Path.GetTempPath(), "pgproj-g159-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            await ExecuteAsync(connectionString, Sql);
            await test(connectionString, directory);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(directory);
        }
    }
}
