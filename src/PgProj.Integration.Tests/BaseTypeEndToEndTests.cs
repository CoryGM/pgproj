using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-267 against a real server: base types are modeled (the user's design of 2026-11-09), replacing G-241's tests, in which a base type and its functions were left out of a
/// project and the database had to have the type first. A project written as <c>pg_dump</c> writes one (a shell, the internal functions, the full definition) publishes behind
/// <c>--allow-superuser-statements</c> and converges; an option <c>ALTER TYPE ... SET</c> covers changes in place; another change rebuilds the type and its functions, or is
/// refused while a column uses it; a removed type is dropped with <c>CASCADE</c>, which the check before it stops when something outside the project uses the type; and
/// <c>init</c> writes the shell, the functions, and the definition in the type's file, and the scaffold builds the same type in another database.
/// </summary>
[TestClass]
public sealed class BaseTypeEndToEndTests
{
    /// <summary>A base type as <c>pg_dump</c> writes one: a shell, the internal functions, the full definition.</summary>
    private const string BaseTypeSql = """
        CREATE TYPE public.xint4;
        CREATE FUNCTION public.xint4in(cstring) RETURNS public.xint4 LANGUAGE internal IMMUTABLE STRICT AS $$int4in$$;
        CREATE FUNCTION public.xint4out(public.xint4) RETURNS cstring LANGUAGE internal IMMUTABLE STRICT AS $$int4out$$;
        CREATE TYPE public.xint4 (INTERNALLENGTH = 4, INPUT = public.xint4in, OUTPUT = public.xint4out, ALIGNMENT = int4, STORAGE = plain, PASSEDBYVALUE);

        """;

    private const string ReceiveSendSql = """
        CREATE FUNCTION public.xint4recv(internal) RETURNS public.xint4 LANGUAGE internal IMMUTABLE STRICT AS $$int4recv$$;
        CREATE FUNCTION public.xint4send(public.xint4) RETURNS bytea LANGUAGE internal IMMUTABLE STRICT AS $$int4send$$;

        """;

    private const string UsesSql = """
        CREATE CAST (integer AS public.xint4) WITHOUT FUNCTION;
        CREATE TABLE public.t (id integer PRIMARY KEY, v public.xint4);

        """;

    private const string Superuser = "--allow-superuser-statements";

    [TestMethod]
    public async Task APgDumpBaseTypePublishesOnlyWithConsentAndConverges()
    {
        var databaseName = await CreateDatabaseAsync("g267");
        var projectDirectory = CreateProjectDirectory("g267");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, BaseTypeSql + UsesSql);

            var built = await RunAsync("build", "--project", projectFile);
            Assert.AreEqual(0, built.ExitCode, built.Output);
            Assert.IsFalse(built.Output.Contains("not modeled", StringComparison.Ordinal), "A base type and its shell are modeled.\n" + built.Output);
            Assert.IsFalse(built.Output.Contains("cycle", StringComparison.OrdinalIgnoreCase), built.Output);

            var refused = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreNotEqual(0, refused.ExitCode, refused.Output);
            StringAssert.Contains(refused.Output, "only a superuser can create a base type or its shell");
            Assert.AreEqual("0", await ScalarTextAsync(connectionString, "SELECT count(*)::text FROM pg_type WHERE typname = 'xint4'"), "Nothing ran.");

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", Superuser);
            Assert.AreEqual(0, published.ExitCode, published.Output);
            Assert.AreEqual("4 t i p xint4in xint4out", await ScalarTextAsync(connectionString, "SELECT concat_ws(' ', typlen, typbyval, typalign, typstorage, typinput, typoutput) FROM pg_type WHERE oid = 'public.xint4'::regtype"));
            Assert.AreEqual("xint4", await ScalarTextAsync(connectionString, "SELECT atttypid::regtype::text FROM pg_attribute WHERE attrelid = 'public.t'::regclass AND attname = 'v'"));
            await ExecuteAsync(connectionString, "INSERT INTO public.t VALUES (1, '42'::public.xint4);");
            Assert.AreEqual("42", await ScalarTextAsync(connectionString, "SELECT v::text FROM public.t"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var again = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, again.ExitCode, "A second publish changes nothing, so it needs no consent.\n" + again.Output);

            // The same type written another way: unqualified functions, LIKE in place of the four options it copies, the defaults stated.
            WriteSchema(projectDirectory, Edit(BaseTypeSql, "CREATE TYPE public.xint4 (INTERNALLENGTH = 4, INPUT = public.xint4in, OUTPUT = public.xint4out, ALIGNMENT = int4, STORAGE = plain, PASSEDBYVALUE);",
                "CREATE TYPE public.xint4 (INPUT = xint4in, OUTPUT = xint4out, LIKE = integer, CATEGORY = 'U', DELIMITER = ',', PREFERRED = false);") + UsesSql);
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task AnOptionSetChangesIsChangedInPlaceUnderAColumn()
    {
        var databaseName = await CreateDatabaseAsync("g267s");
        var projectDirectory = CreateProjectDirectory("g267s");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, BaseTypeSql + ReceiveSendSql + UsesSql);
            Assert.AreEqual(0, (await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", Superuser)).ExitCode);
            await ExecuteAsync(connectionString, "INSERT INTO public.t VALUES (1, '7');");

            WriteSchema(projectDirectory, Edit(BaseTypeSql, "PASSEDBYVALUE);", "PASSEDBYVALUE, RECEIVE = xint4recv, SEND = xint4send);") + ReceiveSendSql + UsesSql);
            var dryRun = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--dry-run");
            StringAssert.Contains(dryRun.Output, "ALTER TYPE \"public\".\"xint4\" SET (RECEIVE = xint4recv, SEND = xint4send);");
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", Superuser);
            Assert.AreEqual(0, published.ExitCode, published.Output);
            Assert.AreEqual("xint4recv xint4send", await ScalarTextAsync(connectionString, "SELECT concat_ws(' ', typreceive, typsend) FROM pg_type WHERE oid = 'public.xint4'::regtype"));
            Assert.AreEqual("7", await ScalarTextAsync(connectionString, "SELECT v::text FROM public.t"), "The column and its value are untouched.");
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task AnyOtherChangeRebuildsTheTypeAndItsFunctionsOrIsRefusedWhileAColumnUsesIt()
    {
        var databaseName = await CreateDatabaseAsync("g267r");
        var projectDirectory = CreateProjectDirectory("g267r");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, BaseTypeSql + "COMMENT ON TYPE public.xint4 IS 'kept';\n");
            Assert.AreEqual(0, (await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", Superuser)).ExitCode);

            // Nothing uses it: dropped with its functions, and created again.
            WriteSchema(projectDirectory, Edit(BaseTypeSql, "PASSEDBYVALUE);", "PASSEDBYVALUE, CATEGORY = 'N');") + "COMMENT ON TYPE public.xint4 IS 'kept';\n");
            var rebuilt = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", Superuser);
            Assert.AreEqual(0, rebuilt.ExitCode, rebuilt.Output);
            Assert.AreEqual("N kept", await ScalarTextAsync(connectionString, "SELECT concat_ws(' ', typcategory, obj_description(oid, 'pg_type')) FROM pg_type WHERE oid = 'public.xint4'::regtype"));
            Assert.AreEqual("2", await ScalarTextAsync(connectionString, "SELECT count(*)::text FROM pg_proc WHERE proname IN ('xint4in', 'xint4out')"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // A column uses it: refused, naming the table, and nothing runs.
            WriteSchema(projectDirectory, Edit(BaseTypeSql, "PASSEDBYVALUE);", "PASSEDBYVALUE, CATEGORY = 'N');") + "COMMENT ON TYPE public.xint4 IS 'kept';\n" + UsesSql);
            Assert.AreEqual(0, (await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", Superuser)).ExitCode);
            WriteSchema(projectDirectory, Edit(BaseTypeSql, "PASSEDBYVALUE);", "PASSEDBYVALUE, CATEGORY = 'S');") + "COMMENT ON TYPE public.xint4 IS 'kept';\n" + UsesSql);
            var planned = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--dry-run");
            StringAssert.Contains(planned.Output, "The base type public.xint4 changes an option that PostgreSQL cannot change in place");
            StringAssert.Contains(planned.Output, "public.t");
            var refused = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", Superuser);
            Assert.AreNotEqual(0, refused.ExitCode, refused.Output);
            StringAssert.Contains(refused.Output, "[Type] public.xint4");
            Assert.AreEqual("N", await ScalarTextAsync(connectionString, "SELECT typcategory::text FROM pg_type WHERE oid = 'public.xint4'::regtype"));
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task ARemovedTypeIsDroppedWithItsFunctionsUnlessSomethingElseUsesIt()
    {
        var databaseName = await CreateDatabaseAsync("g267d");
        var projectDirectory = CreateProjectDirectory("g267d", ", \"schemas\": [\"public\"]");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, BaseTypeSql + "CREATE TABLE public.keep (id integer);\n");
            Assert.AreEqual(0, (await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", Superuser)).ExitCode);

            // A table outside the project's schemas uses the type: the check before the CASCADE stops the publish, which rolls back.
            await ExecuteAsync(connectionString, "CREATE SCHEMA other; CREATE TABLE other.u (v public.xint4);");
            WriteSchema(projectDirectory, "CREATE TABLE public.keep (id integer);\n");
            var stopped = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreNotEqual(0, stopped.ExitCode, stopped.Output);
            StringAssert.Contains(stopped.Output, "base type public.xint4 cannot be dropped: DROP TYPE ... CASCADE would also drop column v of table other.u");
            Assert.AreEqual("1", await ScalarTextAsync(connectionString, "SELECT count(*)::text FROM pg_attribute WHERE attrelid = 'other.u'::regclass AND attname = 'v'"), "The column is still there.");

            await ExecuteAsync(connectionString, "DROP TABLE other.u;");
            var dropped = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, dropped.ExitCode, "Dropping a base type needs no superuser.\n" + dropped.Output);
            Assert.AreEqual("0 0", await ScalarTextAsync(connectionString, "SELECT (SELECT count(*) FROM pg_type WHERE typname IN ('xint4', '_xint4')) || ' ' || (SELECT count(*) FROM pg_proc WHERE proname IN ('xint4in', 'xint4out'))"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task InitWritesTheShellTheFunctionsAndTheTypeInItsFileAndTheScaffoldConverges()
    {
        var databaseName = await CreateDatabaseAsync("g267i");
        var secondDatabase = await CreateDatabaseAsync("g267j");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g267-init-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            await ExecuteAsync(connectionString, BaseTypeSql + ReceiveSendSql + """
                ALTER TYPE public.xint4 SET (RECEIVE = xint4recv, SEND = xint4send);
                CREATE TYPE public.xvec;
                CREATE FUNCTION public.xvec_in(cstring) RETURNS public.xvec LANGUAGE internal IMMUTABLE STRICT AS $$float8in$$;
                CREATE FUNCTION public.xvec_out(public.xvec) RETURNS cstring LANGUAGE internal IMMUTABLE STRICT AS $$float8out$$;
                CREATE TYPE public.xvec (INPUT = xvec_in, OUTPUT = xvec_out, LIKE = float8, ELEMENT = float4, SUBSCRIPT = raw_array_subscript_handler, CATEGORY = 'N', DEFAULT = '0', COLLATABLE = false);
                COMMENT ON TYPE public.xvec IS 'a vector';
                """ + UsesSql + "CREATE FUNCTION public.plain() RETURNS integer LANGUAGE sql AS $$ SELECT 1 $$;\n");

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            Assert.IsFalse(scaffolded.Output.Contains("Left out a base type", StringComparison.Ordinal), "A base type is not left out any more.\n" + scaffolded.Output);

            var sqlDirectory = Path.Combine(scaffoldDirectory, "sql");
            var typeFile = File.ReadAllText(Path.Combine(sqlDirectory, "Schema", "public", "Type", "xint4.sql")).Replace("\r\n", "\n", StringComparison.Ordinal);
            var shell = typeFile.IndexOf("CREATE TYPE public.xint4;", StringComparison.Ordinal);
            var input = typeFile.IndexOf("FUNCTION public.xint4in(cstring)", StringComparison.Ordinal);
            var definition = typeFile.IndexOf("CREATE TYPE public.xint4 (", StringComparison.Ordinal);
            Assert.IsTrue(shell >= 0 && shell < input && input < definition, typeFile);
            StringAssert.Contains(typeFile, "RECEIVE = public.xint4recv");
            Assert.IsFalse(Directory.EnumerateFiles(sqlDirectory, "xint4in.sql", SearchOption.AllDirectories).Any(), "A support function is in its type's file, not a file of its own.");
            Assert.IsTrue(Directory.EnumerateFiles(sqlDirectory, "plain.sql", SearchOption.AllDirectories).Any());
            await AssertDiffIsEmptyAsync(scaffoldProject, connectionString);

            var secondConnection = ConnectionStringFor(secondDatabase);
            var copied = await RunAsync("publish", "--project", scaffoldProject, "--target-connection", secondConnection, "--execute", Superuser);
            Assert.AreEqual(0, copied.ExitCode, copied.Output);
            Assert.AreEqual(
                await ScalarTextAsync(connectionString, "SELECT string_agg(concat_ws(' ', typname, typlen, typbyval, typalign, typstorage, typcategory, typelem::regtype, typdefault, typinput, typreceive, typsubscript, obj_description(oid, 'pg_type')), ' | ' ORDER BY typname) FROM pg_type WHERE typname IN ('xint4', 'xvec')"),
                await ScalarTextAsync(secondConnection, "SELECT string_agg(concat_ws(' ', typname, typlen, typbyval, typalign, typstorage, typcategory, typelem::regtype, typdefault, typinput, typreceive, typsubscript, obj_description(oid, 'pg_type')), ' | ' ORDER BY typname) FROM pg_type WHERE typname IN ('xint4', 'xvec')"));
            await AssertDiffIsEmptyAsync(scaffoldProject, secondConnection);
        }
        finally
        {
            await DropDatabaseAsync(secondDatabase);
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(scaffoldDirectory);
        }
    }
}
