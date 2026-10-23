using Npgsql;
using PgProj.Core;
using PgProj.Deploy;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-70 against a real server. PostgreSQL keeps the case of a quoted identifier, so <c>CREATE TABLE "MyTable" ("Mixed" integer)</c> makes a
/// table named <c>MyTable</c>. The model used to fold every name to lower case, so pgproj deployed <c>mytable</c>, and anything that named
/// the quoted spelling (a trigger, a function or view body, an application) failed or reached a different object. Every kind of object
/// below must be deployed under exactly the name the project wrote, read back as the same object, and removed cleanly.
/// </summary>
[TestClass]
public sealed class MixedCaseIdentifierEndToEndTests
{
    private readonly PgQueryParser _parser = new();
    private readonly SemanticBuilder _semanticBuilder = new();
    private readonly LiveDatabaseModelLoader _liveLoader;

    public MixedCaseIdentifierEndToEndTests()
    {
        _liveLoader = new LiveDatabaseModelLoader(_parser, _semanticBuilder);
    }

    [TestMethod]
    public async Task MixedCaseNames_AreDeployedAsWritten_ReadBackAsTheSameObjects_AndRemoved()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var schema = $"MySchema_{suffix}";
        var role = $"SomeRole_{suffix}";
        await using var connection = await OpenConnectionAsync();
        try
        {
            await ExecuteAsync(connection, $"CREATE ROLE \"{role}\" NOLOGIN;");

            var source = $$"""
                CREATE SCHEMA "{{schema}}";
                CREATE TYPE "{{schema}}"."Mood" AS ENUM ('sad', 'ok');
                CREATE SEQUENCE "{{schema}}"."TicketSeq";
                CREATE TABLE "{{schema}}"."Parent" (
                    "Id" integer PRIMARY KEY,
                    "Code" text UNIQUE,
                    "State" "{{schema}}"."Mood" NOT NULL DEFAULT 'ok',
                    "Order" integer);
                CREATE TABLE "{{schema}}"."Child" (
                    "Id" integer,
                    "ParentId" integer REFERENCES "{{schema}}"."Parent" ("Id"),
                    "Amount" integer CHECK ("Amount" > 0),
                    "Ticket" integer DEFAULT nextval('"{{schema}}"."TicketSeq"'),
                    CONSTRAINT "Child_Pk" PRIMARY KEY ("Id"));
                CREATE INDEX "IxChild" ON "{{schema}}"."Child" ("Amount", lower("ParentId"::text));
                CREATE UNIQUE INDEX "IxParentCode" ON "{{schema}}"."Parent" ("Code") WHERE "Order" > 0;
                CREATE VIEW "{{schema}}"."ChildView" AS SELECT "Id", "Amount" FROM "{{schema}}"."Child";
                CREATE FUNCTION "{{schema}}"."DoIt"() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$;
                CREATE FUNCTION "{{schema}}"."Describe"("M" "{{schema}}"."Mood", "Prefix" text DEFAULT 'x') RETURNS text LANGUAGE sql AS $$ SELECT "Prefix" || "M"::text $$;
                CREATE TRIGGER "TrgChild" AFTER UPDATE OF "Amount" ON "{{schema}}"."Child" FOR EACH ROW WHEN (new."Amount" > 0) EXECUTE FUNCTION "{{schema}}"."DoIt"();
                COMMENT ON TABLE "{{schema}}"."Parent" IS 'parent';
                COMMENT ON COLUMN "{{schema}}"."Parent"."Code" IS 'code';
                COMMENT ON FUNCTION "{{schema}}"."Describe"("{{schema}}"."Mood", text) IS 'describe';
                GRANT SELECT ON "{{schema}}"."Parent" TO "{{role}}";
                """;

            async Task<long> Count(string sql)
            {
                await using var command = new NpgsqlCommand(sql, connection);
                return Convert.ToInt64(await command.ExecuteScalarAsync());
            }

            var target = Build(source);
            Assert.IsFalse(target.Diagnostics.Any(static d => d.Severity == SemanticDiagnosticSeverity.Error), string.Join("; ", target.Diagnostics.Select(static d => d.Message)));
            var script = await PublishAsync(connection, DatabaseModel.Empty, target);

            // 1. Every object exists under exactly the name the project wrote.
            var ns = $"'\"{schema}\"'::regnamespace";
            Assert.AreEqual(2L, await Count($"SELECT count(*) FROM pg_class WHERE relnamespace = {ns} AND relkind = 'r' AND relname IN ('Parent', 'Child')"), script);
            Assert.AreEqual(4L, await Count($"SELECT count(*) FROM pg_attribute WHERE attrelid = '\"{schema}\".\"Parent\"'::regclass AND attname IN ('Id', 'Code', 'State', 'Order')"));
            Assert.AreEqual(4L, await Count($"SELECT count(*) FROM pg_attribute WHERE attrelid = '\"{schema}\".\"Child\"'::regclass AND attname IN ('Id', 'ParentId', 'Amount', 'Ticket')"));
            Assert.AreEqual(1L, await Count($"SELECT count(*) FROM pg_class WHERE relnamespace = {ns} AND relkind = 'S' AND relname = 'TicketSeq'"));
            Assert.AreEqual(1L, await Count($"SELECT count(*) FROM pg_class WHERE relnamespace = {ns} AND relkind = 'v' AND relname = 'ChildView'"));
            Assert.AreEqual(1L, await Count($"SELECT count(*) FROM pg_type WHERE typnamespace = {ns} AND typtype = 'e' AND typname = 'Mood'"));
            Assert.AreEqual(2L, await Count($"SELECT count(*) FROM pg_proc WHERE pronamespace = {ns} AND proname IN ('DoIt', 'Describe')"));
            Assert.AreEqual(2L, await Count($"SELECT count(*) FROM pg_indexes WHERE schemaname = '{schema}' AND indexname IN ('IxChild', 'IxParentCode')"));
            Assert.AreEqual(1L, await Count($"SELECT count(*) FROM pg_constraint WHERE connamespace = {ns} AND conname = 'Child_Pk'"));
            Assert.AreEqual(1L, await Count($"SELECT count(*) FROM pg_trigger WHERE tgname = 'TrgChild' AND NOT tgisinternal"));
            Assert.AreEqual(1L, await Count($"SELECT count(*) FROM pg_constraint WHERE connamespace = {ns} AND contype = 'f' AND conrelid = '\"{schema}\".\"Child\"'::regclass"));
            Assert.AreEqual(1L, await Count($"SELECT count(*) FROM pg_constraint WHERE connamespace = {ns} AND contype = 'c' AND conrelid = '\"{schema}\".\"Child\"'::regclass"));
            Assert.AreEqual(1L, await Count($"SELECT count(*) FROM pg_description WHERE objoid = '\"{schema}\".\"Parent\"'::regclass AND description = 'parent' AND objsubid = 0"));
            Assert.AreEqual(1L, await Count($"SELECT count(*) FROM information_schema.role_table_grants WHERE table_schema = '{schema}' AND table_name = 'Parent' AND grantee = '{role}' AND privilege_type = 'SELECT'"));

            // The objects work: the trigger's own condition, the view, and a function taking the enum.
            await ExecuteAsync(connection, $"INSERT INTO \"{schema}\".\"Parent\" (\"Id\", \"Code\") VALUES (1, 'a'); INSERT INTO \"{schema}\".\"Child\" (\"Id\", \"ParentId\", \"Amount\") VALUES (1, 1, 5); UPDATE \"{schema}\".\"Child\" SET \"Amount\" = 6;");
            Assert.AreEqual(1L, await Count($"SELECT count(*) FROM \"{schema}\".\"ChildView\" WHERE \"Amount\" = 6"));

            // 2. Read back from the catalog, it is the same project, and publishing again has nothing to do.
            var live = await LoadAsync(connection, schema);
            await AssertConverged(connection, live, target, "after the first publish");
            var again = await PublishAsync(connection, live, target);
            Assert.AreEqual(string.Empty, again.Trim(), "A second publish must have nothing to do: " + again);

            // 3. A change is deployed to the mixed-case object, not to a lower-case twin.
            var changed = Build(source.Replace("CHECK (\"Amount\" > 0)", "CHECK (\"Amount\" > 1)", StringComparison.Ordinal)
                .Replace("IS 'parent'", "IS 'changed'", StringComparison.Ordinal)
                .Replace("\"Prefix\" text DEFAULT 'x'", "\"Prefix\" text DEFAULT 'y'", StringComparison.Ordinal));
            var changeScript = await PublishAsync(connection, await LoadAsync(connection, schema), changed);
            Assert.IsTrue(changeScript.Contains("Amount", StringComparison.Ordinal), changeScript);
            Assert.AreEqual(1L, await Count($"SELECT count(*) FROM pg_description WHERE objoid = '\"{schema}\".\"Parent\"'::regclass AND description = 'changed' AND objsubid = 0"));
            Assert.AreEqual(1L, await Count($"SELECT count(*) FROM pg_constraint WHERE conrelid = '\"{schema}\".\"Child\"'::regclass AND contype = 'c' AND pg_get_constraintdef(oid) LIKE '%\"Amount\" > 1%'"));
            await AssertConverged(connection, await LoadAsync(connection, schema), changed, "after the changes");

            // 4. Removing everything drops the mixed-case objects and leaves nothing behind.
            var empty = Build($"CREATE SCHEMA \"{schema}\";");
            var removal = await PublishAsync(connection, await LoadAsync(connection, schema), empty);
            Assert.AreEqual(0L, await Count($"SELECT count(*) FROM pg_class WHERE relnamespace = {ns}"), removal);
            Assert.AreEqual(0L, await Count($"SELECT count(*) FROM pg_proc WHERE pronamespace = {ns}"), removal);
            Assert.AreEqual(0L, await Count($"SELECT count(*) FROM pg_type WHERE typnamespace = {ns} AND typtype = 'e'"), removal);
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE;");
            await ExecuteAsync(connection, $"DROP OWNED BY \"{role}\"; DROP ROLE IF EXISTS \"{role}\";");
        }
    }

    [TestMethod]
    public async Task MixedCaseNames_InTheRemainingObjectKinds_AreDeployedAsWritten()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var schema = $"MySchema_{suffix}";
        var role = $"SomeRole_{suffix}";
        await using var connection = await OpenConnectionAsync();
        try
        {
            await ExecuteAsync(connection, $"CREATE ROLE \"{role}\" NOLOGIN;");

            var source = $$"""
                CREATE SCHEMA "{{schema}}";
                CREATE TYPE "{{schema}}"."Mood" AS ENUM ('sad', 'ok');
                CREATE TYPE "{{schema}}"."Pair" AS ("First" integer, "Second" "{{schema}}"."Mood");
                CREATE TYPE "{{schema}}"."IntSpan" AS RANGE (subtype = integer);
                CREATE TABLE "{{schema}}"."Parent" (
                    "Serial" serial PRIMARY KEY,
                    "Ident" integer GENERATED ALWAYS AS IDENTITY,
                    "Mood" "{{schema}}"."Mood",
                    "Pair" "{{schema}}"."Pair",
                    "Span" "{{schema}}"."IntSpan",
                    "Moods" "{{schema}}"."Mood"[]);
                ALTER TABLE "{{schema}}"."Parent" ADD COLUMN "Extra" text NOT NULL DEFAULT 'x';
                CREATE MATERIALIZED VIEW "{{schema}}"."MatView" AS SELECT "Serial", "Extra" FROM "{{schema}}"."Parent";
                CREATE FUNCTION "{{schema}}"."Describe"("M" "{{schema}}"."Mood") RETURNS text LANGUAGE sql AS $$ SELECT "M"::text $$;
                CREATE FUNCTION "{{schema}}"."DescribeAll"("Ms" "{{schema}}"."Mood"[]) RETURNS "{{schema}}"."Mood" LANGUAGE sql AS $$ SELECT "Ms"[1] $$;
                CREATE PROCEDURE "{{schema}}"."DoProc"("Arg" "{{schema}}"."Mood") LANGUAGE sql AS $$ SELECT 1 $$;
                COMMENT ON VIEW "{{schema}}"."MatView" IS 'view';
                COMMENT ON TYPE "{{schema}}"."Mood" IS 'type';
                COMMENT ON SEQUENCE "{{schema}}"."Parent_Serial_seq" IS 'seq';
                COMMENT ON PROCEDURE "{{schema}}"."DoProc"("{{schema}}"."Mood") IS 'proc';
                GRANT EXECUTE ON FUNCTION "{{schema}}"."Describe"("{{schema}}"."Mood") TO "{{role}}";
                GRANT USAGE ON SCHEMA "{{schema}}" TO "{{role}}";
                """;

            async Task<long> Count(string sql)
            {
                await using var command = new NpgsqlCommand(sql, connection);
                return Convert.ToInt64(await command.ExecuteScalarAsync());
            }

            var target = Build(source);
            Assert.IsFalse(target.Diagnostics.Any(static d => d.Severity == SemanticDiagnosticSeverity.Error), string.Join("; ", target.Diagnostics.Select(static d => d.Message)));
            var script = await PublishAsync(connection, DatabaseModel.Empty, target);

            var ns = $"'\"{schema}\"'::regnamespace";
            Assert.AreEqual(1L, await Count($"SELECT count(*) FROM pg_type WHERE typnamespace = {ns} AND typtype = 'c' AND typname = 'Pair'"), script);
            Assert.AreEqual(1L, await Count($"SELECT count(*) FROM pg_type WHERE typnamespace = {ns} AND typtype = 'r' AND typname = 'IntSpan'"));
            Assert.AreEqual(2L, await Count($"SELECT count(*) FROM pg_attribute WHERE attrelid = '\"{schema}\".\"Pair\"'::regclass AND attname IN ('First', 'Second')"));
            Assert.AreEqual(7L, await Count($"SELECT count(*) FROM pg_attribute WHERE attrelid = '\"{schema}\".\"Parent\"'::regclass AND attnum > 0 AND attname IN ('Serial', 'Ident', 'Mood', 'Pair', 'Span', 'Moods', 'Extra')"));
            Assert.AreEqual(1L, await Count($"SELECT count(*) FROM pg_class WHERE relnamespace = {ns} AND relkind = 'm' AND relname = 'MatView'"));
            Assert.AreEqual(2L, await Count($"SELECT count(*) FROM pg_proc WHERE pronamespace = {ns} AND proname IN ('Describe', 'DescribeAll') AND prokind = 'f'"));
            Assert.AreEqual(1L, await Count($"SELECT count(*) FROM pg_proc WHERE pronamespace = {ns} AND proname = 'DoProc' AND prokind = 'p'"));
            Assert.AreEqual(1L, await Count($"SELECT count(*) FROM pg_class WHERE relnamespace = {ns} AND relkind = 'S' AND relname = 'Parent_Serial_seq'"));
            Assert.AreEqual(1L, await Count($"SELECT count(*) FROM pg_description WHERE objoid = '\"{schema}\".\"MatView\"'::regclass AND description = 'view'"));
            Assert.AreEqual(1L, await Count($"SELECT count(*) FROM pg_description WHERE objoid = '\"{schema}\".\"Mood\"'::regtype AND description = 'type'"));
            Assert.AreEqual(true, await Scalar($"SELECT has_function_privilege('{role}', '\"{schema}\".\"Describe\"(\"{schema}\".\"Mood\")', 'EXECUTE')"));

            var live = await LoadAsync(connection, schema);
            await AssertConverged(connection, live, target, "after the first publish");
            var again = await PublishAsync(connection, live, target);
            Assert.AreEqual(string.Empty, again.Trim(), "A second publish must have nothing to do: " + again);

            var reduced = Build($"CREATE SCHEMA \"{schema}\";");
            var removal = await PublishAsync(connection, await LoadAsync(connection, schema), reduced);
            Assert.AreEqual(0L, await Count($"SELECT count(*) FROM pg_class WHERE relnamespace = {ns}"), removal);
            Assert.AreEqual(0L, await Count($"SELECT count(*) FROM pg_proc WHERE pronamespace = {ns}"), removal);

            async Task<object?> Scalar(string sql)
            {
                await using var command = new NpgsqlCommand(sql, connection);
                return await command.ExecuteScalarAsync();
            }
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE;");
            await ExecuteAsync(connection, $"DROP OWNED BY \"{role}\"; DROP ROLE IF EXISTS \"{role}\";");
        }
    }

    [TestMethod]
    public async Task NamesThatNeedQuotesForOtherReasons_AreDeployedAsWritten()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var schema = $"weird_{suffix}";
        await using var connection = await OpenConnectionAsync();
        try
        {
            // A space, an embedded quote, and lower-case words PostgreSQL reserves: each must stay quoted wherever pgproj writes it,
            // including in the CHECK, index, view and trigger text it regenerates.
            var source = $$""""
                CREATE SCHEMA {{schema}};
                CREATE TABLE {{schema}}."user" (
                    id integer PRIMARY KEY,
                    "order" integer CHECK ("order" > 0),
                    "Col Name" text,
                    "say ""hi""" integer,
                    "select" integer DEFAULT 1);
                CREATE INDEX "my index" ON {{schema}}."user" ("order", lower("Col Name"), "say ""hi""") WHERE "select" > 0;
                CREATE VIEW {{schema}}."group" AS SELECT id, "order", "Col Name" FROM {{schema}}."user" WHERE "select" > 0;
                CREATE FUNCTION {{schema}}."do it"("from" integer, "to" integer) RETURNS integer LANGUAGE sql AS $$ SELECT "from" + "to" $$;
                CREATE FUNCTION {{schema}}.tf() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$;
                CREATE TRIGGER "on" AFTER UPDATE OF "order", "Col Name" ON {{schema}}."user" FOR EACH ROW WHEN (new."order" IS DISTINCT FROM old."order") EXECUTE FUNCTION {{schema}}.tf();
                """";

            async Task<long> Count(string sql)
            {
                await using var command = new NpgsqlCommand(sql, connection);
                return Convert.ToInt64(await command.ExecuteScalarAsync());
            }

            var target = Build(source);
            Assert.IsFalse(target.Diagnostics.Any(static d => d.Severity == SemanticDiagnosticSeverity.Error), string.Join("; ", target.Diagnostics.Select(static d => d.Message)));
            var script = await PublishAsync(connection, DatabaseModel.Empty, target);

            Assert.AreEqual(1L, await Count($"SELECT count(*) FROM pg_class WHERE relnamespace = '{schema}'::regnamespace AND relname = 'user' AND relkind = 'r'"), script);
            Assert.AreEqual(1L, await Count($"SELECT count(*) FROM pg_class WHERE relnamespace = '{schema}'::regnamespace AND relname = 'group' AND relkind = 'v'"));
            Assert.AreEqual(5L, await Count($"SELECT count(*) FROM pg_attribute WHERE attrelid = '{schema}.\"user\"'::regclass AND attname IN ('id', 'order', 'Col Name', 'say \"hi\"', 'select')"));
            Assert.AreEqual(1L, await Count($"SELECT count(*) FROM pg_indexes WHERE schemaname = '{schema}' AND indexname = 'my index'"));
            Assert.AreEqual(1L, await Count($"SELECT count(*) FROM pg_proc WHERE pronamespace = '{schema}'::regnamespace AND proname = 'do it'"));
            Assert.AreEqual(1L, await Count($"SELECT count(*) FROM pg_trigger WHERE tgname = 'on' AND NOT tgisinternal"));

            var live = await LoadAsync(connection, schema);
            await AssertConverged(connection, live, target, "after the first publish");
            var again = await PublishAsync(connection, live, target);
            Assert.AreEqual(string.Empty, again.Trim(), "A second publish must have nothing to do: " + again);

            var removal = await PublishAsync(connection, await LoadAsync(connection, schema), Build($"CREATE SCHEMA {schema};"));
            Assert.AreEqual(0L, await Count($"SELECT count(*) FROM pg_class WHERE relnamespace = '{schema}'::regnamespace"), removal);
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {schema} CASCADE;");
        }
    }

    private static async Task AssertConverged(NpgsqlConnection connection, DatabaseModel live, DatabaseModel target, string step)
    {
        var reDiff = await EndToEndSupport.DiffAsPublishedAsync(connection, live, target);
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
