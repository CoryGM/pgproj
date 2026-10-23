using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-236 against a real server. A project written the way <c>pg_dump --schema-only</c> writes a schema (partitions attached with <c>ALTER TABLE ... ATTACH PARTITION</c>, a serial's default
/// and owner as <c>ALTER</c> statements, identity added with <c>ADD GENERATED</c>, a partition's own index and key attached to the parent's, a placeholder view replaced later) was reported as
/// not modeled and lost all of it. It is now deployed whole, converges, and round-trips through <c>init</c>.
/// </summary>
[TestClass]
public sealed class PgDumpAlterFormsEndToEndTests
{
    private const string Dump = """
        CREATE TABLE public.parted (a integer NOT NULL, b text) PARTITION BY RANGE (a);
        CREATE TABLE public.parted_1 (a integer NOT NULL, b text);
        CREATE TABLE public.parted_2 (a integer NOT NULL, b text);
        CREATE SEQUENCE public.t_id_seq AS integer START WITH 1 INCREMENT BY 1 NO MINVALUE NO MAXVALUE CACHE 1;
        CREATE TABLE public.t (id integer NOT NULL, name text);
        CREATE TABLE public.i (id integer NOT NULL, note text);
        CREATE VIEW public.pv AS SELECT NULL::integer AS id;
        CREATE VIEW public.v2 AS SELECT id FROM public.pv;
        CREATE OR REPLACE VIEW public.pv AS SELECT id FROM public.t;
        ALTER TABLE ONLY public.parted ATTACH PARTITION public.parted_1 FOR VALUES FROM (0) TO (10);
        ALTER TABLE ONLY public.parted ATTACH PARTITION public.parted_2 FOR VALUES FROM (10) TO (20);
        ALTER TABLE ONLY public.t ALTER COLUMN id SET DEFAULT nextval('public.t_id_seq'::regclass);
        ALTER SEQUENCE public.t_id_seq OWNED BY public.t.id;
        ALTER TABLE public.i ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (SEQUENCE NAME public.i_id_seq START WITH 1 INCREMENT BY 1 NO MINVALUE NO MAXVALUE CACHE 1);
        ALTER TABLE ONLY public.t ALTER COLUMN name SET STORAGE EXTERNAL;
        ALTER TABLE ONLY public.t ALTER COLUMN name SET COMPRESSION pglz;
        ALTER TABLE ONLY public.parted ADD CONSTRAINT parted_pkey PRIMARY KEY (a);
        ALTER TABLE ONLY public.parted_1 ADD CONSTRAINT parted_1_pkey PRIMARY KEY (a);
        ALTER TABLE ONLY public.parted_2 ADD CONSTRAINT parted_2_pkey PRIMARY KEY (a);
        CREATE INDEX parted_b_idx ON ONLY public.parted USING btree (b);
        CREATE INDEX parted_1_b_idx ON public.parted_1 USING btree (b);
        CREATE INDEX parted_2_b_idx ON public.parted_2 USING btree (b);
        ALTER INDEX public.parted_pkey ATTACH PARTITION public.parted_1_pkey;
        ALTER INDEX public.parted_pkey ATTACH PARTITION public.parted_2_pkey;
        ALTER INDEX public.parted_b_idx ATTACH PARTITION public.parted_1_b_idx;
        ALTER INDEX public.parted_b_idx ATTACH PARTITION public.parted_2_b_idx;
        """;

    [TestMethod]
    public async Task AProjectWrittenAsPgDumpWritesItIsDeployedWholeAndConverges()
    {
        var databaseName = await CreateDatabaseAsync("g236");
        var projectDirectory = CreateProjectDirectory("g236");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g236-init-" + Guid.NewGuid().ToString("N"));
        var secondDatabase = await CreateDatabaseAsync("g236b");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Dump);

            var built = await RunAsync("build", "--project", projectFile);
            Assert.AreEqual(0, built.ExitCode, built.Output);
            Assert.IsFalse(built.Output.Contains("is not modeled", StringComparison.Ordinal), "No pg_dump form is reported as not modeled.\n" + built.Output);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            await AssertCatalogAsync(connectionString);
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // A second publish has nothing to do.
            var again = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--dry-run");
            Assert.AreEqual(0, again.ExitCode, again.Output);

            // init writes the same things, and what it writes publishes into an empty database and converges.
            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            var secondConnection = ConnectionStringFor(secondDatabase);
            var republished = await RunAsync("publish", "--project", scaffoldProject, "--target-connection", secondConnection, "--execute");
            Assert.AreEqual(0, republished.ExitCode, "publishing the scaffold failed.\n" + republished.Output);
            await AssertCatalogAsync(secondConnection);
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

    private static async Task AssertCatalogAsync(string connectionString)
    {
        // The partitions are partitions, with their bounds.
        Assert.AreEqual("parted_1,parted_2", await ScalarTextAsync(connectionString, "SELECT string_agg(c.relname, ',' ORDER BY c.relname) FROM pg_inherits i JOIN pg_class c ON c.oid = i.inhrelid WHERE i.inhparent = 'public.parted'::regclass"));

        // The serial's default is there, its sequence belongs to the column, and the default works.
        StringAssert.Contains(await ScalarTextAsync(connectionString, "SELECT pg_get_expr(adbin, adrelid) FROM pg_attrdef WHERE adrelid = 'public.t'::regclass"), "nextval('t_id_seq'::regclass)");
        Assert.AreEqual("t.id", await ScalarTextAsync(connectionString, "SELECT d.refobjid::regclass::text || '.' || a.attname FROM pg_depend d JOIN pg_attribute a ON a.attrelid = d.refobjid AND a.attnum = d.refobjsubid WHERE d.objid = 'public.t_id_seq'::regclass AND d.deptype = 'a'"));

        // The identity, the storage, and the compression.
        Assert.AreEqual("a", await ScalarTextAsync(connectionString, "SELECT attidentity::text FROM pg_attribute WHERE attrelid = 'public.i'::regclass AND attname = 'id'"));
        Assert.AreEqual("e|p", await ScalarTextAsync(connectionString, "SELECT attstorage::text || '|' || attcompression::text FROM pg_attribute WHERE attrelid = 'public.t'::regclass AND attname = 'name'"));

        // Each partition has its key and its one index, not two of each.
        Assert.AreEqual("2|2", await ScalarTextAsync(connectionString, "SELECT (SELECT count(*) FROM pg_index WHERE indrelid = 'public.parted_1'::regclass)::text || '|' || (SELECT count(*) FROM pg_index WHERE indrelid = 'public.parted_2'::regclass)::text"));
        Assert.AreEqual("0", await ScalarTextAsync(connectionString, "SELECT count(*)::text FROM pg_index WHERE indrelid IN ('public.parted'::regclass, 'public.parted_1'::regclass, 'public.parted_2'::regclass) AND NOT indisvalid"));

        // The placeholder view is the real one.
        StringAssert.Contains(await ScalarTextAsync(connectionString, "SELECT pg_get_viewdef('public.pv'::regclass)"), "FROM t");
    }
}
