using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-194 against a real server. <c>CREATE TABLE ... INHERITS (...)</c> was deployed without the clause, so the child had only its own columns and no parent,
/// with no warning. Inheritance is now deployed, read back from <c>pg_inherits</c>, compared, scaffolded by <c>init</c>, and changed in place: a column a parent
/// gains reaches the child through the parent, a parent added is <c>INHERIT</c>, and one removed is <c>NO INHERIT</c>.
/// </summary>
[TestClass]
public sealed class TableInheritsEndToEndTests
{
    private const string Sql = """
        CREATE SCHEMA s;
        CREATE TABLE s."Base" (id int NOT NULL, name text DEFAULT 'x', CONSTRAINT base_only CHECK (id > 0) NO INHERIT, CONSTRAINT base_all CHECK (id < 1000));
        CREATE TABLE public.other (o int);
        CREATE TABLE public.child (extra int, name text) INHERITS (s."Base");
        CREATE TABLE public.grandchild (g int) INHERITS (public.child);
        CREATE TABLE public.two (z int CHECK (z > 0)) INHERITS (s."Base", public.other);
        """;

    private const string Parents = "SELECT string_agg(inhrelid::regclass::text || '<-' || inhparent::regclass::text, ' ' ORDER BY inhrelid::regclass::text, inhseqno) FROM pg_inherits;";

    [TestMethod]
    public async Task InheritanceIsDeployedAndConverges_AndChangesAreMadeInPlace()
    {
        var databaseName = await CreateDatabaseAsync("g194");
        var projectDirectory = CreateProjectDirectory("g194");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g194-init-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Sql);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual("child<-s.\"Base\" grandchild<-child two<-s.\"Base\" two<-other", await ScalarTextAsync(connectionString, Parents));
            Assert.AreEqual("id,name,extra,g", await ScalarTextAsync(connectionString, "SELECT string_agg(attname, ',' ORDER BY attnum) FROM pg_attribute WHERE attrelid = 'public.grandchild'::regclass AND attnum > 0;"));
            // A NO INHERIT check stays on the parent; an ordinary one reaches the children.
            Assert.AreEqual("base_all", await ScalarTextAsync(connectionString, "SELECT string_agg(conname, ',' ORDER BY conname) FROM pg_constraint WHERE conrelid = 'public.child'::regclass AND contype = 'c';"));
            Assert.AreEqual("base_all,base_only", await ScalarTextAsync(connectionString, "SELECT string_agg(conname, ',' ORDER BY conname) FROM pg_constraint WHERE conrelid = 's.\"Base\"'::regclass AND contype = 'c';"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            await AssertDiffIsEmptyAsync(scaffoldProject, connectionString);

            // A row in the grandchild is kept through every change below.
            await ExecuteAsync(connectionString, "INSERT INTO public.grandchild (id, extra, g) VALUES (1, 2, 3);");

            // A column the parent gains reaches the children through it; a parent added to child is INHERIT once child has its columns.
            var changedSql = Edit(Edit(Sql, "name text DEFAULT 'x', CONSTRAINT base_only", "name text DEFAULT 'x', added date, CONSTRAINT base_only"),
                "INHERITS (s.\"Base\");", "INHERITS (s.\"Base\", public.other);");
            WriteSchema(projectDirectory, changedSql);
            var changed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, changed.ExitCode, "publish --execute failed.\n" + changed.Output);
            Assert.AreEqual("child<-s.\"Base\" child<-other grandchild<-child two<-s.\"Base\" two<-other", await ScalarTextAsync(connectionString, Parents));
            Assert.AreEqual("1", await ScalarTextAsync(connectionString, "SELECT count(*) FROM public.grandchild WHERE added IS NULL AND o IS NULL;"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // A parent removed is NO INHERIT; the child keeps the columns it states itself.
            WriteSchema(projectDirectory, Edit(changedSql, "CREATE TABLE public.two (z int CHECK (z > 0)) INHERITS (s.\"Base\", public.other);", "CREATE TABLE public.two (z int CHECK (z > 0), o int) INHERITS (s.\"Base\");"));
            var removed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, removed.ExitCode, "publish --execute failed.\n" + removed.Output);
            Assert.AreEqual("child<-s.\"Base\" child<-other grandchild<-child two<-s.\"Base\"", await ScalarTextAsync(connectionString, Parents));
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
