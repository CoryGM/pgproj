using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-325: a foreign key to a table the project does not define was reported three times, by <c>DeepConstraintMappingPass</c> ("missing foreign key
/// table 'app.customer'") and twice by <c>SemanticBinder</c> ("missing table", and "missing foreign key table 'customer'" without its schema). It is
/// reported once, schema-qualified, at the table's name; what else the binder reports is unchanged.
/// </summary>
[TestClass]
public sealed class MissingForeignKeyTableTests
{
    private const string File = "/project/sql/a.sql";

    [TestMethod]
    [DataRow("CREATE TABLE app.orders (id int PRIMARY KEY, customer_id int REFERENCES app.customer (id));", "app.orders.orders_customer_id_fkey", DisplayName = "inline")]
    [DataRow("CREATE TABLE app.orders (id int PRIMARY KEY, customer_id int, CONSTRAINT fk FOREIGN KEY (customer_id) REFERENCES app.customer (id));", "app.orders.fk", DisplayName = "table constraint")]
    [DataRow("CREATE TABLE app.orders (id int PRIMARY KEY, customer_id int);\nALTER TABLE app.orders ADD CONSTRAINT fk FOREIGN KEY (customer_id) REFERENCES app.customer (id);", "app.orders.fk", DisplayName = "ALTER TABLE")]
    [DataRow("CREATE TABLE app.orders (id int, customer_id int REFERENCES app.customer (id)) PARTITION BY RANGE (id);\nCREATE TABLE app.orders_1 PARTITION OF app.orders FOR VALUES FROM (0) TO (10);", "app.orders.orders_customer_id_fkey", DisplayName = "partitioned")]
    public void AMissingForeignKeyTableIsReportedOnce(string sql, string constraint)
    {
        var model = Build(sql, []);

        var reported = model.Diagnostics.Where(static diagnostic => diagnostic.Message.Contains("customer", StringComparison.Ordinal)).ToArray();

        Assert.HasCount(1, reported, string.Join("\n", reported.Select(static diagnostic => diagnostic.Message)));
        Assert.AreEqual($"Constraint '{constraint}' references missing foreign key table 'app.customer'.", reported[0].Message);
        Assert.AreEqual(SemanticDiagnosticSeverity.Warning, reported[0].Severity, "without schemas, an unresolved reference is a warning");
        Assert.IsNotNull(reported[0].Location?.Column, "placed at the table's name");
    }

    [TestMethod]
    public void AnUnqualifiedMissingTableIsNamedWithItsSchema()
    {
        var model = Build("CREATE TABLE orders (id int PRIMARY KEY, customer_id int REFERENCES customer (id));", []);

        var reported = model.Diagnostics.Where(static diagnostic => diagnostic.Message.Contains("customer'", StringComparison.Ordinal)).ToArray();

        Assert.HasCount(1, reported, string.Join("\n", reported.Select(static diagnostic => diagnostic.Message)));
        StringAssert.EndsWith(reported[0].Message, "references missing foreign key table 'public.customer'.");
    }

    [TestMethod]
    public void WithSchemasItIsOneError()
    {
        var model = Build("CREATE TABLE app.orders (id int PRIMARY KEY, customer_id int REFERENCES app.customer (id));", ["app"]);

        var reported = model.Diagnostics.Where(static diagnostic => diagnostic.Message.Contains("customer", StringComparison.Ordinal)).ToArray();

        Assert.HasCount(1, reported, string.Join("\n", reported.Select(static diagnostic => diagnostic.Message)));
        Assert.AreEqual(SemanticDiagnosticSeverity.Error, reported[0].Severity);
    }

    [TestMethod]
    public void AMissingReferencedColumnIsStillReported()
    {
        var model = Build("CREATE TABLE app.customer (id int PRIMARY KEY);\nCREATE TABLE app.orders (id int PRIMARY KEY, customer_id int REFERENCES app.customer (nope));", []);

        Assert.IsTrue(
            model.Diagnostics.Any(static diagnostic => diagnostic.Message == "Constraint 'app.orders.orders_customer_id_fkey' references missing foreign key column 'nope' on table 'app.customer'."),
            string.Join("\n", model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    [TestMethod]
    public void AMissingTableSomethingElseNamesIsStillReported()
    {
        // Only a foreign key's own target is left to DeepConstraintMappingPass; an index or a view on a missing table is the binder's as before.
        var model = Build("CREATE TABLE app.t (id int);\nCREATE INDEX t_x ON app.nope (id);\nCREATE VIEW app.v AS SELECT * FROM app.nowhere;", []);

        Assert.IsTrue(model.Diagnostics.Any(static diagnostic => diagnostic.Message.StartsWith("Index 'app.t_x'", StringComparison.Ordinal)));
        Assert.IsTrue(model.Diagnostics.Any(static diagnostic => diagnostic.Message.Contains("missing table 'app.nowhere'", StringComparison.Ordinal)));
    }

    private static DatabaseModel Build(string sql, IReadOnlyList<string> schemas)
        => new SemanticBuilder().Build([new PgQueryParser().Parse(sql) with { FilePath = File }], schemas);
}
