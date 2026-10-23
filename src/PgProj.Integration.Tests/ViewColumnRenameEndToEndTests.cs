using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-276 against a real server. Renaming a table column (<c>-- @renamed-from</c>) that a view outputs failed: PostgreSQL keeps the view's output
/// column under the old name, and <c>CREATE OR REPLACE VIEW</c> cannot rename it (<c>42P16</c>). The view's project columns were unknown too, because
/// the project view could not be rendered against a table without the new name. Now the renames are applied when the project's views are rendered,
/// and a view whose columns are only renamed gets <c>ALTER VIEW ... RENAME COLUMN</c> before it is replaced in place.
/// </summary>
[TestClass]
public sealed class ViewColumnRenameEndToEndTests
{
    private const string Before = """
        CREATE TABLE public.customer (id int PRIMARY KEY, email text NOT NULL);
        CREATE VIEW public.v_customer AS SELECT id, email FROM public.customer;
        COMMENT ON COLUMN public.v_customer.email IS 'where receipts go';
        CREATE VIEW public.v_top AS SELECT email FROM public.v_customer WHERE id < 10;
        CREATE MATERIALIZED VIEW public.mv_email AS SELECT email FROM public.customer;
        """;

    private const string After = """
        CREATE TABLE public.customer (
            id int PRIMARY KEY,
            -- @renamed-from: email
            email_address text NOT NULL);
        CREATE VIEW public.v_customer AS SELECT id, email_address FROM public.customer;
        COMMENT ON COLUMN public.v_customer.email_address IS 'where receipts go';
        CREATE VIEW public.v_top AS SELECT email_address FROM public.v_customer WHERE id < 10;
        CREATE MATERIALIZED VIEW public.mv_email AS SELECT email_address FROM public.customer;
        """;

    [TestMethod]
    public async Task RenamingAColumnAViewOutputsRenamesTheViewsColumnAndConverges()
    {
        var databaseName = await CreateDatabaseAsync("g276");
        var projectDirectory = CreateProjectDirectory("g276");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g276-init-" + Guid.NewGuid().ToString("N"));
        var secondDatabase = await CreateDatabaseAsync("g276b");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Before);
            var first = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, first.ExitCode, first.Output);
            await ExecuteAsync(connectionString, "INSERT INTO public.customer VALUES (1, 'a@example.com');");
            var viewOid = await ScalarTextAsync(connectionString, "SELECT 'public.v_customer'::regclass::oid::text");

            WriteSchema(projectDirectory, After);
            var script = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--dry-run", "--allow-destructive");
            Assert.AreEqual(0, script.ExitCode, script.Output);
            StringAssert.Contains(script.Output, "ALTER VIEW \"public\".\"v_customer\" RENAME COLUMN email TO email_address;");
            StringAssert.Contains(script.Output, "ALTER VIEW \"public\".\"v_top\" RENAME COLUMN email TO email_address;");

            var renamed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-destructive");
            Assert.AreEqual(0, renamed.ExitCode, "the rename failed.\n" + renamed.Output);

            Assert.AreEqual(viewOid, await ScalarTextAsync(connectionString, "SELECT 'public.v_customer'::regclass::oid::text"), "the view was replaced in place, not dropped");
            Assert.AreEqual("a@example.com", await ScalarTextAsync(connectionString, "SELECT email_address FROM public.v_top"));
            Assert.AreEqual("a@example.com", await ScalarTextAsync(connectionString, "SELECT email_address FROM public.mv_email"));
            Assert.AreEqual("where receipts go", await ScalarTextAsync(connectionString, "SELECT col_description('public.v_customer'::regclass, 2)"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            var secondConnection = ConnectionStringFor(secondDatabase);
            var scaffoldPublished = await RunAsync("publish", "--project", scaffoldProject, "--target-connection", secondConnection, "--execute");
            Assert.AreEqual(0, scaffoldPublished.ExitCode, "publishing the scaffold failed.\n" + scaffoldPublished.Output);
            await AssertDiffIsEmptyAsync(scaffoldProject, secondConnection);
        }
        finally
        {
            await DropDatabaseAsync(secondDatabase);
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
            TryDeleteDirectory(scaffoldDirectory);
        }
    }
}
