using Npgsql;
using PgProj.Core;
using PgProj.Deploy;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-64 against a real server: a function or procedure whose body changes while something that survives still uses it (a column
/// default, a view, a CHECK constraint, an expression index) used to be dropped first, and PostgreSQL refused (<c>2BP01</c>).
/// It is now replaced in place, which keeps every dependent and the routine's grants.
/// </summary>
[TestClass]
public sealed class RoutineReplaceEndToEndTests
{
    private readonly PgQueryParser _parser = new();
    private readonly SemanticBuilder _semanticBuilder = new();
    private readonly LiveDatabaseModelLoader _liveLoader;

    public RoutineReplaceEndToEndTests()
    {
        _liveLoader = new LiveDatabaseModelLoader(_parser, _semanticBuilder);
    }

    [TestMethod]
    public async Task ChangedFunctionBody_IsReplacedWhileEveryKindOfDependentSurvives()
    {
        var schema = $"test_g64_{Guid.NewGuid():N}";
        var role = $"g64r_{Guid.NewGuid():N}"[..18];
        await using var connection = await OpenConnectionAsync();
        try
        {
            await ExecuteAsync(connection, $"CREATE ROLE {role} NOLOGIN;");

            string Source(int addend) => $"""
                CREATE SCHEMA {schema};
                CREATE FUNCTION {schema}.f(x integer) RETURNS integer LANGUAGE sql IMMUTABLE AS $$ SELECT x + {addend} $$;
                CREATE PROCEDURE {schema}.p(x integer) LANGUAGE sql AS $$ SELECT {addend} $$;
                CREATE TABLE {schema}.t (
                    n integer DEFAULT {schema}.f(1),
                    m integer,
                    CONSTRAINT t_m_positive CHECK ({schema}.f(m) > 0));
                CREATE INDEX t_f_m ON {schema}.t (({schema}.f(m)));
                CREATE VIEW {schema}.v AS SELECT {schema}.f(n) AS z FROM {schema}.t;
                GRANT EXECUTE ON FUNCTION {schema}.f(integer) TO {role};
                """;

            async Task<object?> Scalar(string sql)
            {
                await using var command = new NpgsqlCommand(sql, connection);
                return await command.ExecuteScalarAsync();
            }

            var first = Build(Source(1));
            await PublishAsync(connection, DatabaseModel.Empty, first);
            Assert.AreEqual(2, await Scalar($"SELECT {schema}.f(1)"));

            // The register's repro, with every dependent kind: change the body and publish again.
            var second = Build(Source(2));
            var script = await PublishAsync(connection, await LoadAsync(connection, schema), second);

            Assert.IsFalse(script.Contains("DROP FUNCTION", StringComparison.Ordinal), script);
            StringAssert.Contains(script, "CREATE OR REPLACE FUNCTION", script);
            Assert.AreEqual(3, await Scalar($"SELECT {schema}.f(1)"), "The new body must be in effect.");
            Assert.AreEqual(true, await Scalar($"SELECT has_function_privilege('{role}', '{schema}.f(integer)', 'EXECUTE')"), "The grant must survive the replace.");
            Assert.AreEqual(1L, await Scalar($"SELECT count(*) FROM pg_constraint WHERE conname = 't_m_positive' AND connamespace = '{schema}'::regnamespace"), "The CHECK constraint must survive.");
            Assert.AreEqual(1L, await Scalar($"SELECT count(*) FROM pg_indexes WHERE schemaname = '{schema}' AND indexname = 't_f_m'"), "The index must survive.");
            Assert.AreEqual(1L, await Scalar($"SELECT count(*) FROM pg_views WHERE schemaname = '{schema}' AND viewname = 'v'"), "The view must survive.");

            var live = await LoadAsync(connection, schema);
            var reDiff = await EndToEndSupport.DiffAsPublishedAsync(connection, live, second);
            Assert.AreEqual(0, reDiff.Added.Count + reDiff.Removed.Count + reDiff.Modified.Count, "Added: " + string.Join(", ", reDiff.Added.Select(static c => c.NodeId)) + " Removed: " + string.Join(", ", reDiff.Removed.Select(static c => c.NodeId)) + " Modified: " + string.Join(", ", reDiff.Modified.Select(static c => c.NodeId)));
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {schema} CASCADE;");
            await ExecuteAsync(connection, $"DROP OWNED BY {role}; DROP ROLE IF EXISTS {role};");
        }
    }

    /// <summary>
    /// G-66 against a real server. A routine with attributes, defaults, <c>INOUT</c>, <c>SETOF</c>, and a <c>varchar(n)</c> parameter
    /// must read back from the catalog as the same routine (no phantom diff, so a second publish does nothing), and a change to any
    /// of those, which the diff used to miss, must be detected and deployed: replaced in place where PostgreSQL allows it and
    /// dropped and recreated where it does not, converging every time.
    /// </summary>
    [TestMethod]
    public async Task RoutineChanges_AreDetectedDeployedAndConverge()
    {
        var schema = $"test_g66_{Guid.NewGuid():N}";
        await using var connection = await OpenConnectionAsync();
        try
        {
            string Source(string volatility, string secondDefault, string firstParameter, string result, string procedureBody) => $"""
                CREATE SCHEMA {schema};
                CREATE FUNCTION {schema}.f({firstParameter} integer, b text DEFAULT {secondDefault}, c varchar(10) DEFAULT NULL)
                    RETURNS {result} LANGUAGE sql {volatility} STRICT SECURITY DEFINER AS $$ SELECT 1 $$;
                CREATE PROCEDURE {schema}.p(a integer, INOUT b text DEFAULT 'x') LANGUAGE plpgsql AS $$ BEGIN b := b || '{procedureBody}'; END $$;
                """;

            async Task<string?> Scalar(string sql)
            {
                await using var command = new NpgsqlCommand(sql, connection);
                return Convert.ToString(await command.ExecuteScalarAsync());
            }

            async Task AssertConverged(DatabaseModel target, string step)
            {
                var live = await LoadAsync(connection, schema);
                var reDiff = await EndToEndSupport.DiffAsPublishedAsync(connection, live, target);
                var described = "Added: " + string.Join(", ", reDiff.Added.Select(static c => c.NodeId)) + " Removed: " + string.Join(", ", reDiff.Removed.Select(static c => c.NodeId))
                    + " Modified: " + string.Join("; ", reDiff.Modified.Select(static c => c.NodeId + " [" + string.Join(", ", c.Details.Select(static d => d.PropertyName + ": " + d.OldValue + " -> " + d.NewValue)) + "]"));
                Assert.AreEqual(0, reDiff.Added.Count + reDiff.Removed.Count + reDiff.Modified.Count, step + ": " + described);
            }

            // 1. Deploy, and read back as the same routines.
            var first = Build(Source("STABLE", "'x'", "a", "SETOF integer", "y"));
            await PublishAsync(connection, DatabaseModel.Empty, first);
            await AssertConverged(first, "after the first publish");

            // 2. A change to volatility and to a procedure body: both were invisible. Both are replaced in place.
            var second = Build(Source("IMMUTABLE", "'x'", "a", "SETOF integer", "z"));
            var script = await PublishAsync(connection, await LoadAsync(connection, schema), second);
            Assert.IsFalse(script.Contains("DROP ", StringComparison.Ordinal), script);
            Assert.AreEqual("i", await Scalar($"SELECT provolatile FROM pg_proc WHERE oid = '{schema}.f(integer,text,character varying)'::regprocedure"), "The volatility must have changed.");
            StringAssert.Contains(await Scalar($"SELECT prosrc FROM pg_proc WHERE oid = '{schema}.p(integer,text)'::regprocedure") ?? string.Empty, "'z'", "The procedure body must have changed.");
            await AssertConverged(second, "after changing volatility and a procedure body");

            // 3. A changed parameter default cannot be replaced blindly, so it is dropped and recreated, and lands.
            var third = Build(Source("IMMUTABLE", "'changed'", "a", "SETOF integer", "z"));
            var thirdScript = await PublishAsync(connection, await LoadAsync(connection, schema), third);
            StringAssert.Contains(thirdScript, "DROP FUNCTION", thirdScript);
            StringAssert.Contains(await Scalar($"SELECT pg_get_function_arguments('{schema}.f(integer,text,character varying)'::regprocedure)") ?? string.Empty, "changed");
            await AssertConverged(third, "after changing a parameter default");

            // 4. A renamed parameter, and 5. a result that is no longer a set: neither can be replaced, both are dropped and recreated.
            var fourth = Build(Source("IMMUTABLE", "'changed'", "renamed", "SETOF integer", "z"));
            await PublishAsync(connection, await LoadAsync(connection, schema), fourth);
            StringAssert.Contains(await Scalar($"SELECT pg_get_function_arguments('{schema}.f(integer,text,character varying)'::regprocedure)") ?? string.Empty, "renamed");
            await AssertConverged(fourth, "after renaming a parameter");

            var fifth = Build(Source("IMMUTABLE", "'changed'", "renamed", "integer", "z"));
            await PublishAsync(connection, await LoadAsync(connection, schema), fifth);
            Assert.AreEqual("False", await Scalar($"SELECT proretset FROM pg_proc WHERE oid = '{schema}.f(integer,text,character varying)'::regprocedure"), "The function must no longer return a set.");
            await AssertConverged(fifth, "after it stopped returning a set");
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {schema} CASCADE;");
        }
    }

    /// <summary>
    /// G-67 against a real server. The catalog spells parameter types out (<c>integer</c>, <c>bigint</c>, <c>timestamp with time
    /// zone</c>); a project that writes <c>int4</c>, <c>int8</c>, or <c>timestamptz</c> is the same routine and must not be dropped and
    /// recreated on every publish. A second publish must do nothing, and an edit must still be a replace. (The procedure uses short names
    /// and <c>text</c> too: its types used to be qualified with its own schema, G-68.)
    /// </summary>
    [TestMethod]
    public async Task RoutinesWrittenWithShortTypeNames_ConvergeAndAreNotRecreatedEveryPublish()
    {
        var schema = $"test_g67_{Guid.NewGuid():N}";
        await using var connection = await OpenConnectionAsync();
        try
        {
            string Source(int addend) => $"""
                CREATE SCHEMA {schema};
                CREATE FUNCTION {schema}.f(a int4, b int8, c timestamptz, d int4[]) RETURNS int4 LANGUAGE sql AS $$ SELECT a + {addend} $$;
                CREATE PROCEDURE {schema}.p(x int2, y float8, z text, w bool) LANGUAGE sql AS $$ SELECT {addend} $$;
                """;

            async Task AssertNothingToDo(DatabaseModel target, string step)
            {
                var live = await LoadAsync(connection, schema);
                var changes = await EndToEndSupport.DiffAsPublishedAsync(connection, live, target);
                var operations = new ScriptGenerator().Generate(changes, new PgProj.Dependencies.DependencyAnalyzer().Analyze(live, target)).Operations.Count;
                Assert.AreEqual(0, operations, step + ": Added: " + string.Join(", ", changes.Added.Select(static c => c.NodeId)) + " Removed: " + string.Join(", ", changes.Removed.Select(static c => c.NodeId)) + " Modified: " + string.Join("; ", changes.Modified.Select(static c => c.NodeId + " [" + string.Join(", ", c.Details.Select(static d => d.PropertyName + ": " + d.OldValue + " -> " + d.NewValue)) + "]")));
            }

            var first = Build(Source(1));
            await PublishAsync(connection, DatabaseModel.Empty, first);
            await AssertNothingToDo(first, "after the first publish");

            // An edit is a replace (the routine is recognised as the one that exists), and it converges again.
            var second = Build(Source(2));
            var script = await PublishAsync(connection, await LoadAsync(connection, schema), second);
            Assert.IsFalse(script.Contains("DROP ", StringComparison.Ordinal), script);
            StringAssert.Contains(script, "CREATE OR REPLACE FUNCTION", script);
            await AssertNothingToDo(second, "after editing the bodies");
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {schema} CASCADE;");
        }
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
            await EndToEndSupport.DiffAsPublishedAsync(connection, live, target),
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
