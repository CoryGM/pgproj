using Npgsql;
using PgProj.Core;
using PgProj.Deploy;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-71 against a real server: a generated column (GENERATED ALWAYS AS (expr) STORED) used to reach the model as an ordinary column, so
/// it was deployed without its expression and its values were never computed.
/// </summary>
[TestClass]
public sealed class GeneratedColumnEndToEndTests
{
    private readonly PgQueryParser _parser = new();
    private readonly SemanticBuilder _semanticBuilder = new();
    private readonly LiveDatabaseModelLoader _liveLoader;

    public GeneratedColumnEndToEndTests()
    {
        _liveLoader = new LiveDatabaseModelLoader(_parser, _semanticBuilder);
    }


    // Each generated column is written the way an author would; the server stores and prints most of them differently. {S} is the schema.
    private static readonly (string Name, string Type, string Expression, string Expected)[] Columns =
    [
        ("g_mul", "integer", "a * 2", "10"),
        ("g_sum", "integer", "a + b", "8"),
        ("g_paren", "integer", "(a + b) * 2", "16"),
        ("g_mixed", "integer", "a * 2 + b", "13"),
        ("g_neg", "integer", "-a", "-5"),
        ("g_abs", "integer", "abs(b - a)", "2"),
        ("g_coalesce", "integer", "coalesce(a, 0) + 1", "6"),
        ("g_concat", "text", "n || 'x'", "hix"),
        ("g_lower", "text", "lower(n)", "hi"),
        ("g_upper_v", "text", "upper(v)", "VV"),
        ("g_concat_v", "text", "v || 'x'", "vvx"),
        ("g_length", "integer", "length(n)", "2"),
        ("g_cast", "text", "a::text", "5"),
        ("g_cast_paren", "text", "(a * 2)::text", "10"),
        ("g_bigint", "bigint", "a::bigint * 2", "10"),
        ("g_bool", "boolean", "a > 0", "true"),
        ("g_between", "boolean", "a between 1 and 5", "true"),
        ("g_in", "boolean", "a in (1, 2, 5)", "true"),
        ("g_like", "boolean", "n like 'h%'", "true"),
        ("g_case", "text", "case when a > 3 then 'big' else 'small' end", "big"),
        ("g_round", "numeric", "round(x, 2)", "1.23"),
        ("g_scale", "numeric", "x * 2", "2.4690"),
        ("g_qualified", "text", "pg_catalog.lower(n)", "hi"),
        ("g_nullif", "integer", "nullif(a, 0)", "5"),
        ("g_string", "text", "'const'", "const"),
        ("g_div", "integer", "a / 2", "2"),
        ("g_numcmp", "boolean", "x > 0", "true"),
        ("g_negative", "boolean", "a > -1", "true"),
        ("g_varcmp", "boolean", "v = 'vv'", "true"),
        ("g_varin", "boolean", "v in ('vv', 'zz')", "true"),
        ("g_datepart", "integer", "extract(year from ts)::integer", "2020"),
        ("g_dateadd", "date", "d + 1", "2020-01-02"),
        ("g_numconst", "numeric", "x + 1", "2.2345"),
        ("g_trim", "text", "trim(both ' ' from n)", "hi"),
        ("g_substr", "text", "substring(n from 1 for 1)", "h"),
        ("g_position", "integer", "position('i' in n)", "2"),
        ("g_array", "text", "(array[n, 'z'])[1]", "hi"),
        ("g_json", "text", "('{\"k\": 1}'::jsonb ->> 'k')", "1"),
    ];

