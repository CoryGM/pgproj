using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-231 against a real server. A fresh <c>publish</c> created an object before something it needs, because the dependency graph did not see the dependency.
/// Every object here is named so that, in the order of the names, it would come before what it needs.
/// </summary>
[TestClass]
public sealed class DependencyOrderEndToEndTests
{
    [TestMethod]
    public async Task EveryObjectIsCreatedAfterWhatItNeeds()
    {
        var major = await ServerMajorAsync();
        var databaseName = await CreateDatabaseAsync("g231");
        var projectDirectory = CreateProjectDirectory("g231");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g231-init-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            var sql = """
                -- 1: a view that casts to a project type
                CREATE VIEW public.a_cast AS SELECT max(l.mode::z_lockmodes) FROM public.z_locks l;
                CREATE TYPE public.z_lockmodes AS ENUM ('a', 'b');
                CREATE TABLE public.z_locks (mode text);
                -- 2: a view that groups by a primary key and selects another column
                CREATE VIEW public.a_group AS SELECT id, data FROM public.z_base GROUP BY id;
                CREATE TABLE public.z_base (id int PRIMARY KEY, data text);
                -- 3 and 9: a domain whose default names a sequence, and one that calls a function
                CREATE DOMAIN public.a_seq_dom AS int DEFAULT nextval('z_seq');
                CREATE SEQUENCE public.z_seq;
                CREATE DOMAIN public.a_fn_dom AS int DEFAULT z_foo(4);
                CREATE FUNCTION public.z_foo(int) RETURNS int LANGUAGE sql IMMUTABLE AS 'SELECT $1 + 1';
                -- 4: a foreign key to a partition
                CREATE TABLE public.a_ref (x int, CONSTRAINT a_ref_fk FOREIGN KEY (x) REFERENCES public.z_pk1 (a));
                CREATE TABLE public.z_pk (a int PRIMARY KEY) PARTITION BY RANGE (a);
                CREATE TABLE public.z_pk1 PARTITION OF public.z_pk FOR VALUES FROM (1) TO (100);
                -- 6: a view that selects from a sequence
                CREATE VIEW public.a_seqview AS SELECT last_value FROM public.z_seq2;
                CREATE SEQUENCE public.z_seq2;
                -- 8: a PL/pgSQL function that declares a variable of a project type
                CREATE FUNCTION public.a_declare() RETURNS void LANGUAGE plpgsql AS $$ DECLARE v public.z_declared; r public.z_rowtype%ROWTYPE; BEGIN NULL; END $$;
                CREATE TYPE public.z_declared AS ENUM ('x');
                CREATE TABLE public.z_rowtype (id int);
                -- 10: a partitioned table whose key calls a function
                CREATE TABLE public.a_part (a int, b int) PARTITION BY RANGE (a, z_plus(b));
                CREATE FUNCTION public.z_plus(int) RETURNS int LANGUAGE sql IMMUTABLE AS 'SELECT $1 + 1';
                """;
            if (major >= 15)
            {
                // 5: a publication for the tables of a schema that holds nothing (TABLES IN SCHEMA is PostgreSQL 15; G-269)
                sql += """

                    CREATE PUBLICATION a_pub FOR TABLES IN SCHEMA z_pubschema;
                    CREATE SCHEMA z_pubschema;
                    """;
            }

            if (major >= 17)
            {
                // 7: a view whose JSON_TABLE names a project domain
                sql += """

                    CREATE VIEW public.a_json AS SELECT * FROM JSON_TABLE('1'::jsonb, '$' COLUMNS (c public.z_jdom PATH '$'));
                    CREATE DOMAIN public.z_jdom AS jsonb;
                    """;
            }

            WriteSchema(projectDirectory, sql);
            var built = await RunAsync("build", "--project", projectFile);
            Assert.AreEqual(0, built.ExitCode, built.Output);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // init writes the same objects, and what it writes publishes into an empty database too.
            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            var secondDatabase = await CreateDatabaseAsync("g231b");
            try
            {
                var secondConnection = ConnectionStringFor(secondDatabase);
                var republished = await RunAsync("publish", "--project", scaffoldProject, "--target-connection", secondConnection, "--execute");
                Assert.AreEqual(0, republished.ExitCode, "publishing the scaffold failed.\n" + republished.Output);
                await AssertDiffIsEmptyAsync(scaffoldProject, secondConnection);
            }
            finally
            {
                await DropDatabaseAsync(secondDatabase);
            }
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
            TryDeleteDirectory(scaffoldDirectory);
        }
    }
}
