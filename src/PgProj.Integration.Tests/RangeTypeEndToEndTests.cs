using Npgsql;
using PgProj.Core;
using PgProj.Deploy;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-69 against a real server: PostgreSQL creates a constructor function for each arity of a range type, and a multirange type with
/// its own constructors. The live loader read those functions as ordinary objects the project never declared, so a range type diffed
/// as five removed functions after every publish. They are now recognised as part of the range type and left out.
/// </summary>
[TestClass]
public sealed class RangeTypeEndToEndTests
{
    private readonly PgQueryParser _parser = new();
    private readonly SemanticBuilder _semanticBuilder = new();
    private readonly LiveDatabaseModelLoader _liveLoader;

    public RangeTypeEndToEndTests()
    {
        _liveLoader = new LiveDatabaseModelLoader(_parser, _semanticBuilder);
    }

    [TestMethod]
    public async Task RangeTypes_ConvergeAndAreRemovedWithTheirConstructors()
    {
        var schema = $"test_g69_{Guid.NewGuid():N}";
        var role = $"g69r_{Guid.NewGuid():N}"[..18];
        await using var connection = await OpenConnectionAsync();
        try
        {
            await ExecuteAsync(connection, $"CREATE ROLE {role} NOLOGIN;");

            // A range type, a range with a second subtype and a comment, an ordinary function beside them (which must still be read),
            // a table that uses a range, and a grant over every function in the schema.
            var source = $"""
                CREATE SCHEMA {schema};
                CREATE TYPE {schema}.int_span AS RANGE (subtype = integer);
                CREATE TYPE {schema}.float_span AS RANGE (subtype = double precision);
                COMMENT ON TYPE {schema}.float_span IS 'a span';
                CREATE FUNCTION {schema}.plain(x integer) RETURNS integer LANGUAGE sql AS $$ SELECT x $$;
                CREATE TABLE {schema}.t (id integer PRIMARY KEY, span {schema}.int_span);
                GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA {schema} TO {role};
                """;

            async Task<long> Scalar(string sql)
            {
                await using var command = new NpgsqlCommand(sql, connection);
                return Convert.ToInt64(await command.ExecuteScalarAsync());
            }

            var target = Build(source);
            await PublishAsync(connection, DatabaseModel.Empty, target);
            Assert.AreEqual(2L, await Scalar($"SELECT count(*) FROM pg_proc WHERE pronamespace = '{schema}'::regnamespace AND proname IN ('int_span', 'float_span') AND pronargs = 2"), "Both range types must have their constructors.");
            await ExecuteAsync(connection, $"INSERT INTO {schema}.t VALUES (1, {schema}.int_span(1, 5));");

            // The register's repro: the project reads back as itself, and a second publish has nothing to do.
            var live = await LoadAsync(connection, schema);
            Assert.IsFalse(live.Functions.Keys.Any(static key => key.Contains("_span", StringComparison.Ordinal)), "Constructors must not be read as functions: " + string.Join(", ", live.Functions.Keys));
            Assert.IsTrue(live.Functions.Keys.Any(static key => key.EndsWith(".plain(int4)", StringComparison.Ordinal)), "An ordinary function must still be read: " + string.Join(", ", live.Functions.Keys));
            AssertConverged(live, target, "after the first publish");
            var again = await PublishAsync(connection, live, target);
            Assert.AreEqual(string.Empty, again.Trim(), "A second publish must have nothing to do: " + again);

            // The role holds EXECUTE on the constructors too (the grant covered every function that existed). A function added by hand
            // that the role cannot execute makes the loader report per-object grants instead of one over the whole schema; the
            // constructors' must not be among them, or the publish would revoke privileges on functions the project never declared.
            Assert.AreEqual(2L, await Scalar($"SELECT count(*) FROM pg_proc WHERE pronamespace = '{schema}'::regnamespace AND proname = 'int_span' AND pronargs >= 2 AND proacl IS NOT NULL"), "The constructors must carry the grant.");
            var moreSql = $"CREATE FUNCTION {schema}.more(x integer) RETURNS integer LANGUAGE sql AS $$ SELECT x $$;";
            await ExecuteAsync(connection, moreSql);
            var withMore = Build(source + "\n" + moreSql);
            var moreLive = await LoadAsync(connection, schema);
            Assert.IsFalse(moreLive.Grants.Keys.Any(static key => key.Contains("_span", StringComparison.Ordinal)), "Grants on constructors must not be read: " + string.Join(", ", moreLive.Grants.Keys));
            var moreScript = await PublishAsync(connection, moreLive, withMore);
            Assert.IsFalse(moreScript.Contains("REVOKE", StringComparison.Ordinal), "Nothing may be revoked: " + moreScript);
            AssertConverged(await LoadAsync(connection, schema), withMore, "after the missing grant is applied");

            // Removing the range types (and the table that uses one) drops the constructors with them and leaves the ordinary function.
            var reduced = Build($"""
                CREATE SCHEMA {schema};
                CREATE FUNCTION {schema}.plain(x integer) RETURNS integer LANGUAGE sql AS $$ SELECT x $$;
                CREATE FUNCTION {schema}.more(x integer) RETURNS integer LANGUAGE sql AS $$ SELECT x $$;
                GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA {schema} TO {role};
                """);
            var removal = await PublishAsync(connection, await LoadAsync(connection, schema), reduced);
            StringAssert.Contains(removal, "DROP TYPE", removal);
            Assert.IsFalse(removal.Contains("DROP FUNCTION", StringComparison.Ordinal), "The constructors go with the type, never on their own: " + removal);
            Assert.AreEqual(0L, await Scalar($"SELECT count(*) FROM pg_type WHERE typnamespace = '{schema}'::regnamespace AND typtype IN ('r', 'm')"));
            Assert.AreEqual(2L, await Scalar($"SELECT count(*) FROM pg_proc WHERE pronamespace = '{schema}'::regnamespace"), "Only the two ordinary functions remain.");
            AssertConverged(await LoadAsync(connection, schema), reduced, "after removing the range types");
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {schema} CASCADE;");
            await ExecuteAsync(connection, $"DROP OWNED BY {role}; DROP ROLE IF EXISTS {role};");
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
