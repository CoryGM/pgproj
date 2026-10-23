using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-198 against a real server. A <c>RETURNS TABLE</c> function was created as <c>RETURNS SETOF record</c>, without its columns, so every call
/// failed ("a column definition list is required"), and a changed column was not seen. It is now created with its columns, can be called, and
/// a changed column drops and creates it.
/// </summary>
[TestClass]
public sealed class ReturnsTableColumnsEndToEndTests
{
    private const string Sql = """
        CREATE TABLE public.t (id int, name text);
        CREATE TYPE public.mood AS ENUM ('a', 'b');
        CREATE FUNCTION public.f(p int) RETURNS TABLE (id int, name text, m public.mood) LANGUAGE sql STABLE AS $$ SELECT id, name, 'a'::public.mood FROM public.t WHERE id = p $$;
        CREATE FUNCTION public.g() RETURNS TABLE ("Id" bigint) LANGUAGE plpgsql AS $$ BEGIN RETURN QUERY SELECT 1::bigint; END $$;
        """;

    [TestMethod]
    public async Task AFunctionReturningATableHasItsColumns_AndAChangedColumnIsDeployed()
    {
        var databaseName = await CreateDatabaseAsync("g198");
        var projectDirectory = CreateProjectDirectory("g198");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g198-init-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Sql);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual("TABLE(id integer, name text, m mood)", await ScalarTextAsync(connectionString, "SELECT pg_get_function_result('public.f'::regproc);"));
            Assert.AreEqual("1", await ScalarTextAsync(connectionString, "SELECT \"Id\" FROM public.g();"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            await AssertDiffIsEmptyAsync(scaffoldProject, connectionString);

            // A column renamed, one retyped, and one added: a different row type, so the function is dropped and created.
            WriteSchema(projectDirectory, Edit(Edit(Sql, "RETURNS TABLE (id int, name text, m public.mood)", "RETURNS TABLE (id int, label varchar, m public.mood, extra int)"),
                "SELECT id, name, 'a'::public.mood FROM", "SELECT id, name, 'a'::public.mood, 1 FROM"));
            var changed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, changed.ExitCode, "publish --execute failed.\n" + changed.Output);
            Assert.AreEqual("TABLE(id integer, label character varying, m mood, extra integer)", await ScalarTextAsync(connectionString, "SELECT pg_get_function_result('public.f'::regproc);"));
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
