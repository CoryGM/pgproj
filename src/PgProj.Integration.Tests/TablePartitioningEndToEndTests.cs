using Npgsql;
using PgProj.Core;
using PgProj.Deploy;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-24c (Phase 6, the last phase) against a real server. Before this gap, a project that declared a partition deployed wrong:
/// <c>PARTITION BY</c> was silently dropped from a parent, and a partition (whose DDL never lists its own columns) built with
/// zero columns and deployed as a disconnected, empty, unrelated table. The oracle is the server itself: the same
/// <c>RANGE</c>/<c>LIST</c>/<c>HASH</c> hierarchies are created natively in one schema and deployed by pgproj (<c>publish</c>)
/// in another, and what PostgreSQL stores (<c>pg_get_partkeydef</c>, <c>pg_get_expr(relpartbound, ...)</c>) must be identical.
/// <see cref="TablePartitioningLiveLoaderEndToEndTests"/> (Phase 5) covers reading an already-partitioned database; this covers
/// the full round trip through a real <c>publish</c>, plus the two ordering guarantees Phase 4 relied on at the graph level
/// (parent-before-child creation; a parent's drop cascades to its partitions) actually holding against PostgreSQL itself.
/// </summary>
[TestClass]
public sealed class TablePartitioningEndToEndTests
{
    private readonly PgQueryParser _parser = new();
    private readonly SemanticBuilder _semanticBuilder = new();
    private readonly LiveDatabaseModelLoader _liveLoader;

    public TablePartitioningEndToEndTests()
    {
        _liveLoader = new LiveDatabaseModelLoader(_parser, _semanticBuilder);
    }

    // A hash bound's MODULUS/REMAINDER reads back lower-cased regardless of how it is written (G-24c, Phase 5's known limit);
    // written lower-case here so the comparison below is not tripped up by that unrelated, already-documented case difference.
    private static string Hierarchy(string schema) => $"""
        CREATE SCHEMA {schema};
        CREATE TABLE {schema}.p (id integer, d date, name text) PARTITION BY RANGE (d);
        CREATE TABLE {schema}.p_2024 PARTITION OF {schema}.p FOR VALUES FROM ('2024-01-01') TO ('2025-01-01');
        CREATE TABLE {schema}.p_2025 PARTITION OF {schema}.p FOR VALUES FROM ('2025-01-01') TO ('2026-01-01');
        CREATE TABLE {schema}.p_default PARTITION OF {schema}.p DEFAULT;
        CREATE TABLE {schema}.lst (id integer, cat text) PARTITION BY LIST (cat);
        CREATE TABLE {schema}.lst_ab PARTITION OF {schema}.lst FOR VALUES IN ('a', 'b');
        CREATE TABLE {schema}.lst_default PARTITION OF {schema}.lst DEFAULT;
        CREATE TABLE {schema}.h (id integer) PARTITION BY HASH (id);
        CREATE TABLE {schema}.h_0 PARTITION OF {schema}.h FOR VALUES WITH (modulus 2, remainder 0);
        CREATE TABLE {schema}.h_1 PARTITION OF {schema}.h FOR VALUES WITH (modulus 2, remainder 1);
        """;

    private static readonly string[] TableNames =
    [
        "p", "p_2024", "p_2025", "p_default",
        "lst", "lst_ab", "lst_default",
        "h", "h_0", "h_1",
    ];

    [TestMethod]
    public async Task APublishedHierarchyMatchesTheSameHierarchyCreatedNatively()
    {
        var pgprojSchema = $"test_g24c6a_{Guid.NewGuid():N}";
        var nativeSchema = $"test_g24c6b_{Guid.NewGuid():N}";
        await using var connection = await OpenConnectionAsync();
        try
        {
            var target = Build(Hierarchy(pgprojSchema));
            Assert.IsFalse(target.HasErrors, string.Join("; ", target.Diagnostics.Select(static d => d.Message)));
            await PublishAsync(connection, DatabaseModel.Empty, target);
            await ExecuteAsync(connection, Hierarchy(nativeSchema));

            var deployed = await PartitionDefinitionsAsync(connection, pgprojSchema);
            var written = await PartitionDefinitionsAsync(connection, nativeSchema);
            foreach (var name in TableNames)
            {
                Assert.AreEqual(written[name], deployed[name], $"Table '{name}' is stored differently when deployed by pgproj.");
            }

            // The re-diff and a second publish must have nothing to do.
            var live = await LoadAsync(connection, pgprojSchema);
            AssertConverged(live, target, "after the first publish");
            var again = await PublishAsync(connection, live, target);
            Assert.AreEqual(string.Empty, again.Trim(), "A second publish must have nothing to do: " + again);
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {pgprojSchema} CASCADE; DROP SCHEMA IF EXISTS {nativeSchema} CASCADE;");
        }
    }

