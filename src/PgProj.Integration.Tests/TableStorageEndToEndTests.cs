using Npgsql;
using PgProj.Core;
using PgProj.Deploy;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-24b against a real server: a table's <c>UNLOGGED</c> keyword and <c>WITH (...)</c> storage parameters are deployed,
/// read back the same, and a change to either is applied in place. <c>TABLESPACE</c> is covered by unit tests only: a probe
/// against this same container showed that explicitly writing <c>TABLESPACE pg_default</c> still leaves
/// <c>pg_class.reltablespace = 0</c>, the same as writing no <c>TABLESPACE</c> at all, so it cannot be told apart from "unset"
/// using only the tablespaces a disposable container has (a real non-default tablespace needs a writable directory on the
/// server's filesystem, which this fixture does not provision).
/// </summary>
[TestClass]
public sealed class TableStorageEndToEndTests
{
    private readonly PgQueryParser _parser = new();
    private readonly SemanticBuilder _semanticBuilder = new();
    private readonly LiveDatabaseModelLoader _liveLoader;

    public TableStorageEndToEndTests()
    {
        _liveLoader = new LiveDatabaseModelLoader(_parser, _semanticBuilder);
    }

    [TestMethod]
    public async Task AnUnloggedTableWithStorageOptionsIsDeployedReadBackTheSameAndChangedInPlace()
    {
        var schema = $"test_g24b_{Guid.NewGuid():N}";
        await using var connection = await OpenConnectionAsync();
        try
        {
            string Source(bool unlogged, string options) => $"""
                CREATE SCHEMA {schema};
                CREATE {(unlogged ? "UNLOGGED " : string.Empty)}TABLE {schema}.t (a integer){(options.Length == 0 ? string.Empty : $" WITH ({options})")};
                """;

            // 1. Created unlogged, with two options.
            var first = Build(Source(unlogged: true, "fillfactor=70, autovacuum_enabled=false"));
            await PublishAsync(connection, DatabaseModel.Empty, first);
            var (persistenceAfterCreate, optionsAfterCreate) = await ReadCatalogAsync(connection, schema);
            Assert.AreEqual("u", persistenceAfterCreate);
            CollectionAssert.AreEqual(new[] { "autovacuum_enabled=false", "fillfactor=70" }, optionsAfterCreate);
            var liveAfterCreate = await LoadAsync(connection, schema);
            AssertConverged(liveAfterCreate, first, "after create");

            // 2. Logged again, one option changed, one option added, one removed.
            var second = Build(Source(unlogged: false, "fillfactor=80, parallel_workers=4"));
            var changeScript = await PublishAsync(connection, liveAfterCreate, second);
            Assert.IsFalse(changeScript.Contains("DROP TABLE", StringComparison.Ordinal), changeScript);
            Assert.IsFalse(changeScript.Contains("CREATE TABLE", StringComparison.Ordinal), changeScript);
            var (persistenceAfterChange, optionsAfterChange) = await ReadCatalogAsync(connection, schema);
            Assert.AreEqual("p", persistenceAfterChange);
            CollectionAssert.AreEqual(new[] { "fillfactor=80", "parallel_workers=4" }, optionsAfterChange);
            var liveAfterChange = await LoadAsync(connection, schema);
            AssertConverged(liveAfterChange, second, "after change");

            // 3. Every option removed.
            var third = Build(Source(unlogged: false, string.Empty));
            await PublishAsync(connection, liveAfterChange, third);
            var (persistence, options) = await ReadCatalogAsync(connection, schema);
            Assert.AreEqual("p", persistence);
            Assert.IsEmpty(options);
            var liveAfterClear = await LoadAsync(connection, schema);
            AssertConverged(liveAfterClear, third, "after clearing options");

            // 4. A second publish of the same target has nothing to do.
            var again = await PublishAsync(connection, liveAfterClear, third);
            Assert.AreEqual(string.Empty, again.Trim(), "A second publish must have nothing to do: " + again);
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {schema} CASCADE;");
        }
    }

    private static async Task<(string Persistence, string[] Options)> ReadCatalogAsync(NpgsqlConnection connection, string schema)
    {
        await using var command = new NpgsqlCommand(
            "select relpersistence::text, coalesce(reloptions, '{}') from pg_catalog.pg_class c join pg_catalog.pg_namespace n on n.oid = c.relnamespace where n.nspname = @schema and c.relname = 't'",
            connection);
        command.Parameters.AddWithValue("schema", schema);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        var persistence = reader.GetString(0);
        var options = reader.GetFieldValue<string[]>(1).OrderBy(static o => o, StringComparer.Ordinal).ToArray();
        return (persistence, options);
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
