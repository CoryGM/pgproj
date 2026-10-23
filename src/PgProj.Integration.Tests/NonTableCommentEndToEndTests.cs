using Npgsql;
using PgProj.Core;
using PgProj.Deploy;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-34 against a real server: a comment on a view, a materialized view, a function, a procedure, an index, or a sequence is
/// created, changed, and cleared, matching what PostgreSQL's own <c>obj_description</c>/<c>col_description</c> report, and a
/// second publish has nothing to do. This exercises three bugs found while writing the unit tests: a sequence comment-only
/// change used to build a bare <c>ALTER SEQUENCE name;</c> with no action, which <c>--execute</c> would have failed on
/// (<c>42601</c>); a removed view, function, or procedure comment was never cleared live because <c>CREATE OR REPLACE</c> does
/// not touch a comment it is not setting; and a comment-only change to an index or a materialized view dropped and recreated it.
/// </summary>
[TestClass]
public sealed class NonTableCommentEndToEndTests
{
    private readonly PgQueryParser _parser = new();
    private readonly SemanticBuilder _semanticBuilder = new();
    private readonly LiveDatabaseModelLoader _liveLoader;

    public NonTableCommentEndToEndTests()
    {
        _liveLoader = new LiveDatabaseModelLoader(_parser, _semanticBuilder);
    }

    [TestMethod]
    public async Task EachKindsCommentIsCreatedChangedAndClearedMatchingWhatTheServerReports()
    {
        var schema = $"test_g34_{Guid.NewGuid():N}";
        await using var connection = await OpenConnectionAsync();
        try
        {
            string Source(string? viewComment, string? mvComment, string? fnComment, string? procComment, string? indexComment, string? seqComment) => $"""
                CREATE SCHEMA {schema};
                CREATE SEQUENCE {schema}.s;
                {(seqComment is null ? string.Empty : $"COMMENT ON SEQUENCE {schema}.s IS '{seqComment}';")}
                CREATE TABLE {schema}.t (a integer, b integer);
                CREATE INDEX i ON {schema}.t (a);
                {(indexComment is null ? string.Empty : $"COMMENT ON INDEX {schema}.i IS '{indexComment}';")}
                CREATE VIEW {schema}.v AS SELECT a FROM {schema}.t;
                {(viewComment is null ? string.Empty : $"COMMENT ON VIEW {schema}.v IS '{viewComment}';")}
                CREATE MATERIALIZED VIEW {schema}.mv AS SELECT a FROM {schema}.t;
                {(mvComment is null ? string.Empty : $"COMMENT ON MATERIALIZED VIEW {schema}.mv IS '{mvComment}';")}
                CREATE FUNCTION {schema}.f(x integer) RETURNS integer LANGUAGE sql AS $body$ SELECT x $body$;
                {(fnComment is null ? string.Empty : $"COMMENT ON FUNCTION {schema}.f(integer) IS '{fnComment}';")}
                CREATE PROCEDURE {schema}.p(x integer) LANGUAGE sql AS $body$ SELECT x $body$;
                {(procComment is null ? string.Empty : $"COMMENT ON PROCEDURE {schema}.p(integer) IS '{procComment}';")}
                """;

            // 1. Created with a comment each.
            var first = Build(Source("view one", "mv one", "fn one", "proc one", "index one", "seq one"));
            await PublishAsync(connection, DatabaseModel.Empty, first);
            await AssertServerComments(connection, schema, "view one", "mv one", "fn one", "proc one", "index one", "seq one");
            var liveAfterCreate = await LoadAsync(connection, schema);
            await AssertConverged(connection, liveAfterCreate, first, "after create");

            // 2. Every comment changed. This is where the sequence bug (bare ALTER SEQUENCE) would have failed --execute.
            var second = Build(Source("view two", "mv two", "fn two", "proc two", "index two", "seq two"));
            var changeScript = await PublishAsync(connection, liveAfterCreate, second);
            Assert.IsFalse(changeScript.Contains("DROP INDEX", StringComparison.Ordinal), changeScript);
            Assert.IsFalse(changeScript.Contains("DROP MATERIALIZED VIEW", StringComparison.Ordinal), changeScript);
            await AssertServerComments(connection, schema, "view two", "mv two", "fn two", "proc two", "index two", "seq two");
            var liveAfterChange = await LoadAsync(connection, schema);
            await AssertConverged(connection, liveAfterChange, second, "after change");

            // 3. Every comment removed. This is where the CREATE-OR-REPLACE clearing bug would have left the old comment live.
            var third = Build(Source(null, null, null, null, null, null));
            await PublishAsync(connection, liveAfterChange, third);
            await AssertServerComments(connection, schema, null, null, null, null, null, null);
            var liveAfterClear = await LoadAsync(connection, schema);
            await AssertConverged(connection, liveAfterClear, third, "after clearing");

            // 4. A second publish of the same target has nothing to do.
            var again = await PublishAsync(connection, liveAfterClear, third);
            Assert.AreEqual(string.Empty, again.Trim(), "A second publish must have nothing to do: " + again);
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {schema} CASCADE;");
        }
    }

    private static async Task AssertServerComments(
        NpgsqlConnection connection,
        string schema,
        string? view,
        string? materializedView,
        string? function,
        string? procedure,
        string? index,
        string? sequence)
    {
        async Task<string?> Comment(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("schema", schema);
            var result = await command.ExecuteScalarAsync();
            return result is null or DBNull ? null : (string)result;
        }

        Assert.AreEqual(view, await Comment(
            "SELECT pg_catalog.obj_description(c.oid, 'pg_class') FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = @schema AND c.relname = 'v'"));
        Assert.AreEqual(materializedView, await Comment(
            "SELECT pg_catalog.obj_description(c.oid, 'pg_class') FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = @schema AND c.relname = 'mv'"));
        Assert.AreEqual(function, await Comment(
            "SELECT pg_catalog.obj_description(p.oid, 'pg_proc') FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = @schema AND p.proname = 'f'"));
        Assert.AreEqual(procedure, await Comment(
            "SELECT pg_catalog.obj_description(p.oid, 'pg_proc') FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = @schema AND p.proname = 'p'"));
        Assert.AreEqual(index, await Comment(
            "SELECT pg_catalog.obj_description(c.oid, 'pg_class') FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = @schema AND c.relname = 'i'"));
        Assert.AreEqual(sequence, await Comment(
            "SELECT pg_catalog.obj_description(c.oid, 'pg_class') FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = @schema AND c.relname = 's'"));
    }

    private static async Task AssertConverged(NpgsqlConnection connection, DatabaseModel live, DatabaseModel target, string step)
    {
        var reDiff = await EndToEndSupport.DiffAsPublishedAsync(connection, live, target);
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
            await EndToEndSupport.DiffAsPublishedAsync(connection, live, target),
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
