using Npgsql;
using PgProj.Core;
using PgProj.Deploy;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-40 against a real server. The diff used to ignore a trigger's <c>WHEN</c> condition, <c>UPDATE OF</c> columns, arguments, and
/// constraint-trigger options, so a change to any of them was never deployed. Now that they are compared, the same trigger read back
/// from the catalog (which <c>pg_get_triggerdef</c> deparses: parenthesized, <c>LIKE</c> as <c>~~</c>, <c>IN</c> as
/// <c>= ANY (ARRAY[...])</c>, <c>::text</c> added) must compare equal to what the author wrote, or every publish would drop and recreate it.
/// </summary>
[TestClass]
public sealed class TriggerOptionsEndToEndTests
{
    // Each trigger is written the way an author would; the server rewrites most of them when it stores them. {S} is the schema.
    private static readonly string[] Triggers =
    [
        "CREATE TRIGGER w_distinct AFTER UPDATE ON {S}.t FOR EACH ROW WHEN (old.a IS DISTINCT FROM new.a) EXECUTE FUNCTION {S}.f()",
        "CREATE TRIGGER w_simple AFTER UPDATE ON {S}.t FOR EACH ROW WHEN (new.a > 0) EXECUTE FUNCTION {S}.f()",
        "CREATE TRIGGER w_noteq AFTER UPDATE ON {S}.t FOR EACH ROW WHEN (new.a != 0) EXECUTE FUNCTION {S}.f()",
        "CREATE TRIGGER w_and_or AFTER UPDATE ON {S}.t FOR EACH ROW WHEN (NEW.A>0 AND (new.b<10 OR new.b IS NULL)) EXECUTE FUNCTION {S}.f()",
        "CREATE TRIGGER w_and3 AFTER UPDATE ON {S}.t FOR EACH ROW WHEN (new.a > 0 AND new.b > 0 AND new.a < 9) EXECUTE FUNCTION {S}.f()",
        "CREATE TRIGGER w_not AFTER UPDATE ON {S}.t FOR EACH ROW WHEN (NOT (new.a = 1)) EXECUTE FUNCTION {S}.f()",
        "CREATE TRIGGER w_between AFTER UPDATE ON {S}.t FOR EACH ROW WHEN (new.a BETWEEN 1 AND 5) EXECUTE FUNCTION {S}.f()",
        "CREATE TRIGGER w_notbetween AFTER UPDATE ON {S}.t FOR EACH ROW WHEN (new.a NOT BETWEEN 1 AND 5) EXECUTE FUNCTION {S}.f()",
        "CREATE TRIGGER w_in AFTER UPDATE ON {S}.t FOR EACH ROW WHEN (new.a IN (1, 2, 3)) EXECUTE FUNCTION {S}.f()",
        "CREATE TRIGGER w_notin AFTER UPDATE ON {S}.t FOR EACH ROW WHEN (new.a NOT IN (1, 2, 3)) EXECUTE FUNCTION {S}.f()",
        "CREATE TRIGGER w_any AFTER UPDATE ON {S}.t FOR EACH ROW WHEN (new.b = ANY (ARRAY[1, 2])) EXECUTE FUNCTION {S}.f()",
        "CREATE TRIGGER w_coalesce AFTER UPDATE ON {S}.t FOR EACH ROW WHEN (COALESCE(new.a, 0) > 0) EXECUTE FUNCTION {S}.f()",
        "CREATE TRIGGER w_wholerow AFTER UPDATE ON {S}.t FOR EACH ROW WHEN (old.* IS DISTINCT FROM new.*) EXECUTE FUNCTION {S}.f()",
        "CREATE TRIGGER w_lower AFTER UPDATE ON {S}.t FOR EACH ROW WHEN (lower(new.n) = 'x') EXECUTE FUNCTION {S}.f()",
        "CREATE TRIGGER w_qualified AFTER UPDATE ON {S}.t FOR EACH ROW WHEN (pg_catalog.lower(new.n) = 'x') EXECUTE FUNCTION {S}.f()",
        "CREATE TRIGGER w_like AFTER UPDATE ON {S}.t FOR EACH ROW WHEN (new.n LIKE 'a%') EXECUTE FUNCTION {S}.f()",
        "CREATE TRIGGER w_notlike AFTER UPDATE ON {S}.t FOR EACH ROW WHEN (new.n NOT LIKE 'a%') EXECUTE FUNCTION {S}.f()",
        "CREATE TRIGGER w_ilike AFTER UPDATE ON {S}.t FOR EACH ROW WHEN (new.n ILIKE 'a%') EXECUTE FUNCTION {S}.f()",
        "CREATE TRIGGER w_quote AFTER UPDATE ON {S}.t FOR EACH ROW WHEN (new.n = 'it''s') EXECUTE FUNCTION {S}.f()",
        "CREATE TRIGGER w_arith AFTER UPDATE ON {S}.t FOR EACH ROW WHEN (new.a + 1 > 2 * new.b) EXECUTE FUNCTION {S}.f()",
        "CREATE TRIGGER w_varchar AFTER UPDATE ON {S}.t FOR EACH ROW WHEN (new.v = 'x') EXECUTE FUNCTION {S}.f()",
        "CREATE TRIGGER w_varchar_cast AFTER UPDATE ON {S}.t FOR EACH ROW WHEN (new.v::text = 'x') EXECUTE FUNCTION {S}.f()",
        "CREATE TRIGGER w_nulltest AFTER UPDATE ON {S}.t FOR EACH ROW WHEN (new.a IS NOT NULL AND new.b IS NULL) EXECUTE FUNCTION {S}.f()",
        "CREATE TRIGGER w_negative AFTER UPDATE ON {S}.t FOR EACH ROW WHEN (new.a > -1) EXECUTE FUNCTION {S}.f()",
        "CREATE TRIGGER w_numeric AFTER UPDATE ON {S}.t FOR EACH ROW WHEN (new.x > 0) EXECUTE FUNCTION {S}.f()",
        "CREATE TRIGGER w_numeric_neg AFTER UPDATE ON {S}.t FOR EACH ROW WHEN (new.x > -1) EXECUTE FUNCTION {S}.f()",
        "CREATE TRIGGER w_varchar_in AFTER UPDATE ON {S}.t FOR EACH ROW WHEN (new.v IN ('a', 'b')) EXECUTE FUNCTION {S}.f()",
        "CREATE TRIGGER w_varchar_notin AFTER UPDATE ON {S}.t FOR EACH ROW WHEN (new.v NOT IN ('a', 'b')) EXECUTE FUNCTION {S}.f()",
        "CREATE TRIGGER w_varchar_const AFTER UPDATE ON {S}.t FOR EACH ROW WHEN (new.n = 'x'::varchar) EXECUTE FUNCTION {S}.f()",
        "CREATE TRIGGER w_ins_upd AFTER INSERT OR UPDATE ON {S}.t FOR EACH ROW WHEN (new.a > 0) EXECUTE FUNCTION {S}.f()",
        "CREATE TRIGGER u_one AFTER UPDATE OF a ON {S}.t FOR EACH ROW EXECUTE FUNCTION {S}.f()",
        "CREATE TRIGGER u_many AFTER UPDATE OF b, a ON {S}.t FOR EACH ROW EXECUTE FUNCTION {S}.f()",
        "CREATE TRIGGER u_events AFTER INSERT OR UPDATE OF b, a OR DELETE ON {S}.t FOR EACH ROW EXECUTE FUNCTION {S}.f()",
        "CREATE TRIGGER u_when AFTER UPDATE OF a ON {S}.t FOR EACH ROW WHEN (old.a IS DISTINCT FROM new.a) EXECUTE FUNCTION {S}.f()",
        "CREATE TRIGGER args AFTER UPDATE ON {S}.t FOR EACH ROW EXECUTE FUNCTION {S}.f('x', 2, 'it''s')",
        "CREATE CONSTRAINT TRIGGER c_deferred AFTER UPDATE ON {S}.t DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION {S}.f()",
        "CREATE CONSTRAINT TRIGGER c_immediate AFTER UPDATE ON {S}.t DEFERRABLE INITIALLY IMMEDIATE FOR EACH ROW EXECUTE FUNCTION {S}.f()",
        "CREATE CONSTRAINT TRIGGER c_plain AFTER UPDATE ON {S}.t FOR EACH ROW EXECUTE FUNCTION {S}.f()",
        "CREATE CONSTRAINT TRIGGER c_from AFTER UPDATE ON {S}.t FROM {S}.t NOT DEFERRABLE FOR EACH ROW EXECUTE FUNCTION {S}.f()",
        "CREATE TRIGGER r_both AFTER UPDATE ON {S}.t REFERENCING OLD TABLE AS ot NEW TABLE AS nt FOR EACH STATEMENT EXECUTE FUNCTION {S}.f()",
        "CREATE TRIGGER r_new AFTER INSERT ON {S}.t REFERENCING NEW TABLE AS nt FOR EACH STATEMENT EXECUTE FUNCTION {S}.f()",
        "CREATE TRIGGER e_truncate BEFORE TRUNCATE ON {S}.t FOR EACH STATEMENT EXECUTE FUNCTION {S}.f()",
    ];