    [TestMethod]
    public async Task GeneratedColumns_AreDeployedComputedAndReadBackAsTheSameColumns()
    {
        var schema = $"test_g71_{Guid.NewGuid():N}";
        await using var connection = await OpenConnectionAsync();
        try
        {
            string Source(Func<string, string>? edit = null) => $"""
                CREATE SCHEMA {schema};
                CREATE TABLE {schema}.t (
                    a integer,
                    b integer,
                    n text,
                    v varchar(20),
                    x numeric(10,4),
                    ts timestamp,
                    d date,
                    {string.Join(",\n    ", Columns.Select(column => $"\"{column.Name}\" {column.Type} GENERATED ALWAYS AS ({(edit ?? (static e => e))(column.Expression)}) STORED"))});
                """;

            async Task<string> Scalar(string column)
            {
                await using var command = new NpgsqlCommand($"SELECT \"{column}\"::text FROM {schema}.t WHERE a = 5", connection);
                return Convert.ToString(await command.ExecuteScalarAsync()) ?? "<null>";
            }

            var target = Build(Source());
            Assert.IsFalse(target.Diagnostics.Any(static d => d.Severity == SemanticDiagnosticSeverity.Error), string.Join("; ", target.Diagnostics.Select(static d => d.Message)));
            var script = await PublishAsync(connection, DatabaseModel.Empty, target);
            Assert.AreEqual(Columns.Length, target.Tables.Values.Single().Columns.Count(static c => c.IsGenerated), "Every generated column must be captured.");

            // 1. The values are computed by the server: the deployed column really is generated.
            await ExecuteAsync(connection, $"INSERT INTO {schema}.t (a, b, n, v, x, ts, d) VALUES (5, 3, 'hi', 'vv', 1.2345, '2020-01-01', '2020-01-01');");
            foreach (var column in Columns)
            {
                Assert.AreEqual(column.Expected, await Scalar(column.Name), $"{column.Name} = {column.Expression}\n{script}");
            }

            // 2. Read back from the catalog it is the same project, and a second publish has nothing to do.
            var live = await LoadAsync(connection, schema);
            AssertConverged(live, target, "after the first publish");
            var again = await PublishAsync(connection, live, target);
            Assert.AreEqual(string.Empty, again.Trim(), "A second publish must have nothing to do: " + again);
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {schema} CASCADE;");
        }
    }

    [TestMethod]
    public async Task ChangesToGeneratedColumns_AreDeployedAndConverge()
    {
        var schema = $"test_g71c_{Guid.NewGuid():N}";
        await using var connection = await OpenConnectionAsync();
        try
        {
            string Source(string expression, bool keepGenerated, bool extra, bool ordinaryBecomesGenerated = false) => $"""
                CREATE SCHEMA {schema};
                CREATE TABLE {schema}.t (
                    a integer,
                    g_expr integer GENERATED ALWAYS AS ({expression}) STORED NOT NULL,
                    g_keep integer{(keepGenerated ? " GENERATED ALWAYS AS (a + 1) STORED" : string.Empty)},
                    o integer{(ordinaryBecomesGenerated ? " GENERATED ALWAYS AS (a - 1) STORED" : string.Empty)});
                COMMENT ON COLUMN {schema}.t.g_expr IS 'doubled';
                {(extra ? $"ALTER TABLE {schema}.t ADD COLUMN g_new text GENERATED ALWAYS AS (a::text || '!') STORED;" : string.Empty)}
                """;

            async Task<string> Scalar(string sql)
            {
                await using var command = new NpgsqlCommand(sql, connection);
                return Convert.ToString(await command.ExecuteScalarAsync()) ?? "<null>";
            }

            string Generated(string column) => $"SELECT attgenerated::text FROM pg_attribute WHERE attrelid = '{schema}.t'::regclass AND attname = '{column}'";

            var first = Build(Source("a * 2", keepGenerated: true, extra: false));
            await PublishAsync(connection, DatabaseModel.Empty, first);
            await ExecuteAsync(connection, $"INSERT INTO {schema}.t (a, o) VALUES (5, 100);");
            Assert.AreEqual("10", await Scalar($"SELECT g_expr FROM {schema}.t"));
            AssertConverged(await LoadAsync(connection, schema), first, "after the first publish");

            // 1. A changed expression is re-added (its values are derived), keeping the comment; a generated column that becomes ordinary
            // keeps the values it holds; a generated column added by ALTER TABLE is computed.
            var second = Build(Source("a * 3", keepGenerated: false, extra: true));
            var secondScript = await PublishAsync(connection, await LoadAsync(connection, schema), second);
            Assert.AreEqual("15", await Scalar($"SELECT g_expr FROM {schema}.t"), secondScript);
            Assert.AreEqual("s", await Scalar(Generated("g_expr")));
            Assert.AreEqual("doubled", await Scalar($"SELECT col_description('{schema}.t'::regclass, (SELECT attnum FROM pg_attribute WHERE attrelid = '{schema}.t'::regclass AND attname = 'g_expr'))"));
            Assert.AreEqual(string.Empty, await Scalar(Generated("g_keep")), "It is an ordinary column now.");
            Assert.AreEqual("6", await Scalar($"SELECT g_keep FROM {schema}.t"), "Its values are kept.");
            Assert.AreEqual("5!", await Scalar($"SELECT g_new FROM {schema}.t"));
            AssertConverged(await LoadAsync(connection, schema), second, "after the changes");
            var settled = await PublishAsync(connection, await LoadAsync(connection, schema), second);
            Assert.AreEqual(string.Empty, settled.Trim(), "A second publish must have nothing to do: " + settled);

            // 2. An ordinary column that becomes generated cannot be converted in place and would lose its values: nothing is deployed
            // and the script says so, which blocks `publish --execute`.
            var third = Build(Source("a * 3", keepGenerated: false, extra: true, ordinaryBecomesGenerated: true));
            var scriptGenerator = new ScriptGenerator();
            var live = await LoadAsync(connection, schema);
            var batch = scriptGenerator.Generate(new PgProj.Diff.DiffEngine().Diff(live, third), new PgProj.Dependencies.DependencyAnalyzer().Analyze(live, third));
            StringAssert.Contains(scriptGenerator.GenerateSqlScript(batch), "-- TODO: Column \"o\"");
            Assert.IsNotEmpty(batch.GetUnsupportedOperations().ToArray(), "publish --execute must refuse it.");
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {schema} CASCADE;");
        }
    }

