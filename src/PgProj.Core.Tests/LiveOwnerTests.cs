using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core.Tests;

/// <summary>
/// G-146: a live database's owners are matched to the objects the loader read (an owner never names an object the model lacks), written to the snapshot as
/// <c>ALTER ... OWNER TO</c> and read back as owners, and scaffolded by <c>init</c> into each object's own file. A sequence a column owns has no owner of its own
/// to scaffold. With owners excluded, none are read.
/// </summary>
[TestClass]
public sealed class LiveOwnerTests
{
    private static readonly LiveSchemaSnapshot[] Schemas = [new("public"), new("app")];

    private static readonly LiveTableSnapshot[] Tables = [new("app", "t", [new LiveColumnSnapshot("id", "integer", false, null, false, null, null)])];

    private static readonly LiveViewSnapshot[] Views =
    [
        new("app", "v", "SELECT id FROM app.t"),
        new("app", "mv", "SELECT id FROM app.t", IsMaterialized: true),
    ];

    private static readonly LiveSequenceSnapshot[] Sequences =
    [
        new("app", "s", "bigint", "1", "1", "9223372036854775807", "1", false, "1", null, null, null),
        new("app", "t_id_seq", "integer", "1", "1", "2147483647", "1", false, "1", "app", "t", "id"),
    ];

    private static readonly LiveFunctionSnapshot[] Functions =
    [
        new("app", "f", "CREATE OR REPLACE FUNCTION app.f(a integer, b text) RETURNS integer LANGUAGE sql AS $$ SELECT a $$", IdentityArguments: "a integer, b text"),
        new("app", "p", "CREATE OR REPLACE PROCEDURE app.p() LANGUAGE sql AS $$ SELECT 1 $$", IdentityArguments: string.Empty, IsProcedure: true),
    ];

    private static readonly LiveTypeSnapshot[] Types =
    [
        new("app", "mood", "user-defined", "CREATE TYPE app.mood AS ENUM ('a', 'b')"),
        new("app", "d", "domain", "CREATE DOMAIN app.d AS integer"),
        new("public", "citext", "extension", "CREATE EXTENSION IF NOT EXISTS citext SCHEMA public"),
    ];

    private static readonly LiveEventTriggerSnapshot[] EventTriggers = [new("audit", "ddl_command_end", [], "app", "f", "O")];

    private static readonly LivePublicationSnapshot[] Publications = [];

    private static LiveDatabaseModelLoader.OwnerRow Row(string kind, string? schema, string name, string owner = "lc_owner", string arguments = "")
        => new(kind, schema, name, arguments, null, null, owner);

    [TestMethod]
    public void AnOwnerIsWrittenForEachKindOfObjectTheLoaderRead()
    {
        // Arrange
        LiveDatabaseModelLoader.OwnerRow[] rows =
        [
            Row("schema", "app", "app"),
            Row("table", "app", "t"),
            Row("view", "app", "v"),
            Row("materialized view", "app", "mv"),
            Row("sequence", "app", "s"),
            Row("function", "app", "f", arguments: "a integer, b text"),
            Row("procedure", "app", "p"),
            Row("type", "app", "mood"),
            Row("domain", "app", "d"),
            Row("event trigger", null, "audit"),
        ];

        // Act
        var owners = LiveDatabaseModelLoader.BuildOwnerSnapshots(rows, Schemas, Tables, Views, Sequences, Functions, Types, [], [], EventTriggers, Publications);

        // Assert
        var statements = owners.Select(static owner => owner.Statement).ToHashSet();
        CollectionAssert.IsSubsetOf(
            new[]
            {
                "ALTER SCHEMA app OWNER TO lc_owner;",
                "ALTER TABLE app.t OWNER TO lc_owner;",
                "ALTER VIEW app.v OWNER TO lc_owner;",
                "ALTER MATERIALIZED VIEW app.mv OWNER TO lc_owner;",
                "ALTER SEQUENCE app.s OWNER TO lc_owner;",
                "ALTER FUNCTION app.f(a integer, b text) OWNER TO lc_owner;",
                "ALTER PROCEDURE app.p() OWNER TO lc_owner;",
                "ALTER TYPE app.mood OWNER TO lc_owner;",
                "ALTER DOMAIN app.d OWNER TO lc_owner;",
                "ALTER EVENT TRIGGER audit OWNER TO lc_owner;",
            },
            statements.ToArray());
        Assert.AreEqual(10, owners.Count);
    }

    [TestMethod]
    public void AnOwnerOfSomethingTheLoaderDidNotReadIsLeftOut()
    {
        // Arrange: a table in a schema outside the project's scope, an extension's type, a function that is not among those read, and a system schema.
        LiveDatabaseModelLoader.OwnerRow[] rows =
        [
            Row("table", "other", "t"),
            Row("type", "public", "citext"),
            Row("function", "app", "unread", arguments: "x integer"),
            Row("function", "app", "f", arguments: "a integer"),
            Row("schema", "other", "other"),
            Row("table", "app", "t"),
        ];

        // Act
        var owners = LiveDatabaseModelLoader.BuildOwnerSnapshots(rows, Schemas, Tables, Views, Sequences, Functions, Types, [], [], EventTriggers, Publications);

        // Assert: only the table that was read (an overload with other arguments is another function).
        Assert.AreEqual("ALTER TABLE app.t OWNER TO lc_owner;", owners.Single().Statement);
    }

