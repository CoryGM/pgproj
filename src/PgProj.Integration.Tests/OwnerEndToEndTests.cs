using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-146 against a real server. A project states the owners it chooses to with <c>ALTER ... OWNER TO</c>: they are deployed after the object exists, read back
/// from the catalogs, and converge; a changed owner needs <c>--allow-destructive</c>; an owner the project does not state is never compared; <c>init</c> writes
/// each beside its object; <c>excludeOwners</c> leaves them out on both sides; and a role the server lacks is refused before anything runs (G-147). Roles are
/// cluster-wide, so each test names its own and drops them last.
/// </summary>
[TestClass]
public sealed class OwnerEndToEndTests
{
    /// <summary>One object of each kind that has an owner to set. <c>{O}</c> owns all but the event trigger (its owner must be a superuser, <c>{S}</c>); two objects state none.</summary>
    private const string Kinds = """
        CREATE SCHEMA app AUTHORIZATION {O};
        CREATE TABLE app.t (id integer PRIMARY KEY, n varchar(10));
        ALTER TABLE app.t OWNER TO {O};
        CREATE VIEW app.v AS SELECT id, n FROM app.t;
        ALTER VIEW app.v OWNER TO {O};
        CREATE MATERIALIZED VIEW app.mv AS SELECT id FROM app.t;
        ALTER MATERIALIZED VIEW app.mv OWNER TO {O};
        CREATE SEQUENCE app.s;
        ALTER SEQUENCE app.s OWNER TO {O};
        CREATE FUNCTION app.f(a integer) RETURNS integer LANGUAGE sql AS $$ SELECT a $$;
        ALTER FUNCTION app.f(integer) OWNER TO {O};
        CREATE PROCEDURE app.p() LANGUAGE sql AS $$ SELECT 1 $$;
        ALTER PROCEDURE app.p() OWNER TO {O};
        CREATE TYPE app.mood AS ENUM ('a', 'b');
        ALTER TYPE app.mood OWNER TO {O};
        CREATE DOMAIN app.d AS integer;
        ALTER DOMAIN app.d OWNER TO {O};
        CREATE AGGREGATE app.agg(integer) (SFUNC = int4pl, STYPE = integer, INITCOND = '0');
        ALTER AGGREGATE app.agg(integer) OWNER TO {O};
        CREATE FUNCTION app.opf(integer, integer) RETURNS integer LANGUAGE sql AS $$ SELECT $1 + $2 $$;
        CREATE OPERATOR app.+#+ (LEFTARG = integer, RIGHTARG = integer, FUNCTION = app.opf);
        ALTER OPERATOR app.+#+ (integer, integer) OWNER TO {O};
        CREATE FUNCTION app.evt() RETURNS event_trigger LANGUAGE plpgsql AS $$ BEGIN NULL; END $$;
        CREATE EVENT TRIGGER {EVT} ON ddl_command_end EXECUTE FUNCTION app.evt();
        ALTER EVENT TRIGGER {EVT} OWNER TO {S};
        CREATE PUBLICATION {PUB} FOR TABLE app.t;
        ALTER PUBLICATION {PUB} OWNER TO {O};
        """;

    /// <summary>The owner of each of those objects, one string per object, in a fixed order; the event trigger and publication are named in <c>{EVT}</c> and <c>{PUB}</c>.</summary>
    private const string OwnersQuery = """
        SELECT string_agg(x, '|') FROM (
            SELECT 'schema:' || pg_get_userbyid(nspowner) AS x FROM pg_namespace WHERE nspname = 'app'
            UNION ALL SELECT relkind::text || ':' || relname || ':' || pg_get_userbyid(relowner) FROM pg_class WHERE relnamespace = 'app'::regnamespace AND relkind IN ('r', 'v', 'm', 'S')
            UNION ALL SELECT 'proc:' || proname || ':' || pg_get_userbyid(proowner) FROM pg_proc WHERE pronamespace = 'app'::regnamespace
            UNION ALL SELECT 'type:' || typname || ':' || pg_get_userbyid(typowner) FROM pg_type WHERE typnamespace = 'app'::regnamespace AND typtype IN ('e', 'd')
            UNION ALL SELECT 'op:' || oprname || ':' || pg_get_userbyid(oprowner) FROM pg_operator WHERE oprnamespace = 'app'::regnamespace
            UNION ALL SELECT 'evt:' || evtname || ':' || pg_get_userbyid(evtowner) FROM pg_event_trigger WHERE evtname = '{EVT}'
            UNION ALL SELECT 'pub:' || pubname || ':' || pg_get_userbyid(pubowner) FROM pg_publication WHERE pubname = '{PUB}') s;
        """;