    [TestMethod]
    public async Task AGeneratedColumnThatCallsAProjectFunction_IsCreatedAfterTheFunctionAndDroppedBeforeIt()
    {
        var schema = $"test_g71f_{Guid.NewGuid():N}";
        await using var connection = await OpenConnectionAsync();
        try
        {
            // The function's name sorts after the table's, so only a real dependency puts it first.
            var target = Build($"""
                CREATE SCHEMA {schema};
                CREATE TABLE {schema}.t (a integer, doubled integer GENERATED ALWAYS AS ({schema}.zz_double(a)) STORED);
                CREATE FUNCTION {schema}.zz_double(x integer) RETURNS integer LANGUAGE sql IMMUTABLE AS $$ SELECT x * 2 $$;
                """);
            await PublishAsync(connection, DatabaseModel.Empty, target);
            await ExecuteAsync(connection, $"INSERT INTO {schema}.t (a) VALUES (21);");
            await using (var command = new NpgsqlCommand($"SELECT doubled FROM {schema}.t", connection))
            {
                Assert.AreEqual(42, await command.ExecuteScalarAsync());
            }

            AssertConverged(await LoadAsync(connection, schema), target, "after the first publish");

            var removal = await PublishAsync(connection, await LoadAsync(connection, schema), Build($"CREATE SCHEMA {schema};"));
            await using var count = new NpgsqlCommand($"SELECT count(*) FROM pg_class WHERE relnamespace = '{schema}'::regnamespace", connection);
            Assert.AreEqual(0L, await count.ExecuteScalarAsync(), removal);
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
        Assert.AreEqual(0, reDiff.Added.Count + reDiff.Removed.Count + reDiff.Modified.Count, step + ": " + described + " | Live tables: " + string.Join(", ", live.Tables.Keys) + " | Live diagnostics: " + string.Join("; ", live.Diagnostics.Select(static d => d.Severity + " " + d.Message)));
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