    [TestMethod]
    public void AnAggregateAndAnOperatorAreNamedByTheirArgumentsAndOperands()
    {
        // Arrange
        var aggregates = new[] { new LiveAggregateSnapshot("app", "agg", "integer", "n", -1, "int4pl", "integer", 0, null, false, "r", null, null, null, "0", null, null, null, 0, null, false, "r", null, null, "u") };
        var operators = new[] { new LiveOperatorSnapshot("app", "#+#", "integer", "text", "pg_catalog", "f", null, null, null, null, null, null, false, false) };
        LiveDatabaseModelLoader.OwnerRow[] rows =
        [
            Row("aggregate", "app", "agg", arguments: "integer"),
            Row("aggregate", "app", "agg", arguments: string.Empty),
            new("operator", "app", "#+#", string.Empty, "integer", "text", "lc_owner"),
            new("operator", "app", "#+#", string.Empty, "integer", "integer", "lc_owner"),
        ];

        // Act
        var owners = LiveDatabaseModelLoader.BuildOwnerSnapshots(rows, Schemas, Tables, Views, Sequences, Functions, Types, operators, aggregates, EventTriggers, Publications);

        // Assert: zero arguments are written (*); the operator that was not read is left out.
        var statements = owners.Select(static owner => owner.Statement).ToArray();
        CollectionAssert.Contains(statements, "ALTER AGGREGATE app.agg (integer) OWNER TO lc_owner;");
        CollectionAssert.Contains(statements, "ALTER AGGREGATE app.agg (*) OWNER TO lc_owner;");
        Assert.AreEqual(1, statements.Count(static s => s.StartsWith("ALTER OPERATOR", StringComparison.Ordinal)));
        StringAssert.Contains(statements.Single(static s => s.StartsWith("ALTER OPERATOR", StringComparison.Ordinal)), "(integer, text)");
    }

    [TestMethod]
    public void ARoleThatNeedsQuotingIsQuotedAndTheOutputIsInAStableOrder()
    {
        // Arrange
        LiveDatabaseModelLoader.OwnerRow[] rows = [Row("table", "app", "t", "Mixed Case"), Row("schema", "app", "app", "Mixed Case")];

        // Act
        var owners = LiveDatabaseModelLoader.BuildOwnerSnapshots(rows, Schemas, Tables, Views, Sequences, Functions, Types, [], [], EventTriggers, Publications);

        // Assert
        CollectionAssert.AreEqual(
            new[] { "ALTER SCHEMA app OWNER TO \"Mixed Case\";", "ALTER TABLE app.t OWNER TO \"Mixed Case\";" },
            owners.Select(static owner => owner.Statement).ToArray());
    }

