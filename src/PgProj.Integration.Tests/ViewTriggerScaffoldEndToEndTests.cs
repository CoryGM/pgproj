using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-203 against a real server. <c>init</c> left a trigger on a view out of the scaffold (it looked only for a table's file), so the scaffold's first
/// <c>diff</c> proposed dropping the trigger. A row-level <c>INSTEAD OF</c> trigger, one with a comment, and a statement-level trigger on a view are now
/// scaffolded into the view's file, and the scaffold converges.
/// </summary>
[TestClass]
public sealed class ViewTriggerScaffoldEndToEndTests
{
    private const string Sql = """
        CREATE TABLE public.t (id int);
        CREATE VIEW public.v AS SELECT id FROM public.t;
        CREATE FUNCTION public.f() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN INSERT INTO public.t VALUES (NEW.id); RETURN NEW; END $$;
        CREATE FUNCTION public.s() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NULL; END $$;
        CREATE TRIGGER tr INSTEAD OF INSERT ON public.v FOR EACH ROW EXECUTE FUNCTION public.f();
        CREATE TRIGGER tu INSTEAD OF UPDATE OR DELETE ON public.v FOR EACH ROW EXECUTE FUNCTION public.f();
        COMMENT ON TRIGGER tu ON public.v IS 'x';
        CREATE TRIGGER ts AFTER INSERT ON public.v FOR EACH STATEMENT EXECUTE FUNCTION public.s();
        """;

    [TestMethod]
    public async Task TriggersOnAViewAreScaffolded_AndTheScaffoldConverges()
    {
        var databaseName = await CreateDatabaseAsync("g203");
        var projectDirectory = CreateProjectDirectory("g203");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g203-init-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Sql);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            var viewFile = await File.ReadAllTextAsync(Directory.GetFiles(scaffoldDirectory, "v.sql", SearchOption.AllDirectories).Single());
            StringAssert.Contains(viewFile, "CREATE TRIGGER tr ");
            StringAssert.Contains(viewFile, "CREATE TRIGGER tu ");
            StringAssert.Contains(viewFile, "CREATE TRIGGER ts ");
            await AssertDiffIsEmptyAsync(scaffoldProject, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
            TryDeleteDirectory(scaffoldDirectory);
        }
    }
}