    private readonly PgQueryParser _parser = new();
    private readonly SemanticBuilder _semanticBuilder = new();
    private readonly LiveDatabaseModelLoader _liveLoader;

    public TriggerOptionsEndToEndTests()
    {
        _liveLoader = new LiveDatabaseModelLoader(_parser, _semanticBuilder);
    }

    [TestMethod]
    public async Task TriggerOptions_ReadBackAsTheSameTriggers_AndEditsAreDeployed()
    {
        var schema = $"test_g40_{Guid.NewGuid():N}";
        await using var connection = await OpenConnectionAsync();
        try
        {
            string Source(Func<string, string>? edit = null) => $"""
                CREATE SCHEMA {schema};
                CREATE TABLE {schema}.t (a integer, b integer, x numeric, n text, v varchar(20));
                CREATE FUNCTION {schema}.f() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NULL; END $$;
                {string.Join(";\n", Triggers.Select(trigger => (edit ?? (static s => s))(trigger.Replace("{S}", schema, StringComparison.Ordinal))))};
                """;

            async Task<string> Definition(string trigger)
            {
                await using var command = new NpgsqlCommand($"SELECT pg_get_triggerdef(oid) FROM pg_trigger WHERE tgname = '{trigger}' AND tgrelid = '{schema}.t'::regclass", connection);
                return Convert.ToString(await command.ExecuteScalarAsync()) ?? string.Empty;
            }

            // 1. Deploy every trigger, read the catalog back, and require the re-diff to be empty: nothing the author wrote may read
            // as a change once the server has stored and deparsed it.
            var first = Build(Source());
            await PublishAsync(connection, DatabaseModel.Empty, first);
            var live = await LoadAsync(connection, schema);
            Assert.AreEqual(Triggers.Length, live.Triggers.Count, "Every trigger must be read back: " + string.Join(", ", live.Triggers.Keys));
            AssertConverged(live, first, "after the first publish");
            var again = await PublishAsync(connection, live, first);
            Assert.AreEqual(string.Empty, again.Trim(), "A second publish must have nothing to do: " + again);

            // 2. Edit each kind of option. Each edit used to be invisible, so nothing was published.
            var edited = Build(Source(static trigger => trigger
                .Replace("WHEN (new.a > 0) EXECUTE", "WHEN (new.a > 100) EXECUTE", StringComparison.Ordinal)
                .Replace("UPDATE OF a ON", "UPDATE OF a, b ON", StringComparison.Ordinal)
                .Replace("f('x', 2, 'it''s')", "f('y', 2, 'it''s')", StringComparison.Ordinal)
                .Replace("DEFERRABLE INITIALLY DEFERRED", "DEFERRABLE INITIALLY IMMEDIATE", StringComparison.Ordinal)
                .Replace("REFERENCING NEW TABLE AS nt", "REFERENCING NEW TABLE AS renamed", StringComparison.Ordinal)));
            var editScript = await PublishAsync(connection, await LoadAsync(connection, schema), edited);

            StringAssert.Contains(await Definition("w_simple"), "new.a > 100");
            StringAssert.Contains(await Definition("w_ins_upd"), "new.a > 100");
            StringAssert.Contains(await Definition("u_one"), "UPDATE OF a, b");
            StringAssert.Contains(await Definition("args"), "'y'");
            StringAssert.Contains(await Definition("c_deferred"), "INITIALLY IMMEDIATE");
            StringAssert.Contains(await Definition("r_new"), "NEW TABLE AS renamed");
            foreach (var untouched in new[] { "w_between", "w_like", "u_many", "c_plain", "r_both", "e_truncate" })
            {
                Assert.IsFalse(editScript.Contains($"DROP TRIGGER \"{untouched}\"", StringComparison.Ordinal), $"Trigger {untouched} did not change and must not be recreated:\n{editScript}");
            }

            AssertConverged(await LoadAsync(connection, schema), edited, "after the edits");
            var settled = await PublishAsync(connection, await LoadAsync(connection, schema), edited);
            Assert.AreEqual(string.Empty, settled.Trim(), "Publishing the edited project again must have nothing to do: " + settled);
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {schema} CASCADE;");
        }
    }

