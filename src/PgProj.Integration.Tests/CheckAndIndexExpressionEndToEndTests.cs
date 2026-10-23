using Npgsql;
using PgProj.Core;
using PgProj.Deploy;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-72 and G-73 against a real server. A CHECK constraint, an index predicate, and an index key expression used to be re-rendered by a
/// renderer that knew a few node kinds and dropped the rest, so <c>a BETWEEN 1 AND 9</c> was deployed as <c>a BETWEEN 1, 9</c> or not at
/// all, and any compound CHECK failed the build. The oracle is the server itself: the same statements are run natively in one schema and
/// deployed by pgproj in another, and what PostgreSQL stores (<c>pg_get_constraintdef</c>, <c>pg_get_indexdef</c>) must be identical.
/// </summary>
[TestClass]
public sealed class CheckAndIndexExpressionEndToEndTests
{
    private readonly PgQueryParser _parser = new();
    private readonly SemanticBuilder _semanticBuilder = new();
    private readonly LiveDatabaseModelLoader _liveLoader;

    public CheckAndIndexExpressionEndToEndTests()
    {
        _liveLoader = new LiveDatabaseModelLoader(_parser, _semanticBuilder);
    }

    // Each condition is written the way an author would. The good row (a=5, b=3, n='hi', v='vv', arr={1,2}, x=1.5) satisfies all of them.
    private static readonly string[] Checks =
    [
        "a > 0",
        "a > 0 AND a < 9",
        "a > 100 OR b > 0",
        "a BETWEEN 1 AND 9",
        "a NOT BETWEEN 10 AND 20",
        "a IN (1, 5, 9)",
        "a NOT IN (7, 8)",
        "a = ANY (ARRAY[1, 5])",
        "NOT (a < 0)",
        "CASE WHEN a > 3 THEN b > 0 ELSE true END",
        "(a > 0) IS TRUE",
        "(a < 0) IS NOT TRUE",
        "coalesce(a, 0) > 0",
        "arr[1] > 0",
        "a IS DISTINCT FROM b",
        "n COLLATE \"C\" > 'a'",
        "n ~ '^h' AND length(n) < 5",
        "n LIKE 'h%' OR n IS NULL",
        "n ILIKE 'H%'",
        "a IS NOT NULL",
        "a % 2 = 1",
        "a > 0 AND (b IS NULL OR b > a - 10)",
        "a::bigint > 0",
        "abs(a - b) BETWEEN 1 AND 9",
        "-a < 5",
        "v = 'vv'",
        "v IN ('vv', 'zz')",
        "lower(v) = 'vv'",
        "x > 0",
        "a + b * 2 > 3 AND (a + b) * 2 > 3",
        "a > 0 AND b > 0 AND x > 0",
        "a > 0 OR b > 0 OR x > 0",
        "position('i' IN n) > 0",
        "a = 5 OR a = 6",
        "(n || 'x') <> ''",
        "array_length(arr, 1) = 2",
        "'a''b)' <> n",
    ];

    private static readonly string[] Indexes =
    [
        "CREATE INDEX i0 ON {S}.t0 (a) WHERE a > 0 AND b IS NOT NULL",
        "CREATE INDEX i1 ON {S}.t0 (a) WHERE a IN (1, 2)",
        "CREATE INDEX i2 ON {S}.t0 (a) WHERE a BETWEEN 1 AND 9",
        "CREATE INDEX i3 ON {S}.t0 (a) WHERE n LIKE 'x%' OR a IS NULL",
        "CREATE INDEX i4 ON {S}.t0 (lower(n))",
        "CREATE INDEX i5 ON {S}.t0 ((a + b) DESC, a)",
        "CREATE INDEX i6 ON {S}.t0 ((arr[1]))",
        "CREATE INDEX i7 ON {S}.t0 ((CASE WHEN a > 0 THEN 1 ELSE 2 END))",
        "CREATE INDEX i8 ON {S}.t0 ((a BETWEEN 1 AND 2))",
        "CREATE INDEX i9 ON {S}.t0 (lower(n), (a * 2)) WHERE b > 0 AND (n IS NOT NULL OR a > 1)",
        "CREATE UNIQUE INDEX i10 ON {S}.t0 (a, (b + 1)) WHERE x IS NOT NULL",
        "CREATE INDEX i11 ON {S}.t0 ((n::varchar(5)))",
        "CREATE INDEX i13 ON {S}.t0 (upper(n) text_pattern_ops)",
        "CREATE INDEX i14 ON {S}.t0 (n varchar_pattern_ops, (a * 2))",
        "CREATE INDEX i15 ON {S}.t0 (n COLLATE \"C\" text_pattern_ops DESC NULLS LAST, a)",
        "CREATE INDEX i12 ON {S}.t0 (a) WHERE NOT (b < 0)",
    ];