    [TestMethod]
    public void TheSnapshotIsReadBackAsOwnersWithNoDiagnostics()
    {
        // Arrange
        var owners = LiveDatabaseModelLoader.BuildOwnerSnapshots(
            [Row("schema", "app", "app"), Row("table", "app", "t", "table_owner"), Row("view", "app", "v"), Row("function", "app", "f", arguments: "a integer, b text"), Row("type", "app", "mood")],
            Schemas, Tables, Views, Sequences, Functions, Types, [], [], EventTriggers, Publications);
        var sql = LiveDatabaseModelLoader.GenerateSnapshotSql(Schemas, Tables, [], [], Views, Functions, Types, sequences: Sequences, owners: owners);

        // Act
        var model = new LiveDatabaseModelLoader(new PgQueryParser(), new SemanticBuilder()).BuildModelFromSnapshotSql(sql);

        // Assert
        Assert.AreEqual("table_owner", model.Owners["owner:table:app.t"].OwnerRole);
        Assert.AreEqual("lc_owner", model.Owners["owner:view:app.v"].OwnerRole);
        Assert.AreEqual("lc_owner", model.Owners["owner:schema:app"].OwnerRole);
        Assert.AreEqual("lc_owner", model.Owners["owner:type:app.mood"].OwnerRole);
        Assert.AreEqual(5, model.Owners.Count);
        Assert.AreEqual(0, model.Diagnostics.Count, string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
    }

    [TestMethod]
    public void WithOwnersExcludedNoOwnerIsRead()
    {
        // Arrange
        var owners = LiveDatabaseModelLoader.BuildOwnerSnapshots([Row("table", "app", "t")], Schemas, Tables, Views, Sequences, Functions, Types, [], [], EventTriggers, Publications);
        var sql = LiveDatabaseModelLoader.GenerateSnapshotSql(Schemas, Tables, [], [], owners: owners);

        // Act
        var model = new LiveDatabaseModelLoader(new PgQueryParser(), new SemanticBuilder()).BuildModelFromSnapshotSql(sql, excludeOwners: true);

        // Assert
        Assert.IsEmpty(model.Owners);
    }

    [TestMethod]
    public void TheScaffoldPutsEachOwnerBesideItsObject()
    {
        // Arrange
        var owners = LiveDatabaseModelLoader.BuildOwnerSnapshots(
            [Row("schema", "app", "app"), Row("table", "app", "t"), Row("view", "app", "v"), Row("function", "app", "f", arguments: "a integer, b text"), Row("type", "app", "mood"), Row("event trigger", null, "audit")],
            Schemas, Tables, Views, Sequences, Functions, Types, [], [], EventTriggers, Publications);

        // Act
        var files = LiveDatabaseModelLoader.BuildScaffoldFiles(Schemas, Tables, [], [], Views, Functions, Types, [], Sequences, [], [], EventTriggers, owners: owners);

        // Assert
        string Content(string path) => files.Single(f => f.RelativePath == path).Content;
        StringAssert.Contains(Content("Schema/app/Table/t.sql"), "ALTER TABLE app.t OWNER TO lc_owner;");
        StringAssert.Contains(Content("Schema/app/View/v.sql"), "ALTER VIEW app.v OWNER TO lc_owner;");
        StringAssert.Contains(Content("Schema/app/Function/f.sql"), "ALTER FUNCTION app.f(a integer, b text) OWNER TO lc_owner;");
        StringAssert.Contains(Content("Schema/app/Type/mood.sql"), "ALTER TYPE app.mood OWNER TO lc_owner;");
        StringAssert.Contains(Content("EventTrigger/audit.sql"), "ALTER EVENT TRIGGER audit OWNER TO lc_owner;");
        StringAssert.Contains(Content("Schema/app/app.sql"), "ALTER SCHEMA app OWNER TO lc_owner;");
        Assert.IsEmpty(new LiveDatabaseModelLoader(new PgQueryParser(), new SemanticBuilder()).FindUnparseableFiles(files));
    }

    [TestMethod]
    public void AnOwnerStatementComesAfterTheStatementThatCreatesItsObject()
    {
        // Arrange
        var owners = LiveDatabaseModelLoader.BuildOwnerSnapshots([Row("table", "app", "t")], Schemas, Tables, Views, Sequences, Functions, Types, [], [], EventTriggers, Publications);

        // Act
        var content = LiveDatabaseModelLoader.BuildScaffoldFiles(Schemas, Tables, [], [], Views, Functions, Types, [], Sequences, [], [], owners: owners).Single(static f => f.RelativePath == "Schema/app/Table/t.sql").Content;

        // Assert
        Assert.IsLessThan(content.IndexOf("ALTER TABLE app.t OWNER TO", StringComparison.Ordinal), content.IndexOf("CREATE TABLE", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ASequenceAColumnOwnsHasNoOwnerStatementInTheScaffoldButItsSnapshotKeepsOne()
    {
        // Arrange: PostgreSQL refuses ALTER SEQUENCE ... OWNER TO on a sequence owned by a column, so init must not write it.
        var owners = LiveDatabaseModelLoader.BuildOwnerSnapshots([Row("sequence", "app", "s"), Row("sequence", "app", "t_id_seq")], Schemas, Tables, Views, Sequences, Functions, Types, [], [], EventTriggers, Publications);

        // Act
        var files = LiveDatabaseModelLoader.BuildScaffoldFiles(Schemas, Tables, [], [], Views, Functions, Types, [], Sequences, [], [], owners: owners);
        var all = string.Join("\n", files.Select(static f => f.Content));

        // Assert
        Assert.AreEqual(2, owners.Count);
        StringAssert.Contains(all, "ALTER SEQUENCE app.s OWNER TO lc_owner;");
        Assert.IsFalse(all.Contains("ALTER SEQUENCE app.t_id_seq OWNER TO", StringComparison.Ordinal), all);
    }

    [TestMethod]
    public void AnOwnerWithNoFileOfItsObjectFallsBackToItsSchemasFile()
    {
        // Arrange: an operator is not given a path by the owner scaffolding unless one was written.
        var operators = new[] { new LiveOperatorSnapshot("app", "#+#", "integer", "integer", "pg_catalog", "int4pl", null, null, null, null, null, null, false, false) };
        var rows = new[] { new LiveDatabaseModelLoader.OwnerRow("operator", "app", "#+#", string.Empty, "integer", "integer", "lc_owner") };
        var owners = LiveDatabaseModelLoader.BuildOwnerSnapshots(rows, Schemas, Tables, Views, Sequences, Functions, Types, operators, [], EventTriggers, Publications);

        // Act
        var files = LiveDatabaseModelLoader.BuildScaffoldFiles(Schemas, Tables, [], [], Views, Functions, Types, [], Sequences, [], [], operators: operators, owners: owners);

        // Assert: beside the operator it is the owner of.
        var file = files.Single(static f => f.Content.Contains("OWNER TO", StringComparison.Ordinal));
        StringAssert.Contains(file.RelativePath, "Operator");
        StringAssert.Contains(file.Content, "CREATE OPERATOR");
    }
}
