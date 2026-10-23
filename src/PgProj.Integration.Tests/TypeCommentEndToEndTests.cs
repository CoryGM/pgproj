using Npgsql;
using PgProj.Core;
using PgProj.Deploy;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-53 against a real server: changing only the comment of an enum or composite type used to be deployed as
/// <c>DROP TYPE</c> + <c>CREATE TYPE</c>, which PostgreSQL refuses while a column uses the type (<c>2BP01</c>). It is now a
/// <c>COMMENT ON TYPE</c>, so the type, its dependents, and their data are untouched.
/// </summary>
[TestClass]
public sealed class TypeCommentEndToEndTests
{
    private readonly PgQueryParser _parser = new();
    private readonly SemanticBuilder _semanticBuilder = new();
    private readonly LiveDatabaseModelLoader _liveLoader;

    public TypeCommentEndToEndTests()
    {
        _liveLoader = new LiveDatabaseModelLoader(_parser, _semanticBuilder);
    }

    [TestMethod]
    public async Task ChangingOnlyATypeComment_IsCommentOnTypeAndTheTypesSurviveWhileInUse()
    {
        var schema = $"test_g53_{Guid.NewGuid():N}";
        await using var connection = await OpenConnectionAsync();
        try
        {
            string Source(string? mood, string? money, string? span) => $"""
                CREATE SCHEMA {schema};
                CREATE TYPE {schema}.mood AS ENUM ('sad', 'ok');
                CREATE TYPE {schema}.money_amount AS (amount numeric, currency text);
                CREATE TYPE {schema}.size AS ENUM ('small', 'large');
                CREATE TABLE {schema}.t (id integer PRIMARY KEY, m {schema}.mood, c {schema}.money_amount, s {schema}.size);
                {Comment("mood", mood)}
                {Comment("money_amount", money)}
                {Comment("size", span)}
                """;

            string Comment(string type, string? text)
                => text is null ? string.Empty : $"COMMENT ON TYPE {schema}.{type} IS '{text.Replace("'", "''", StringComparison.Ordinal)}';";

            async Task<string?> TypeComment(string type)
            {
                await using var command = new NpgsqlCommand($"SELECT obj_description('{schema}.{type}'::regtype, 'pg_type')", connection);
                var value = await command.ExecuteScalarAsync();
                return value is DBNull or null ? null : Convert.ToString(value);
            }

            async Task<long> TypeOid(string type)
            {
                await using var command = new NpgsqlCommand($"SELECT '{schema}.{type}'::regtype::oid::bigint", connection);
                return Convert.ToInt64(await command.ExecuteScalarAsync());
            }

            var first = Build(Source("first", "first", null));
            await PublishAsync(connection, DatabaseModel.Empty, first);
            await ExecuteAsync(connection, $"INSERT INTO {schema}.t (id, m) VALUES (1, 'ok');");
            var oids = new[] { await TypeOid("mood"), await TypeOid("money_amount"), await TypeOid("size") };
            await AssertConverged(connection, schema, first, "after the first publish");

            // Change one comment, add one, and remove one, while a column uses every type.
            var second = Build(Source("it's second", "first", "added"));
            var secondScript = await PublishAsync(connection, await LoadAsync(connection, schema), second);
            Assert.IsFalse(secondScript.Contains("DROP TYPE", StringComparison.Ordinal), secondScript);
            Assert.IsFalse(secondScript.Contains("CREATE TYPE", StringComparison.Ordinal), secondScript);
            Assert.AreEqual("it's second", await TypeComment("mood"));
            Assert.AreEqual("first", await TypeComment("money_amount"));
            Assert.AreEqual("added", await TypeComment("size"));
            await AssertConverged(connection, schema, second, "after changing and adding comments");

            var third = Build(Source("it's second", null, "added"));
            var thirdScript = await PublishAsync(connection, await LoadAsync(connection, schema), third);
            Assert.IsFalse(thirdScript.Contains("DROP TYPE", StringComparison.Ordinal), thirdScript);
            Assert.IsNull(await TypeComment("money_amount"));
            await AssertConverged(connection, schema, third, "after removing a comment");

            // The types were never recreated and the row that uses one is intact.
            CollectionAssert.AreEqual(oids, new[] { await TypeOid("mood"), await TypeOid("money_amount"), await TypeOid("size") });
            await using var count = new NpgsqlCommand($"SELECT count(*) FROM {schema}.t WHERE m = 'ok'", connection);
            Assert.AreEqual(1L, await count.ExecuteScalarAsync());

            // A second publish of the same project has nothing to do.
            var again = await PublishAsync(connection, await LoadAsync(connection, schema), third);
            Assert.IsFalse(again.Contains("COMMENT ON", StringComparison.Ordinal), again);
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {schema} CASCADE;");
        }
    }

    private async Task AssertConverged(NpgsqlConnection connection, string schema, DatabaseModel target, string step)
    {
        var live = await LoadAsync(connection, schema);
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