    private static void AssertConverged(DatabaseModel live, DatabaseModel target, string step)
    {
        var reDiff = new PgProj.Diff.DiffEngine().Diff(live, target);
        var described = "Added: " + string.Join(", ", reDiff.Added.Select(static c => c.NodeId)) + " Removed: " + string.Join(", ", reDiff.Removed.Select(static c => c.NodeId))
            + " Modified: " + string.Join("; ", reDiff.Modified.Select(static c => c.NodeId + " [" + string.Join(", ", c.Details.Select(static d => d.PropertyName + ": " + d.OldValue + " -> " + d.NewValue)) + "]"));
        Assert.AreEqual(0, reDiff.Added.Count + reDiff.Removed.Count + reDiff.Modified.Count, step + ": " + described);
    }

    private DatabaseModel Build(string sql) => _semanticBuilder.Build([_parser.Parse(sql)]);

    private async Task<DatabaseModel> LoadAsync(NpgsqlConnection connection, string schema)
    {
        var model = await _liveLoader.LoadAsync(connection, CancellationToken.None);
        var prefix = schema + ".";

        Dictionary<string, T> InSchema<T>(IReadOnlyDictionary<string, T> source) where T : class
            => source.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal)).ToDictionary(pair => pair.Key, pair => pair.Value);

        return model with
        {
            Tables = InSchema(model.Tables),
            Views = InSchema(model.Views),
            Functions = InSchema(model.Functions),
            Triggers = InSchema(model.Triggers),
            Procedures = InSchema(model.Procedures),
            Indexes = InSchema(model.Indexes),
            Constraints = InSchema(model.Constraints),
            Sequences = InSchema(model.Sequences),
            Types = InSchema(model.Types),
            Grants = model.Grants
                .Where(pair => string.Equals(pair.Value.SchemaName, schema, StringComparison.Ordinal))
                .ToDictionary(pair => pair.Key, pair => pair.Value),
        };
    }

    private static async Task<string> PublishAsync(NpgsqlConnection connection, DatabaseModel live, DatabaseModel target)
    {
        var scriptGenerator = new ScriptGenerator();
        var batch = scriptGenerator.Generate(
            new PgProj.Diff.DiffEngine().Diff(live, target),
            new PgProj.Dependencies.DependencyAnalyzer().Analyze(live, target));
        var script = scriptGenerator.GenerateSqlScript(batch);
        if (batch.Operations.Count > 0)
        {
            await using var transaction = await connection.BeginTransactionAsync();
            foreach (var statement in SqlStatementSplitter.Split(script))
            {
                await using var command = new NpgsqlCommand(statement, connection, transaction);
                await command.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }

        return script;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        foreach (var statement in SqlStatementSplitter.Split(sql))
        {
            await using var command = new NpgsqlCommand(statement, connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        var connection = new NpgsqlConnection(PostgresContainerFixture.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }
}
