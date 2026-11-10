using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-119 against a real server (the user's decisions of 2026-11-09). A composite type that retypes a field while table columns use it is rebuilt under
/// them: the old type renamed out of the way, the new one created under its own name, each column moved with its value built field by field, by name, and
/// read through text, the old type dropped, with the column's default, a check, an expression index, and a view on it taken out of the way and put back. A
/// widening needs no consent; a narrowing needs <c>--allow-destructive</c>, and a value that does not fit stops the publish in words, nothing applied. A
/// number given fewer fractional digits is rounded, as a cast rounds it (a limit, documented).
/// </summary>
[TestClass]
public sealed class CompositeFieldRetypeEndToEndTests
{
    private const string Sql = """
        CREATE TYPE public.addr AS (street text, zip varchar(5), n int);
        COMMENT ON TYPE public.addr IS 'a postal address';
        COMMENT ON COLUMN public.addr.zip IS 'the postal code';
        CREATE TABLE public.h (
            id int PRIMARY KEY,
            a public.addr DEFAULT ROW('main', '1', 0)::public.addr,
            past public.addr[],
            CONSTRAINT h_n_positive CHECK ((a).n >= 0)
        );
        CREATE INDEX h_zip ON public.h (((a).zip));
        CREATE VIEW public.v AS SELECT id, (a).zip AS zip FROM public.h;
        """;

    private const string OldTypeLeft = "SELECT count(*)::text FROM pg_type WHERE typname IN ('addr_pgproj_old', '_addr_pgproj_old')";

    private const string Rows = "SELECT string_agg(id || ':' || coalesce(a::text, 'null') || ':' || coalesce(past::text, 'null'), '|' ORDER BY id) FROM public.h";

    private const string Data = """
        INSERT INTO public.h VALUES
            (1, ROW('high', '12345', 1), ARRAY[ROW('old', '54321', 2)::public.addr, NULL]),
            (2, NULL, '{}'),
            (3, ROW(NULL, NULL, NULL), NULL);
        """;

    [TestMethod]
    public async Task AWidenedFieldIsRebuiltUnderItsColumnsWithoutConsentAndConverges()
    {
        await WithProjectAsync("g119w", async (connectionString, projectDirectory, projectFile) =>
        {
            await ExecuteAsync(connectionString, Data);
            WriteSchema(projectDirectory, Edit(Sql, "(street text, zip varchar(5), n int)", "(street text, zip varchar(10), n bigint)"));

            var dryRun = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--dry-run");
            StringAssert.Contains(dryRun.Output, "ALTER TYPE \"public\".\"addr\" RENAME TO \"addr_pgproj_old\";");
            StringAssert.Contains(dryRun.Output, "ALTER COLUMN a TYPE (composite public.addr rebuilt)", "the rewrite is warned about");

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            Assert.AreEqual(
                "1:(high,12345,1):{\"(old,54321,2)\",NULL}|2:null:{}|3:(,,):null",
                await ScalarTextAsync(connectionString, Rows),
                "the rows came through, a null column null and a row of nulls a row of nulls");
            Assert.AreEqual("character varying(10),bigint", await ScalarTextAsync(connectionString, "SELECT string_agg(format_type(atttypid, atttypmod), ',' ORDER BY attnum) FROM pg_attribute WHERE attrelid = 'public.addr'::regclass AND attname IN ('zip', 'n')"));
            Assert.AreEqual("0", await ScalarTextAsync(connectionString, OldTypeLeft));
            StringAssert.Contains(await ScalarTextAsync(connectionString, "SELECT pg_get_expr(adbin, adrelid) FROM pg_attrdef WHERE adrelid = 'public.h'::regclass"), "main", "the default was put back");
            Assert.AreEqual("h_pkey,h_zip", await ScalarTextAsync(connectionString, "SELECT string_agg(indexrelid::regclass::text, ',' ORDER BY indexrelid::regclass::text) FROM pg_index WHERE indrelid = 'public.h'::regclass AND indisvalid"));
            Assert.AreEqual("t", await ScalarTextAsync(connectionString, "SELECT convalidated::text::char FROM pg_constraint WHERE conname = 'h_n_positive'"));
            Assert.AreEqual("12345", await ScalarTextAsync(connectionString, "SELECT zip FROM public.v WHERE id = 1"));
            Assert.AreEqual("a postal address|the postal code", await ScalarTextAsync(connectionString, "SELECT obj_description('public.addr'::regtype, 'pg_type') || '|' || col_description('public.addr'::regclass, 2)"));

            await AssertDiffIsEmptyAsync(projectFile, connectionString);
            var again = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--dry-run");
            Assert.DoesNotContain("pgproj_old", again.Output, "a second publish has nothing to rebuild");
        });
    }

    [TestMethod]
    public async Task ANarrowedFieldNeedsConsentAndAValueThatDoesNotFitStopsThePublishInWords()
    {
        await WithProjectAsync("g119n", async (connectionString, projectDirectory, projectFile) =>
        {
            await ExecuteAsync(connectionString, Data);
            WriteSchema(projectDirectory, Edit(Sql, "(street text, zip varchar(5), n int)", "(street text, zip varchar(3), n int)"));

            var refused = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(1, refused.ExitCode, refused.Output);
            StringAssert.Contains(refused.Output, "field \"zip\" of composite type public.addr changes from character varying(5) to varchar(3)");

            var failed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-destructive");
            Assert.AreEqual(1, failed.ExitCode, failed.Output);
            StringAssert.Contains(failed.Output, "the column public.h.a holds a value the composite type public.addr cannot take after the change, so the type was not rebuilt: value too long for type character varying(3)");
            StringAssert.Contains(failed.Output, "Change those rows first");
            Assert.AreEqual("1:(high,12345,1):{\"(old,54321,2)\",NULL}|2:null:{}|3:(,,):null", await ScalarTextAsync(connectionString, Rows), "rolled back, nothing cut");
            Assert.AreEqual("0", await ScalarTextAsync(connectionString, OldTypeLeft));

            await ExecuteAsync(connectionString, "UPDATE public.h SET a = ROW('high', '123', 1), past = ARRAY[ROW('old', '543', 2)::public.addr, NULL] WHERE id = 1;");
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-destructive");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            Assert.AreEqual("1:(high,123,1):{\"(old,543,2)\",NULL}|2:null:{}|3:(,,):null", await ScalarTextAsync(connectionString, Rows));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        });
    }

    [TestMethod]
    public async Task FieldsReorderedAddedAndRetypedAreMovedByNameArraysThroughAFunction()
    {
        await WithProjectAsync("g119r", async (connectionString, projectDirectory, projectFile) =>
        {
            await ExecuteAsync(connectionString, Data);
            WriteSchema(projectDirectory, Edit(Edit(Sql, "(street text, zip varchar(5), n int)", "(n bigint, extra int, zip varchar(10), street text)"), "ROW('main', '1', 0)", "ROW(0, NULL, '1', 'main')"));

            var dryRun = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--dry-run");
            StringAssert.Contains(dryRun.Output, "CREATE FUNCTION \"public\".\"addr_pgproj_move\"");

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            Assert.AreEqual(
                "1:(1,,12345,high):{\"(2,,54321,old)\",NULL}|2:null:{}|3:(,,,):null",
                await ScalarTextAsync(connectionString, Rows),
                "each field by its name, the new one null, the array's order and its null element kept");
            Assert.AreEqual("0", await ScalarTextAsync(connectionString, "SELECT count(*)::text FROM pg_proc WHERE proname = 'addr_pgproj_move'"));
            Assert.AreEqual("0", await ScalarTextAsync(connectionString, OldTypeLeft));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        });
    }

    [TestMethod]
    public async Task AMultiDimensionalArrayIsRefusedByNameNotLost()
    {
        await WithProjectAsync("g119m", async (connectionString, projectDirectory, projectFile) =>
        {
            await ExecuteAsync(connectionString, "INSERT INTO public.h VALUES (1, NULL, ARRAY[[ROW('a', '1', 1)::public.addr], [ROW('b', '2', 2)::public.addr]]);");
            WriteSchema(projectDirectory, Edit(Edit(Sql, "(street text, zip varchar(5), n int)", "(n int, zip varchar(10), street text)"), "ROW('main', '1', 0)", "ROW(0, '1', 'main')"));

            var failed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");

            Assert.AreEqual(1, failed.ExitCode, failed.Output);
            StringAssert.Contains(failed.Output, "the column public.h.past holds a multi-dimensional array, which pgproj does not move to the changed composite type public.addr");
            Assert.AreEqual("{{\"(a,1,1)\"},{\"(b,2,2)\"}}", await ScalarTextAsync(connectionString, "SELECT past::text FROM public.h"), "rolled back");
        });
    }

    [TestMethod]
    public async Task ANumberGivenFewerFractionalDigitsIsRoundedNotRefused()
    {
        const string Numbers = """
            CREATE TYPE public.amount AS (value numeric(10,3), label text);
            CREATE TABLE public.p (id int PRIMARY KEY, a public.amount);
            """;
        var databaseName = await CreateDatabaseAsync("g119num");
        var projectDirectory = CreateProjectDirectory("g119num");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Numbers);
            var first = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, first.ExitCode, first.Output);
            await ExecuteAsync(connectionString, "INSERT INTO public.p VALUES (1, ROW(12.345, 'x'));");
            WriteSchema(projectDirectory, Edit(Numbers, "numeric(10,3)", "numeric(10,2)"));

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-destructive");

            Assert.AreEqual(0, published.ExitCode, published.Output);
            Assert.AreEqual("(12.35,x)", await ScalarTextAsync(connectionString, "SELECT a::text FROM public.p"), "rounded, as a cast rounds it: the reason consent is asked");
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task TheRebuiltTypeKeepsTheOwnerTheProjectStates()
    {
        await WithProjectAsync("g119own", async (connectionString, projectDirectory, projectFile) =>
        {
            await ExecuteAsync(connectionString, "DO $$ BEGIN CREATE ROLE g119_type_owner; EXCEPTION WHEN duplicate_object THEN NULL; END $$;");
            WriteSchema(projectDirectory, Sql + "\nALTER TYPE public.addr OWNER TO g119_type_owner;\n");
            var owned = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-destructive");
            Assert.AreEqual(0, owned.ExitCode, owned.Output);
            WriteSchema(projectDirectory, Edit(Sql, "zip varchar(5)", "zip varchar(10)") + "\nALTER TYPE public.addr OWNER TO g119_type_owner;\n");

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");

            Assert.AreEqual(0, published.ExitCode, published.Output);
            Assert.AreEqual("g119_type_owner", await ScalarTextAsync(connectionString, "SELECT typowner::regrole::text FROM pg_type WHERE oid = 'public.addr'::regtype"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        });
    }

    private static async Task WithProjectAsync(string prefix, Func<string, string, string, Task> test)
    {
        var databaseName = await CreateDatabaseAsync(prefix);
        var projectDirectory = CreateProjectDirectory(prefix);
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Sql);
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            await test(connectionString, projectDirectory, projectFile);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }
}
