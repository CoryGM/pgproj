using Npgsql;
using PgProj.Core;
using PgProj.Deploy;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-24c (Phase 5) against a real server: before this, a partitioned parent and its partitions were read back from a live
/// database as flat, unrelated ordinary tables with no partitioning information at all (confirmed while scoping this gap), so
/// <c>diff</c>/<c>--dry-run</c> against an already-partitioned database silently misrepresented it. <see cref="TablePartitioningEndToEndTests"/>
/// (Phase 6) covers the full native-vs-pgproj-deployed oracle; this covers only reading an existing (natively created) hierarchy.
/// </summary>
[TestClass]
public sealed class TablePartitioningLiveLoaderEndToEndTests
{
    private readonly PgQueryParser _parser = new();
    private readonly SemanticBuilder _semanticBuilder = new();
    private readonly LiveDatabaseModelLoader _liveLoader;

    public TablePartitioningLiveLoaderEndToEndTests()
    {
        _liveLoader = new LiveDatabaseModelLoader(_parser, _semanticBuilder);
    }

    [TestMethod]
    public async Task AnExistingPartitionedHierarchyIsReadBackWithItsStrategyKeyParentAndBound()
    {
        var schema = $"test_g24c5_{Guid.NewGuid():N}";
        await using var connection = await OpenConnectionAsync();
        try
        {
            var source = $"""
                CREATE SCHEMA {schema};
                CREATE TABLE {schema}.p (id integer, d date, name text) PARTITION BY RANGE (d);
                CREATE TABLE {schema}.p_2024 PARTITION OF {schema}.p FOR VALUES FROM ('2024-01-01') TO ('2025-01-01');
                CREATE TABLE {schema}.p_default PARTITION OF {schema}.p DEFAULT;
                CREATE TABLE {schema}.lst (id integer, cat text) PARTITION BY LIST (cat);
                CREATE TABLE {schema}.lst_a PARTITION OF {schema}.lst FOR VALUES IN ('a', 'b');
                CREATE TABLE {schema}.h (id integer) PARTITION BY HASH (id);
                CREATE TABLE {schema}.h_0 PARTITION OF {schema}.h FOR VALUES WITH (modulus 2, remainder 0);
                """;
            await ExecuteAsync(connection, source);

            var live = await LoadAsync(connection, schema);
            Assert.IsFalse(live.HasErrors, string.Join("; ", live.Diagnostics.Select(static d => d.Message)));

            var p = live.Tables[$"{schema}.p"];
            Assert.AreEqual("RANGE", p.PartitionStrategy);
            Assert.AreEqual("d", p.PartitionKeyElements.Single().Name);
            Assert.IsNull(p.PartitionOfTableName);

            var p2024 = live.Tables[$"{schema}.p_2024"];
            Assert.AreEqual(schema, p2024.PartitionOfSchemaName);
            Assert.AreEqual("p", p2024.PartitionOfTableName);
            StringAssert.Contains(p2024.PartitionBoundRaw, "2024-01-01");
            CollectionAssert.AreEquivalent(new[] { "id", "d", "name" }, p2024.Columns.Select(static c => c.Name).ToArray());

            var pDefault = live.Tables[$"{schema}.p_default"];
            Assert.AreEqual("DEFAULT", pDefault.PartitionBoundRaw);

            var lst = live.Tables[$"{schema}.lst"];
            Assert.AreEqual("LIST", lst.PartitionStrategy);
            Assert.AreEqual("cat", lst.PartitionKeyElements.Single().Name);

            var lstA = live.Tables[$"{schema}.lst_a"];
            Assert.AreEqual("lst", lstA.PartitionOfTableName);
            StringAssert.Contains(lstA.PartitionBoundRaw, "'a'");

            var h = live.Tables[$"{schema}.h"];
            Assert.AreEqual("HASH", h.PartitionStrategy);

            var h0 = live.Tables[$"{schema}.h_0"];
            Assert.AreEqual("h", h0.PartitionOfTableName);
            StringAssert.Contains(h0.PartitionBoundRaw, "modulus", StringComparison.OrdinalIgnoreCase);

            // The strongest read-fidelity check: the live-loaded model (from the loader's synthetic snapshot SQL) diffs empty
            // against the exact same DDL parsed the ordinary project way.
            var project = Build(source);
            AssertConverged(live, project, "live read vs. the same DDL parsed as a project");
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {schema} CASCADE;");
        }
    }

    private static void AssertConverged(DatabaseModel live, DatabaseModel target, string step)
    {
        var reDiff = new DiffEngine().Diff(live, target);
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
