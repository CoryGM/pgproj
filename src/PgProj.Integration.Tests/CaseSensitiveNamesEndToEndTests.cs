using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-206 against a real server. Names are compared as PostgreSQL compares them: a project can declare objects that differ only by the case of a quoted
/// name (they were a duplicate-definition error), they deploy and converge and are scaffolded by <c>init</c>, and a change of case is a change.
/// </summary>
[TestClass]
public sealed class CaseSensitiveNamesEndToEndTests
{
    private const string Sql = """
        CREATE SCHEMA "S";
        CREATE SCHEMA s;
        CREATE TYPE public."Mood" AS ENUM ('ok', 'OK');
        CREATE TYPE public.mood AS ENUM ('sad');
        CREATE DOMAIN public."D" AS int;
        CREATE DOMAIN public.d AS text;
        CREATE SEQUENCE public."Q";
        CREATE SEQUENCE public.q;
        CREATE TABLE public."T" ("Id" int PRIMARY KEY, id text, m public."Mood", n public.mood, x public."D", y public.d, v int DEFAULT nextval('public."Q"'), w int DEFAULT nextval('public.q'));
        CREATE TABLE public.t ("Id" text, id int PRIMARY KEY);
        CREATE TABLE "S"."T" (a int);
        CREATE TABLE s.t (a int);
        CREATE TABLE public.c ("TId" int REFERENCES public."T" ("Id"), tid int REFERENCES public.t (id));
        CREATE INDEX "T_ix" ON public."T" ("Id");
        CREATE INDEX t_ix ON public."T" (id);
        CREATE VIEW public."V" AS SELECT "Id" FROM public."T";
        CREATE VIEW public.v AS SELECT id FROM public.t;
        CREATE FUNCTION public."F"(m public."Mood") RETURNS int LANGUAGE sql AS $$ SELECT 1 $$;
        CREATE FUNCTION public.f(m public.mood) RETURNS int LANGUAGE sql AS $$ SELECT 2 $$;
        CREATE FUNCTION public.f(m public."Mood") RETURNS int LANGUAGE sql AS $$ SELECT 3 $$;
        CREATE FUNCTION public.trg() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$;
        CREATE TRIGGER "Tr" BEFORE INSERT ON public."T" FOR EACH ROW EXECUTE FUNCTION public.trg();
        CREATE TRIGGER tr BEFORE INSERT ON public."T" FOR EACH ROW EXECUTE FUNCTION public.trg();
        ALTER TABLE public."T" ENABLE ROW LEVEL SECURITY;
        CREATE POLICY "Pol" ON public."T" USING ("Id" > 0);
        CREATE POLICY pol ON public."T" USING (id <> '');
        CREATE STATISTICS public."St" ON "Id", id FROM public."T";
        CREATE STATISTICS public.st ON "Id", id FROM public.t;
        COMMENT ON TABLE public."T" IS 'upper';
        COMMENT ON TABLE public.t IS 'lower';
        """;

    [TestMethod]
    public async Task ObjectsDifferingOnlyByCaseDeployAndConverge_AndACaseChangeIsDeployed()
    {
        var databaseName = await CreateDatabaseAsync("g206");
        var projectDirectory = CreateProjectDirectory("g206");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g206-init-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Sql);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual(
                "S|T public|T public|c public|t s|t",
                await ScalarTextAsync(connectionString, "SELECT string_agg(n.nspname || '|' || c.relname, ' ' ORDER BY n.nspname COLLATE \"C\", c.relname COLLATE \"C\") FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE c.relkind = 'r' AND n.nspname IN ('public', 'S', 's');"));
            Assert.AreEqual("F(m \"Mood\") f(m \"Mood\") f(m mood) trg()", await ScalarTextAsync(connectionString, "SELECT string_agg(proname || '(' || pg_get_function_identity_arguments(oid) || ')', ' ' ORDER BY proname COLLATE \"C\", pg_get_function_identity_arguments(oid) COLLATE \"C\") FROM pg_proc WHERE pronamespace = 'public'::regnamespace;"));
            Assert.AreEqual("ok,OK", await ScalarTextAsync(connectionString, "SELECT string_agg(enumlabel::text, ',' ORDER BY enumsortorder) FROM pg_enum WHERE enumtypid = 'public.\"Mood\"'::regtype;"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            await AssertDiffIsEmptyAsync(scaffoldProject, connectionString);

            // A change of case is a change: "S"."T" becomes "S".t, another table.
            WriteSchema(projectDirectory, Edit(Sql, "CREATE TABLE \"S\".\"T\" (a int);", "CREATE TABLE \"S\".t (a int);"));
            var changed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-destructive");
            Assert.AreEqual(0, changed.ExitCode, "publish --execute failed.\n" + changed.Output);
            Assert.AreEqual("t", await ScalarTextAsync(connectionString, "SELECT string_agg(relname, ',') FROM pg_class WHERE relkind = 'r' AND relnamespace = '\"S\"'::regnamespace;"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
            TryDeleteDirectory(scaffoldDirectory);
        }
    }
}
