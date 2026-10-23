using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-295 against a real server. A comment on the index of a primary key, unique, or exclusion constraint (<c>COMMENT ON INDEX t_pkey IS ...</c>) was not read, so <c>init</c> lost it and
/// a project's statement was not deployed. The index has the constraint's name; the comment is deployed, changed or removed in place, and scaffolded.
/// </summary>
[TestClass]
public sealed class KeyIndexCommentEndToEndTests
{
    private const string Before = """
        CREATE EXTENSION IF NOT EXISTS btree_gist;
        CREATE TABLE public.t (id int PRIMARY KEY, code text, span int4range, CONSTRAINT t_code_key UNIQUE (code), CONSTRAINT t_span_excl EXCLUDE USING gist (span WITH &&));
        COMMENT ON INDEX public.t_pkey IS 'pkey index';
        COMMENT ON INDEX public.t_code_key IS 'code index';
        COMMENT ON INDEX public.t_span_excl IS 'span index';
        CREATE INDEX t_code_idx ON public.t (code);
        COMMENT ON INDEX public.t_code_idx IS 'plain index';
        """;

    private const string After = """
        CREATE EXTENSION IF NOT EXISTS btree_gist;
        CREATE TABLE public.t (id int PRIMARY KEY, code text, span int4range, CONSTRAINT t_code_key UNIQUE (code), CONSTRAINT t_span_excl EXCLUDE USING gist (span WITH &&));
        COMMENT ON INDEX public.t_pkey IS 'pkey index, changed';
        COMMENT ON INDEX public.t_code_key IS NULL;
        CREATE INDEX t_code_idx ON public.t (code);
        COMMENT ON INDEX public.t_code_idx IS 'plain index';
        """;

    private const string Query = """
        SELECT string_agg(c.relname || '=' || coalesce(obj_description(c.oid, 'pg_class'), '-'), '|' ORDER BY c.relname)
        FROM pg_class c WHERE c.relkind = 'i' AND c.relnamespace = 'public'::regnamespace;
        """;

    [TestMethod]
    public async Task AKeyIndexCommentIsDeployedChangedRemovedAndScaffolded()
    {
        var databaseName = await CreateDatabaseAsync("g295");
        var scaffoldDatabaseName = await CreateDatabaseAsync("g295s");
        var projectDirectory = CreateProjectDirectory("g295");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Before);

            var built = await RunAsync("build", "--project", projectFile);
            Assert.AreEqual(0, built.ExitCode, built.Output);
            Assert.IsFalse(built.Output.Contains("not modeled", StringComparison.Ordinal), built.Output);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual("t_code_idx=plain index|t_code_key=code index|t_pkey=pkey index|t_span_excl=span index", await ScalarTextAsync(connectionString, Query));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            WriteSchema(projectDirectory, After);
            var diff = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString);
            StringAssert.Contains(diff.Output, "Index comment");
            var altered = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, altered.ExitCode, "the second publish failed.\n" + altered.Output);
            // t_span_excl is not mentioned any more, so its comment is left alone.
            Assert.AreEqual("t_code_idx=plain index|t_code_key=-|t_pkey=pkey index, changed|t_span_excl=span index", await ScalarTextAsync(connectionString, Query));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var scaffoldDirectory = Path.Combine(projectDirectory, "scaffold");
            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--source-connection", connectionString, "--output", scaffoldDirectory, "--project", scaffoldProject);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            await AssertDiffIsEmptyAsync(scaffoldProject, connectionString);

            var scaffoldConnectionString = ConnectionStringFor(scaffoldDatabaseName);
            var deployedScaffold = await RunAsync("publish", "--project", scaffoldProject, "--target-connection", scaffoldConnectionString, "--execute");
            Assert.AreEqual(0, deployedScaffold.ExitCode, deployedScaffold.Output);
            Assert.AreEqual("t_code_idx=plain index|t_code_key=-|t_pkey=pkey index, changed|t_span_excl=span index", await ScalarTextAsync(scaffoldConnectionString, Query));
            await AssertDiffIsEmptyAsync(scaffoldProject, scaffoldConnectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            await DropDatabaseAsync(scaffoldDatabaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }
}