    [TestMethod]
    public async Task CheckConstraints_AreStoredByTheServerAsIfWrittenNatively_AndConverge()
    {
        var pgproj = $"test_g73a_{Guid.NewGuid():N}";
        var native = $"test_g73b_{Guid.NewGuid():N}";
        await using var connection = await OpenConnectionAsync();
        try
        {
            string Source(string schema) => $"""
                CREATE SCHEMA {schema};
                {string.Join("\n", Checks.Select((check, index) => $"CREATE TABLE {schema}.t{index} (a integer, b integer, n text, v varchar(20), arr integer[], x numeric(10,4), CONSTRAINT c CHECK ({check}));"))}
                """;

            var target = Build(Source(pgproj));
            Assert.IsFalse(target.Diagnostics.Any(static d => d.Severity == SemanticDiagnosticSeverity.Error), string.Join("; ", target.Diagnostics.Select(static d => d.Message)));
            await PublishAsync(connection, DatabaseModel.Empty, target);
            await ExecuteAsync(connection, Source(native));

            var deployed = await ConstraintDefinitionsAsync(connection, pgproj);
            var written = await ConstraintDefinitionsAsync(connection, native);
            for (var i = 0; i < Checks.Length; i++)
            {
                Assert.AreEqual(written[$"t{i}"], deployed[$"t{i}"], $"CHECK ({Checks[i]}) is stored differently when deployed by pgproj");
            }

            // The good row satisfies every condition, so a check that was deployed as something else is caught by the comparison above.
            for (var i = 0; i < Checks.Length; i++)
            {
                await ExecuteAsync(connection, $"INSERT INTO {pgproj}.t{i} (a, b, n, v, arr, x) VALUES (5, 3, 'hi', 'vv', '{{1,2}}', 1.5);");
            }

            var live = await LoadAsync(connection, pgproj);
            AssertConverged(live, target, "after the first publish");
            var again = await PublishAsync(connection, live, target);
            Assert.AreEqual(string.Empty, again.Trim(), "A second publish must have nothing to do: " + again);
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {pgproj} CASCADE; DROP SCHEMA IF EXISTS {native} CASCADE;");
        }
    }

    [TestMethod]
    public async Task IndexPredicatesAndExpressions_AreStoredByTheServerAsIfWrittenNatively_AndConverge()
    {
        var pgproj = $"test_g73c_{Guid.NewGuid():N}";
        var native = $"test_g73d_{Guid.NewGuid():N}";
        await using var connection = await OpenConnectionAsync();
        try
        {
            string Source(string schema) => $"""
                CREATE SCHEMA {schema};
                CREATE TABLE {schema}.t0 (a integer, b integer, n text, arr integer[], x numeric(10,4));
                {string.Join(";\n", Indexes.Select(index => index.Replace("{S}", schema, StringComparison.Ordinal)))};
                """;

            var target = Build(Source(pgproj));
            Assert.IsFalse(target.Diagnostics.Any(static d => d.Severity == SemanticDiagnosticSeverity.Error), string.Join("; ", target.Diagnostics.Select(static d => d.Message)));
            await PublishAsync(connection, DatabaseModel.Empty, target);
            await ExecuteAsync(connection, Source(native));

            var deployed = await IndexDefinitionsAsync(connection, pgproj);
            var written = await IndexDefinitionsAsync(connection, native);
            for (var i = 0; i < Indexes.Length; i++)
            {
                Assert.AreEqual(written[$"i{i}"], deployed[$"i{i}"], $"{Indexes[i]} is stored differently when deployed by pgproj");
            }

            var live = await LoadAsync(connection, pgproj);
            AssertConverged(live, target, "after the first publish");
            var again = await PublishAsync(connection, live, target);
            Assert.AreEqual(string.Empty, again.Trim(), "A second publish must have nothing to do: " + again);
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {pgproj} CASCADE; DROP SCHEMA IF EXISTS {native} CASCADE;");
        }
    }

    [TestMethod]
    public async Task AChangedCheckIsDeployed_AndTheServerHoldsTheNewCondition()
    {
        var schema = $"test_g73e_{Guid.NewGuid():N}";
        await using var connection = await OpenConnectionAsync();
        try
        {
            string Source(string check) => $"""
                CREATE SCHEMA {schema};
                CREATE TABLE {schema}.t (a integer, CONSTRAINT c CHECK ({check}));
                """;

            var first = Build(Source("a BETWEEN 1 AND 9"));
            await PublishAsync(connection, DatabaseModel.Empty, first);

            var second = Build(Source("a BETWEEN 1 AND 20"));
            var script = await PublishAsync(connection, await LoadAsync(connection, schema), second);
            StringAssert.Contains(script, "BETWEEN 1 AND 20", script);

            await ExecuteAsync(connection, $"INSERT INTO {schema}.t (a) VALUES (15);");
            var exception = await Assert.ThrowsExactlyAsync<PostgresException>(() => ExecuteAsync(connection, $"INSERT INTO {schema}.t (a) VALUES (21);"));
            Assert.AreEqual("23514", exception.SqlState);

            AssertConverged(await LoadAsync(connection, schema), second, "after the change");
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {schema} CASCADE;");
        }
    }

    private static async Task<Dictionary<string, string>> ConstraintDefinitionsAsync(NpgsqlConnection connection, string schema)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var command = new NpgsqlCommand(
            "SELECT c.relname, pg_catalog.pg_get_constraintdef(con.oid) FROM pg_catalog.pg_constraint con JOIN pg_catalog.pg_class c ON c.oid = con.conrelid JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = @schema AND con.contype = 'c'",
            connection);
        command.Parameters.AddWithValue("schema", schema);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result[reader.GetString(0)] = reader.GetString(1);
        }

        return result;
    }

    private static async Task<Dictionary<string, string>> IndexDefinitionsAsync(NpgsqlConnection connection, string schema)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var command = new NpgsqlCommand(
            "SELECT indexname, indexdef FROM pg_catalog.pg_indexes WHERE schemaname = @schema",
            connection);
        command.Parameters.AddWithValue("schema", schema);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result[reader.GetString(0)] = reader.GetString(1).Replace(schema, "S", StringComparison.Ordinal);
        }

        return result;
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
            => source.Where(pair => pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

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
                .Where(pair => string.Equals(pair.Value.SchemaName, schema, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
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
                try
                {
                    await command.ExecuteNonQueryAsync();
                }
                catch (PostgresException exception)
                {
                    throw new InvalidOperationException(exception.SqlState + ": " + exception.MessageText + "\nStatement: " + statement + "\nScript:\n" + script, exception);
                }
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