    [TestMethod]
    public async Task RemovingAWholeHierarchyDropsEveryTableWithNoRedundantDropAfterAParentsCascade()
    {
        // ScriptGenerator drops every removed table individually, each partition before its parent (the dependent-first
        // ordering Phase 4 relies on) — it does not lean on a parent's own cascade to remove its partitions, so this proves
        // the whole script actually executes cleanly against a real server, not just that the generated text looks ordered.
        var schema = $"test_g24c6c_{Guid.NewGuid():N}";
        await using var connection = await OpenConnectionAsync();
        try
        {
            var target = Build(Hierarchy(schema));
            await PublishAsync(connection, DatabaseModel.Empty, target);
            var live = await LoadAsync(connection, schema);

            var empty = Build($"CREATE SCHEMA {schema};");
            await PublishAsync(connection, live, empty);

            var remaining = await TableNamesInSchemaAsync(connection, schema);
            Assert.IsEmpty(remaining, string.Join(",", remaining));
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {schema} CASCADE;");
        }
    }

    [TestMethod]
    public async Task DroppingJustAPublishedParentCascadesToItsPartitionsOnARealServer()
    {
        // The reverse proof: a single "DROP TABLE parent" (the statement PostgreSQL itself would run, not pgproj's own
        // dependent-first script) still removes every partition with it, confirmed against a real server during scoping and
        // relied on by Phase 4's reasoning for why no partition's drop is ever redundant.
        var schema = $"test_g24c6d_{Guid.NewGuid():N}";
        await using var connection = await OpenConnectionAsync();
        try
        {
            var target = Build(Hierarchy(schema));
            await PublishAsync(connection, DatabaseModel.Empty, target);

            await ExecuteAsync(connection, $"DROP TABLE \"{schema}\".\"p\";");
            var remainingAfterParentDrop = await TableNamesInSchemaAsync(connection, schema);
            CollectionAssert.AreEquivalent(new[] { "lst", "lst_ab", "lst_default", "h", "h_0", "h_1" }, remainingAfterParentDrop);
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {schema} CASCADE;");
        }
    }

    private static async Task<Dictionary<string, (string? PartitionKeyDef, string? Parent, string? Bound)>> PartitionDefinitionsAsync(NpgsqlConnection connection, string schema)
    {
        var result = new Dictionary<string, (string?, string?, string?)>(StringComparer.Ordinal);
        await using var command = new NpgsqlCommand(
            """
            select cls.relname,
                   pg_catalog.pg_get_partkeydef(cls.oid),
                   case when cls.relispartition then parent_cls.relname end,
                   case when cls.relispartition then pg_catalog.pg_get_expr(cls.relpartbound, cls.oid) end
            from pg_catalog.pg_class cls
            join pg_catalog.pg_namespace ns on ns.oid = cls.relnamespace
            left join pg_catalog.pg_inherits inh on inh.inhrelid = cls.oid
            left join pg_catalog.pg_class parent_cls on parent_cls.oid = inh.inhparent
            where ns.nspname = @schema and cls.relkind in ('r', 'p')
            """,
            connection);
        command.Parameters.AddWithValue("schema", schema);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result[reader.GetString(0)] = (
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3));
        }

        return result;
    }

    private static async Task<string[]> TableNamesInSchemaAsync(NpgsqlConnection connection, string schema)
    {
        await using var command = new NpgsqlCommand(
            "select c.relname from pg_catalog.pg_class c join pg_catalog.pg_namespace n on n.oid = c.relnamespace where n.nspname = @schema and c.relkind in ('r', 'p')",
            connection);
        command.Parameters.AddWithValue("schema", schema);
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return [.. names];
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