    [TestMethod]
    public async Task EveryKindOfOwnerIsDeployedReadBackConvergesAndRoundTripsThroughInit()
    {
        var names = new Names("a");
        var databaseName = await CreateDatabaseAsync("g146a");
        var scaffoldDatabaseName = await CreateDatabaseAsync("g146as");
        var projectDirectory = CreateProjectDirectory("g146a");
        try
        {
            await CreateRolesAsync(names);
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, names.Apply(Kinds));

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-superuser-statements");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            StringAssert.Contains(published.Output, "[Info] 2 objects state no owner (ALTER ... OWNER TO); their owners are not compared.");

            var expected = names.Expected();
            CollectionAssert.AreEqual(expected, await OwnersAsync(connectionString, names), "Every stated owner was set; the two objects that state none are owned by the deploying role.");
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // A second publish changes nothing, and an owner the project does not state is never compared: change the live one of an unstated function.
            await ExecuteAsync(connectionString, $"ALTER FUNCTION app.opf(integer, integer) OWNER TO {names.Owner};");
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
            var again = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-superuser-statements");
            Assert.AreEqual(0, again.ExitCode, again.Output);
            Assert.AreEqual(names.Owner, await ScalarTextAsync(connectionString, "SELECT pg_get_userbyid(proowner) FROM pg_proc WHERE proname = 'opf';"), "Left alone.");
            await ExecuteAsync(connectionString, "ALTER FUNCTION app.opf(integer, integer) OWNER TO postgres;");

            // init writes each owner beside its object; the scaffold builds without a diagnostic, matches the database, and deploys the same owners elsewhere.
            var scaffoldDirectory = Path.Combine(projectDirectory, "scaffold");
            var scaffolded = await RunAsync("init", "--source-connection", connectionString, "--output", scaffoldDirectory, "--project", Path.Combine(scaffoldDirectory, "pgproj.json"));
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            StringAssert.Contains(await File.ReadAllTextAsync(Path.Combine(scaffoldDirectory, "Schema", "app", "Table", "t.sql")), $"ALTER TABLE app.t OWNER TO {names.Owner};");
            StringAssert.Contains(await File.ReadAllTextAsync(Path.Combine(scaffoldDirectory, "EventTrigger", $"{names.Event}.sql")), $"ALTER EVENT TRIGGER {names.Event} OWNER TO {names.Super};");
            StringAssert.Contains(await File.ReadAllTextAsync(Path.Combine(scaffoldDirectory, "Schema", "app", "app.sql")), $"ALTER SCHEMA app OWNER TO {names.Owner};");
            var built = await RunAsync("build", "--project", Path.Combine(scaffoldDirectory, "pgproj.json"));
            Assert.AreEqual(0, built.ExitCode, built.Output);
            StringAssert.Contains(built.Output, "No diagnostics.");
            await AssertDiffIsEmptyAsync(Path.Combine(scaffoldDirectory, "pgproj.json"), connectionString);

            var scaffoldConnectionString = ConnectionStringFor(scaffoldDatabaseName);
            var deployed = await RunAsync("publish", "--project", Path.Combine(scaffoldDirectory, "pgproj.json"), "--target-connection", scaffoldConnectionString, "--execute", "--allow-superuser-statements");
            Assert.AreEqual(0, deployed.ExitCode, deployed.Output);
            CollectionAssert.AreEqual(expected, await OwnersAsync(scaffoldConnectionString, names));
            await AssertDiffIsEmptyAsync(Path.Combine(scaffoldDirectory, "pgproj.json"), scaffoldConnectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            await DropDatabaseAsync(scaffoldDatabaseName);
            await DropRolesAsync(names);
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task AChangedOwnerIsRefusedUntilAllowDestructiveAndNothingElseIsHeldUp()
    {
        var names = new Names("b");
        var databaseName = await CreateDatabaseAsync("g146b");
        var projectDirectory = CreateProjectDirectory("g146b");
        try
        {
            await CreateRolesAsync(names);
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, "CREATE SCHEMA app;\nCREATE TABLE app.t (id integer);\nALTER TABLE app.t OWNER TO " + names.Other + ";\n");
            Assert.AreEqual(0, (await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-superuser-statements")).ExitCode);

            // The project now says another role owns it.
            WriteSchema(projectDirectory, "CREATE SCHEMA app;\nCREATE TABLE app.t (id integer);\nALTER TABLE app.t OWNER TO " + names.Owner + ";\n");
            var diffed = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString);
            StringAssert.Contains(diffed.Output, $"Owner: '{names.Other}' -> '{names.Owner}'");
            var script = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--dry-run");
            StringAssert.Contains(script.Output, $"ALTER TABLE \"app\".\"t\" OWNER TO \"{names.Owner}\";");

            var refused = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-superuser-statements");
            Assert.AreEqual(1, refused.ExitCode, refused.Output);
            StringAssert.Contains(refused.Output, "can discard data and were not executed");
            StringAssert.Contains(refused.Output, $"owner of table app.t changes from {names.Other} to {names.Owner}; {names.Other} loses the privileges it had as the owner");
            Assert.AreEqual(names.Other, await ScalarTextAsync(connectionString, "SELECT pg_get_userbyid(relowner) FROM pg_class WHERE oid = 'app.t'::regclass;"), "Nothing was changed.");

            var allowed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-destructive", "--allow-superuser-statements");
            Assert.AreEqual(0, allowed.ExitCode, allowed.Output);
            Assert.AreEqual(names.Owner, await ScalarTextAsync(connectionString, "SELECT pg_get_userbyid(relowner) FROM pg_class WHERE oid = 'app.t'::regclass;"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            await DropRolesAsync(names);
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task AnOwnerTheProjectDoesNotStateIsNeverComparedAndANewObjectIsOwnedByTheDeployer()
    {
        var names = new Names("c");
        var databaseName = await CreateDatabaseAsync("g146c");
        var projectDirectory = CreateProjectDirectory("g146c");
        try
        {
            await CreateRolesAsync(names);
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);

            // A project that states no owner at all (every project before G-146): nothing is said about owners, and the database's are not looked at.
            WriteSchema(projectDirectory, "CREATE SCHEMA app;\nCREATE TABLE app.t (id integer);\nCREATE TABLE app.u (id integer);\n");
            var first = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-superuser-statements");
            Assert.AreEqual(0, first.ExitCode, first.Output);
            Assert.IsFalse(first.Output.Contains("state no owner", StringComparison.Ordinal), first.Output);
            await ExecuteAsync(connectionString, $"ALTER TABLE app.t OWNER TO {names.Other}; ALTER TABLE app.u OWNER TO {names.Owner}; ALTER SCHEMA app OWNER TO {names.Other};");
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // Stating one owner manages that one and says how many it leaves out; the rest are still not compared.
            WriteSchema(projectDirectory, $"CREATE SCHEMA app;\nCREATE TABLE app.t (id integer);\nCREATE TABLE app.u (id integer);\nALTER TABLE app.u OWNER TO {names.Owner};\n");
            var second = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-superuser-statements");
            Assert.AreEqual(0, second.ExitCode, second.Output);
            StringAssert.Contains(second.Output, "2 objects state no owner");
            Assert.AreEqual(names.Other, await ScalarTextAsync(connectionString, "SELECT pg_get_userbyid(relowner) FROM pg_class WHERE oid = 'app.t'::regclass;"), "Not stated, so not touched.");

            // A new object with no stated owner is owned by whoever deploys, as it always was; one with a stated owner is created and then handed over.
            WriteSchema(projectDirectory, $"CREATE SCHEMA app;\nCREATE TABLE app.t (id integer);\nCREATE TABLE app.u (id integer);\nALTER TABLE app.u OWNER TO {names.Owner};\nCREATE TABLE app.plain (id integer);\nCREATE TABLE app.handed (id integer);\nALTER TABLE app.handed OWNER TO {names.Other};\n");
            var third = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-superuser-statements");
            Assert.AreEqual(0, third.ExitCode, third.Output);
            Assert.AreEqual("postgres", await ScalarTextAsync(connectionString, "SELECT pg_get_userbyid(relowner) FROM pg_class WHERE oid = 'app.plain'::regclass;"));
            Assert.AreEqual(names.Other, await ScalarTextAsync(connectionString, "SELECT pg_get_userbyid(relowner) FROM pg_class WHERE oid = 'app.handed'::regclass;"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            await DropRolesAsync(names);
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task ARecreatedViewIsOwnedByTheRoleTheProjectStatesForIt()
    {
        var names = new Names("d");
        var databaseName = await CreateDatabaseAsync("g146d");
        var projectDirectory = CreateProjectDirectory("g146d");
        try
        {
            await CreateRolesAsync(names);
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            const string Sql = "CREATE SCHEMA app;\nCREATE TABLE app.t (id integer PRIMARY KEY, n varchar(10));\nCREATE VIEW app.v AS SELECT id, n FROM app.t;\nALTER VIEW app.v OWNER TO {O};\n";
            WriteSchema(projectDirectory, Edit(Sql, "{O}", names.Owner));
            Assert.AreEqual(0, (await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-superuser-statements")).ExitCode);

            // The column the view reads changes type, so the view is dropped and created again around it (G-103); the project's owner is set on the new one.
            WriteSchema(projectDirectory, Edit(Edit(Sql, "{O}", names.Owner), "n varchar(10)", "n varchar(20)"));
            var script = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--dry-run");
            StringAssert.Contains(script.Output, "DROP VIEW \"app\".\"v\";");
            var owned = script.Output.Split('\n').Count(static line => line.StartsWith("ALTER VIEW \"app\".\"v\" OWNER TO", StringComparison.Ordinal));
            Assert.AreEqual(1, owned, "Set once.\n" + script.Output);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-destructive", "--allow-superuser-statements");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            Assert.AreEqual(names.Owner, await ScalarTextAsync(connectionString, "SELECT pg_get_userbyid(relowner) FROM pg_class WHERE oid = 'app.v'::regclass;"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            await DropRolesAsync(names);
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task ExcludeOwnersLeavesOwnersOutOnBothSidesAndInitWritesTheField()
    {
        var names = new Names("e");
        var databaseName = await CreateDatabaseAsync("g146e");
        var projectDirectory = CreateProjectDirectory("g146e");
        try
        {
            await CreateRolesAsync(names);
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, "CREATE SCHEMA app;\nCREATE TABLE app.t (id integer);\nALTER TABLE app.t OWNER TO " + names.Owner + ";\n");
            Assert.AreEqual(0, (await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-superuser-statements")).ExitCode);

            // The database's owner differs from the project's; with owners left out neither side is looked at.
            await ExecuteAsync(connectionString, $"ALTER TABLE app.t OWNER TO {names.Other};");
            StringAssert.Contains((await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString)).Output, "Modified: 1");
            var excluded = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString, "--exclude-owners");
            StringAssert.Contains(excluded.Output, "Modified: 0");
            Assert.IsFalse(excluded.Output.Contains("state no owner", StringComparison.Ordinal), excluded.Output);
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--exclude-owners");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            Assert.AreEqual(names.Other, await ScalarTextAsync(connectionString, "SELECT pg_get_userbyid(relowner) FROM pg_class WHERE oid = 'app.t'::regclass;"), "Not changed.");

            // init --exclude-owners scaffolds no owner statement and writes the field, so later runs keep matching.
            var scaffoldDirectory = Path.Combine(projectDirectory, "scaffold");
            var scaffolded = await RunAsync("init", "--source-connection", connectionString, "--output", scaffoldDirectory, "--project", Path.Combine(scaffoldDirectory, "pgproj.json"), "--exclude-owners");
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            Assert.IsFalse((await File.ReadAllTextAsync(Path.Combine(scaffoldDirectory, "Schema", "app", "Table", "t.sql"))).Contains("OWNER TO", StringComparison.Ordinal));
            StringAssert.Contains(await File.ReadAllTextAsync(Path.Combine(scaffoldDirectory, "pgproj.json")), "\"excludeOwners\": true");
            await AssertDiffIsEmptyAsync(Path.Combine(scaffoldDirectory, "pgproj.json"), connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            await DropRolesAsync(names);
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task AnOwnerRoleTheServerLacksIsRefusedBeforeAnythingRunsUnlessThePreDeployScriptCreatesIt()
    {
        var names = new Names("f");
        var databaseName = await CreateDatabaseAsync("g146f");
        var projectDirectory = CreateProjectDirectory("g146f", ", \"predeploy\": \"./pre.sql\"");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, "CREATE SCHEMA app;\nCREATE TABLE app.t (id integer);\nALTER TABLE app.t OWNER TO " + names.Owner + ";\n");

            // No such role on the server and no pre-deploy script that makes it.
            await File.WriteAllTextAsync(Path.Combine(projectDirectory, "pre.sql"), "SELECT 1;\n");
            var refused = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-superuser-statements");
            Assert.AreEqual(1, refused.ExitCode, refused.Output);
            StringAssert.Contains(refused.Output, $"role \"{names.Owner}\", named by table app.t (owner)");
            // The code as the server's error prints it ("42704: role ... does not exist"); the bare digits can be in the temporary folder's name (G-301).
            Assert.IsFalse(refused.Output.Contains("42704:", StringComparison.Ordinal), "The server was never asked.\n" + refused.Output);
            Assert.AreEqual("0", await ScalarTextAsync(connectionString, "SELECT count(*) FROM pg_namespace WHERE nspname = 'app';"), "Nothing was run.");

            // The script creates it, the documented way: the same project deploys, and the table is owned by the role.
            await File.WriteAllTextAsync(Path.Combine(projectDirectory, "pre.sql"), $"DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{names.Owner}') THEN CREATE ROLE {names.Owner} NOLOGIN; END IF; END $$;\n");
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-superuser-statements");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            Assert.AreEqual(names.Owner, await ScalarTextAsync(connectionString, "SELECT pg_get_userbyid(relowner) FROM pg_class WHERE oid = 'app.t'::regclass;"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            await DropRolesAsync(names);
            TryDeleteDirectory(projectDirectory);
        }
    }

    private static async Task CreateRolesAsync(Names names)
        => await ExecuteAsync(
            PostgresContainerFixture.ConnectionString,
            $"CREATE ROLE {names.Owner} NOLOGIN; CREATE ROLE {names.Other} NOLOGIN; CREATE ROLE {names.Super} NOLOGIN SUPERUSER;");

    private static async Task DropRolesAsync(Names names)
        => await ExecuteAsync(
            PostgresContainerFixture.ConnectionString,
            $"DROP ROLE IF EXISTS {names.Owner}; DROP ROLE IF EXISTS {names.Other}; DROP ROLE IF EXISTS {names.Super};");

    /// <summary>The owner of each object of <see cref="Kinds"/>, sorted.</summary>
    private static async Task<string[]> OwnersAsync(string connectionString, Names names)
    {
        var joined = await ScalarTextAsync(connectionString, names.ApplyToQuery(OwnersQuery));
        return joined.Split('|').Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>The role, event trigger, and publication names one test uses, so tests that run together never meet.</summary>
    private sealed record Names(string Suffix)
    {
        public string Owner => $"g146_{Suffix}_owner";

        public string Other => $"g146_{Suffix}_other";

        public string Super => $"g146_{Suffix}_super";

        public string Event => $"g146_{Suffix}_evt";

        public string Publication => $"g146_{Suffix}_pub";

        /// <summary><see cref="Kinds"/> with the names filled in; every placeholder must be there.</summary>
        public string Apply(string sql)
            => Edit(Edit(Edit(Edit(sql, "{O}", Owner), "{S}", Super), "{EVT}", Event), "{PUB}", Publication);

        /// <summary><see cref="OwnersQuery"/> with the names filled in.</summary>
        public string ApplyToQuery(string sql)
            => Edit(Edit(sql, "{EVT}", Event), "{PUB}", Publication);

        /// <summary>What <see cref="OwnersAsync"/> returns for <see cref="Kinds"/> once deployed: each stated owner, and the deploying role for the two functions that state none.</summary>
        public string[] Expected()
            => new[]
            {
                $"schema:{Owner}",
                $"r:t:{Owner}",
                $"v:v:{Owner}",
                $"m:mv:{Owner}",
                $"S:s:{Owner}",
                $"proc:f:{Owner}",
                $"proc:p:{Owner}",
                $"proc:agg:{Owner}",
                "proc:opf:postgres",
                "proc:evt:postgres",
                $"type:mood:{Owner}",
                $"type:d:{Owner}",
                $"op:+#+:{Owner}",
                $"evt:{Event}:{Super}",
                $"pub:{Publication}:{Owner}",
            }.Order(StringComparer.Ordinal).ToArray();
    }
}
