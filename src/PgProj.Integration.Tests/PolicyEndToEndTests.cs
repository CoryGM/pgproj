using Npgsql;
using PgProj.Core;
using PgProj.Deploy;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-76 against a real server: row-level security policies and a table's <c>ENABLE</c>/<c>FORCE ROW LEVEL SECURITY</c> flags
/// must read back from <c>pg_policy</c>/<c>pg_class</c> (via <see cref="LiveDatabaseModelLoader"/>) as the same model the
/// author wrote, or every publish would drop and recreate every policy. There is no <c>pg_get_policydef</c>, so the live
/// loader assembles the statement itself from <c>pg_policy</c>'s already-rendered pieces (<see
/// cref="LiveDatabaseModelLoader.BuildCreatePolicyStatement"/>) — this is the one place that reconstruction is proven against
/// a real catalog rather than just unit-tested.
/// </summary>
[TestClass]
public sealed class PolicyEndToEndTests
{
    private readonly PgQueryParser _parser = new();
    private readonly SemanticBuilder _semanticBuilder = new();
    private readonly LiveDatabaseModelLoader _liveLoader;

    public PolicyEndToEndTests()
    {
        _liveLoader = new LiveDatabaseModelLoader(_parser, _semanticBuilder);
    }

    [TestMethod]
    public async Task PoliciesAndRowSecurityFlags_ReadBackConverged_AndEditsAreDeployed()
    {
        var schema = $"test_g76_{Guid.NewGuid():N}";
        await using var connection = await OpenConnectionAsync();
        try
        {
            string Source(string usingExpression = "owner_id = 1") => $"""
                CREATE SCHEMA {schema};
                CREATE TABLE {schema}.t (id integer, owner_id integer, status text);
                CREATE POLICY p_default ON {schema}.t USING ({usingExpression});
                CREATE POLICY p_restrictive ON {schema}.t AS RESTRICTIVE USING (status = 'active');
                CREATE POLICY p_select_only ON {schema}.t FOR SELECT TO PUBLIC USING (true);
                CREATE POLICY p_insert_check ON {schema}.t FOR INSERT WITH CHECK (status = 'active');
                ALTER TABLE {schema}.t ENABLE ROW LEVEL SECURITY;
                ALTER TABLE {schema}.t FORCE ROW LEVEL SECURITY;
                """;

            // 1. Deploy every policy and the RLS flags, read the catalog back, and require the re-diff to be empty.
            var first = Build(Source());
            await PublishAsync(connection, DatabaseModel.Empty, first);
            var live = await LoadAsync(connection, schema);
            Assert.AreEqual(4, live.Policies.Count, "Every policy must be read back: " + string.Join(", ", live.Policies.Keys));
            Assert.IsTrue(live.Tables[$"{schema}.t"].IsRowSecurityEnabled);
            Assert.IsTrue(live.Tables[$"{schema}.t"].IsRowSecurityForced);
            AssertConverged(live, first, "after the first publish");
            var again = await PublishAsync(connection, live, first);
            Assert.AreEqual(string.Empty, again.Trim(), "A second publish must have nothing to do: " + again);

            // 2. Editing one policy's USING expression must drop and recreate only that policy.
            var edited = Build(Source("owner_id = 2"));
            var editScript = await PublishAsync(connection, await LoadAsync(connection, schema), edited);

            StringAssert.Contains(editScript, "DROP POLICY \"p_default\"");
            foreach (var untouched in new[] { "p_restrictive", "p_select_only", "p_insert_check" })
            {
                Assert.IsFalse(editScript.Contains($"DROP POLICY \"{untouched}\"", StringComparison.Ordinal), $"Policy {untouched} did not change and must not be recreated:\n{editScript}");
            }

            AssertConverged(await LoadAsync(connection, schema), edited, "after the edit");

            // 3. Disabling row security is a genuine in-place ALTER, not a drop-and-recreate of anything.
            var disabled = Build($"""
                CREATE SCHEMA {schema};
                CREATE TABLE {schema}.t (id integer, owner_id integer, status text);
                CREATE POLICY p_default ON {schema}.t USING (owner_id = 2);
                CREATE POLICY p_restrictive ON {schema}.t AS RESTRICTIVE USING (status = 'active');
                CREATE POLICY p_select_only ON {schema}.t FOR SELECT TO PUBLIC USING (true);
                CREATE POLICY p_insert_check ON {schema}.t FOR INSERT WITH CHECK (status = 'active');
                """);
            var disableScript = await PublishAsync(connection, await LoadAsync(connection, schema), disabled);

            StringAssert.Contains(disableScript, "DISABLE ROW LEVEL SECURITY");
            Assert.IsFalse(disableScript.Contains("DROP POLICY", StringComparison.Ordinal), "Disabling row security must not touch any policy:\n" + disableScript);

            var settledLive = await LoadAsync(connection, schema);
            Assert.IsFalse(settledLive.Tables[$"{schema}.t"].IsRowSecurityEnabled);
            AssertConverged(settledLive, disabled, "after disabling row security");
            var settled = await PublishAsync(connection, settledLive, disabled);
            Assert.AreEqual(string.Empty, settled.Trim(), "Publishing again must have nothing to do: " + settled);
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
            Policies = InSchema(model.Policies),
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
