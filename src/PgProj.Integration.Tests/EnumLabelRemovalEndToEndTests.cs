using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-191 against a real server (the user's decisions of 2026-11-09). An enum type that loses or reorders a label while columns use it is rebuilt under them:
/// the old type renamed out of the way, the new one created under its own name, each column moved through its text, the old type dropped, with the
/// column's default, a check, a partial index, and a view on it taken out of the way and put back. A removed label needs <c>--allow-destructive</c>, and a
/// row that holds it stops the publish in words; a reorder does not need consent; a label marked <c>-- @renamed-label</c> is renamed in place. G-190
/// refused all of this, and these tests replace its test of that refusal.
/// </summary>
[TestClass]
public sealed class EnumLabelRemovalEndToEndTests
{
    private const string Sql = """
        CREATE TYPE public.mood AS ENUM ('sad', 'ok', 'happy');
        CREATE TABLE public.m (
            id int PRIMARY KEY,
            cur public.mood NOT NULL DEFAULT 'sad',
            past public.mood[],
            CONSTRAINT m_not_happy_low CHECK (cur <> 'happy' OR id > 0)
        );
        CREATE INDEX m_cur ON public.m (cur);
        CREATE INDEX m_not_sad ON public.m (id) WHERE cur <> 'sad';
        CREATE VIEW public.v AS SELECT id, cur FROM public.m;
        """;

    private const string Labels = "SELECT string_agg(enumlabel, ',' ORDER BY enumsortorder) FROM pg_enum WHERE enumtypid = 'public.mood'::regtype";

    private const string OldTypeLeft = "SELECT count(*)::text FROM pg_type WHERE typname = 'mood_pgproj_old'";

    [TestMethod]
    public async Task ARemovedLabelIsRebuiltUnderItsColumnsWithConsentAndConverges()
    {
        await WithProjectAsync("g191rm", async (connectionString, projectDirectory, projectFile) =>
        {
            await ExecuteAsync(connectionString, "INSERT INTO public.m VALUES (1, 'sad', '{sad,happy}'), (2, 'happy', NULL);");
            WriteSchema(projectDirectory, Edit(Sql, "('sad', 'ok', 'happy')", "('sad', 'happy')"));

            var dryRun = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--dry-run");
            StringAssert.Contains(dryRun.Output, "ALTER TYPE \"public\".\"mood\" RENAME TO \"mood_pgproj_old\";");
            StringAssert.Contains(dryRun.Output, "CREATE TYPE \"public\".\"mood\" AS ENUM ('sad', 'happy');", "created under its own name, with no rename after");
            StringAssert.Contains(dryRun.Output, "ALTER COLUMN cur TYPE (enum public.mood rebuilt)", "the rewrite is warned about");

            var refused = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(1, refused.ExitCode, refused.Output);
            StringAssert.Contains(refused.Output, "enum label 'ok' removed while columns use the type");
            Assert.AreEqual("sad,ok,happy", await ScalarTextAsync(connectionString, Labels), "nothing ran");

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-destructive");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            Assert.AreEqual("sad,happy", await ScalarTextAsync(connectionString, Labels));
            Assert.AreEqual("1:sad:{sad,happy}|2:happy:", await ScalarTextAsync(connectionString, "SELECT string_agg(id || ':' || cur || ':' || coalesce(past::text, ''), '|' ORDER BY id) FROM public.m"), "the rows came through");
            Assert.AreEqual("0", await ScalarTextAsync(connectionString, OldTypeLeft));
            StringAssert.StartsWith(await ScalarTextAsync(connectionString, "SELECT pg_get_expr(adbin, adrelid) FROM pg_attrdef WHERE adrelid = 'public.m'::regclass"), "'sad'::", "the default was put back");
            Assert.AreEqual("m_cur,m_not_sad,m_pkey", await ScalarTextAsync(connectionString, "SELECT string_agg(indexrelid::regclass::text, ',' ORDER BY indexrelid::regclass::text) FROM pg_index WHERE indrelid = 'public.m'::regclass AND indisvalid"));
            Assert.AreEqual("t", await ScalarTextAsync(connectionString, "SELECT convalidated::text::char FROM pg_constraint WHERE conname = 'm_not_happy_low'"));
            Assert.AreEqual("2", await ScalarTextAsync(connectionString, "SELECT count(*)::text FROM public.v"));

            await AssertDiffIsEmptyAsync(projectFile, connectionString);
            var again = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--dry-run");
            Assert.DoesNotContain("pgproj_old", again.Output, "a second publish has nothing to rebuild");
        });
    }

    [TestMethod]
    public async Task ARowHoldingARemovedLabelStopsThePublishInWords()
    {
        await WithProjectAsync("g191row", async (connectionString, projectDirectory, projectFile) =>
        {
            await ExecuteAsync(connectionString, "INSERT INTO public.m VALUES (1, 'ok', NULL);");
            WriteSchema(projectDirectory, Edit(Sql, "('sad', 'ok', 'happy')", "('sad', 'happy')"));

            var failed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-destructive");

            Assert.AreEqual(1, failed.ExitCode, failed.Output);
            StringAssert.Contains(failed.Output, "the column public.m.cur holds a label the enum type public.mood no longer has, so the type was not rebuilt");
            StringAssert.Contains(failed.Output, "Change or delete those rows first");
            Assert.AreEqual("sad,ok,happy", await ScalarTextAsync(connectionString, Labels), "rolled back");
            Assert.AreEqual("0", await ScalarTextAsync(connectionString, OldTypeLeft));
            Assert.AreEqual("1:ok", await ScalarTextAsync(connectionString, "SELECT id || ':' || cur FROM public.m"));
        });
    }

    [TestMethod]
    public async Task AReorderIsRebuiltWithoutConsent()
    {
        await WithProjectAsync("g191ord", async (connectionString, projectDirectory, projectFile) =>
        {
            await ExecuteAsync(connectionString, "INSERT INTO public.m VALUES (1, 'happy', NULL), (2, 'sad', NULL);");
            WriteSchema(projectDirectory, Edit(Sql, "('sad', 'ok', 'happy')", "('happy', 'ok', 'sad')"));

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");

            Assert.AreEqual(0, published.ExitCode, published.Output);
            Assert.AreEqual("happy,ok,sad", await ScalarTextAsync(connectionString, Labels));
            Assert.AreEqual("1,2", await ScalarTextAsync(connectionString, "SELECT string_agg(id::text, ',' ORDER BY cur) FROM public.m"), "the new order sorts the rows");
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        });
    }

    [TestMethod]
    public async Task AnIndexWithAnExpressionOnTheColumnIsRebuiltWithTheType()
    {
        // G-345: PostgreSQL was left to rebuild the expression index during the swap and refused (42883: operator does not exist: mood = mood_pgproj_old).
        const string Index = "\nCREATE INDEX m_cur_ok ON public.m ((cur = 'ok'::public.mood));\n";
        await WithProjectAsync("g345", async (connectionString, projectDirectory, projectFile) =>
        {
            WriteSchema(projectDirectory, Sql + Index);
            var indexed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, indexed.ExitCode, indexed.Output);
            await ExecuteAsync(connectionString, "INSERT INTO public.m VALUES (1, 'ok', NULL), (2, 'sad', NULL);");
            WriteSchema(projectDirectory, Edit(Sql, "('sad', 'ok', 'happy')", "('happy', 'ok', 'sad')") + Index);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");

            Assert.AreEqual(0, published.ExitCode, published.Output);
            Assert.AreEqual("happy,ok,sad", await ScalarTextAsync(connectionString, Labels));
            Assert.AreEqual("(cur = 'ok'::mood)", await ScalarTextAsync(connectionString, "SELECT pg_get_indexdef('public.m_cur_ok'::regclass, 1, true)"));
            Assert.AreEqual("0", await ScalarTextAsync(connectionString, OldTypeLeft));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        });
    }

    [TestMethod]
    public async Task ALabelMarkedRenamedIsRenamedInPlace()
    {
        await WithProjectAsync("g191ren", async (connectionString, projectDirectory, projectFile) =>
        {
            await ExecuteAsync(connectionString, "INSERT INTO public.m VALUES (1, 'ok', '{ok,sad}');");
            WriteSchema(projectDirectory, "-- @renamed-label: ok -> fine\n" + Edit(Sql, "('sad', 'ok', 'happy')", "('sad', 'fine', 'happy')"));

            var dryRun = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--dry-run");
            StringAssert.Contains(dryRun.Output, "ALTER TYPE \"public\".\"mood\" RENAME VALUE 'ok' TO 'fine';");
            Assert.DoesNotContain("pgproj_old", dryRun.Output, "no rebuild");

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            Assert.AreEqual("1:fine:{fine,sad}", await ScalarTextAsync(connectionString, "SELECT id || ':' || cur || ':' || past::text FROM public.m"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        });
    }

    [TestMethod]
    public async Task TheRebuiltTypeKeepsTheOwnerTheProjectStates()
    {
        await WithProjectAsync("g191own", async (connectionString, projectDirectory, projectFile) =>
        {
            await ExecuteAsync(connectionString, "DO $$ BEGIN CREATE ROLE g191_type_owner; EXCEPTION WHEN duplicate_object THEN NULL; END $$;");
            WriteSchema(projectDirectory, Sql + "\nALTER TYPE public.mood OWNER TO g191_type_owner;\n");
            var owned = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-destructive");
            Assert.AreEqual(0, owned.ExitCode, owned.Output);
            WriteSchema(projectDirectory, Edit(Sql, "('sad', 'ok', 'happy')", "('happy', 'ok', 'sad')") + "\nALTER TYPE public.mood OWNER TO g191_type_owner;\n");

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");

            Assert.AreEqual(0, published.ExitCode, published.Output);
            Assert.AreEqual("g191_type_owner", await ScalarTextAsync(connectionString, "SELECT typowner::regrole::text FROM pg_type WHERE oid = 'public.mood'::regtype"));
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
