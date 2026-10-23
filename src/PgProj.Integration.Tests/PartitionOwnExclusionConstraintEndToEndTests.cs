using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-296 against a real server (PostgreSQL 17 and later, which allow an exclusion constraint on a partitioned table). An exclusion constraint made on a partition first and adopted by the
/// parent's equivalent one keeps its own name; it was given the generated name of a clone instead, as G-237 fixed for a partition's own index and key.
/// </summary>
[TestClass]
public sealed class PartitionOwnExclusionConstraintEndToEndTests
{
    // As pg_dump writes it: the partition's constraint, the attach, then the parent's constraint and ALTER INDEX ... ATTACH PARTITION.
    private const string Schema = """
        CREATE EXTENSION IF NOT EXISTS btree_gist;
        CREATE TABLE public.idxpart (a int, b int4range) PARTITION BY RANGE (a);
        CREATE TABLE public.idxpart_1 (a int, b int4range);
        ALTER TABLE ONLY public.idxpart_1 ADD CONSTRAINT idxpart_1_own_excl EXCLUDE USING gist (a WITH =, b WITH &&);
        ALTER TABLE ONLY public.idxpart ATTACH PARTITION public.idxpart_1 FOR VALUES FROM (1) TO (10);
        ALTER TABLE ONLY public.idxpart ADD CONSTRAINT idxpart_excl EXCLUDE USING gist (a WITH =, b WITH &&);
        ALTER INDEX public.idxpart_excl ATTACH PARTITION public.idxpart_1_own_excl;
        """;

    private const string Query = """
        SELECT string_agg(c.relname || ':' || con.conname || ':' || (con.conparentid <> 0)::text, '|' ORDER BY c.relname, con.conname)
        FROM pg_constraint con JOIN pg_class c ON c.oid = con.conrelid WHERE con.contype = 'x' AND c.relnamespace = 'public'::regnamespace;
        """;

    [TestMethod]
    [PostgreSqlVersion(Minimum = 17)]
    public async Task APartitionsOwnExclusionConstraintKeepsItsNameThroughPublishAndInit()
    {
        var databaseName = await CreateDatabaseAsync("g296");
        var scaffoldDatabaseName = await CreateDatabaseAsync("g296s");
        var projectDirectory = CreateProjectDirectory("g296");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Schema);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual("idxpart:idxpart_excl:false|idxpart_1:idxpart_1_own_excl:true", await ScalarTextAsync(connectionString, Query));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var scaffoldDirectory = Path.Combine(projectDirectory, "scaffold");
            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--source-connection", connectionString, "--output", scaffoldDirectory, "--project", scaffoldProject);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            await AssertDiffIsEmptyAsync(scaffoldProject, connectionString);

            var scaffoldConnectionString = ConnectionStringFor(scaffoldDatabaseName);
            var deployedScaffold = await RunAsync("publish", "--project", scaffoldProject, "--target-connection", scaffoldConnectionString, "--execute");
            Assert.AreEqual(0, deployedScaffold.ExitCode, deployedScaffold.Output);
            Assert.AreEqual("idxpart:idxpart_excl:false|idxpart_1:idxpart_1_own_excl:true", await ScalarTextAsync(scaffoldConnectionString, Query));
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
