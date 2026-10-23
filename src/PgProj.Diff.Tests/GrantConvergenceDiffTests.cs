using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-61: after a deploy the sample project's grants did not converge: a second publish revoked and re-granted privileges,
/// including PostgreSQL's own implicit ones. Four causes: (1) the same privileges spelled differently (live reports
/// <c>ALL PRIVILEGES</c> for <c>CREATE, USAGE</c> on a schema and <c>EXECUTE</c> on functions, the project writes them out) made a
/// removed and an added grant; (2) PostgreSQL's implicit grants (the owner's, and <c>PUBLIC</c>'s default <c>EXECUTE</c> on
/// functions) looked like grants the project wanted removed; (3) a grant over <c>ALL TABLES</c> of a schema with no tables can
/// never be seen live, so it was re-added every publish; (4) the live loader did not count views as tables. Both directions
/// matter: what is the same reports nothing, and what genuinely differs is still reported.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class GrantConvergenceDiffTests
{
    private const string Objects = "create schema persons; create table persons.t (id integer); create function public.f() returns integer language sql as $$ select 1 $$; ";

    /// <summary>
    /// The sample's public.sql grants against the text the live loader renders for what PostgreSQL recorded. (The loader leaves
    /// out the owner's own entries, such as <c>postgres</c> on functions; the Docker tests check that against a real server.)
    /// </summary>
    [TestMethod]
    public void SampleGrantsAgainstTheirLiveRenderingProduceNoChange()
    {
        var project = Build(Objects + """
            GRANT USAGE ON SCHEMA public TO PUBLIC;
            GRANT USAGE, CREATE ON SCHEMA public TO local_pg_admin;
            GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO local_pg_admin;
            GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public TO local_pg_admin;
            GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA public TO local_pg_admin;
            ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON TABLES TO local_pg_admin;
            ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON SEQUENCES TO local_pg_admin;
            ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT EXECUTE ON FUNCTIONS TO local_pg_admin;
            """);
        var live = Build(Objects + """
            GRANT USAGE ON SCHEMA public TO PUBLIC;
            GRANT ALL PRIVILEGES ON SCHEMA public TO local_pg_admin;
            GRANT ALL PRIVILEGES ON ALL FUNCTIONS IN SCHEMA public TO local_pg_admin;
            GRANT ALL PRIVILEGES ON ALL FUNCTIONS IN SCHEMA public TO PUBLIC;
            ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL PRIVILEGES ON TABLES TO local_pg_admin;
            ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL PRIVILEGES ON SEQUENCES TO local_pg_admin;
            ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL PRIVILEGES ON FUNCTIONS TO local_pg_admin;
            """);

        AssertNoGrantChange(live, project);
    }

    [TestMethod]
    public void SelfDiffOfGrantsIsEmpty()
    {
        var model = Build(Objects + "GRANT USAGE, CREATE ON SCHEMA public TO r; GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA public TO r;");

        AssertNoGrantChange(model, model);
    }

    // ---- (1) the same privileges, spelled differently ----

    [TestMethod]
    [DataRow("GRANT USAGE, CREATE ON SCHEMA s TO r;", "GRANT ALL PRIVILEGES ON SCHEMA s TO r;")]
    [DataRow("GRANT CREATE, USAGE ON SCHEMA s TO r;", "GRANT ALL ON SCHEMA s TO r;")]
    [DataRow("GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA s TO r;", "GRANT ALL PRIVILEGES ON ALL FUNCTIONS IN SCHEMA s TO r;")]
    [DataRow("GRANT SELECT, UPDATE, USAGE ON ALL SEQUENCES IN SCHEMA s TO r;", "GRANT ALL ON ALL SEQUENCES IN SCHEMA s TO r;")]
    [DataRow("GRANT SELECT, INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER ON ALL TABLES IN SCHEMA s TO r;", "GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA s TO r;")]
    [DataRow("GRANT SELECT, INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER, MAINTAIN ON ALL TABLES IN SCHEMA s TO r;", "GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA s TO r;")]
    [DataRow("ALTER DEFAULT PRIVILEGES IN SCHEMA s GRANT EXECUTE ON FUNCTIONS TO r;", "ALTER DEFAULT PRIVILEGES IN SCHEMA s GRANT ALL PRIVILEGES ON FUNCTIONS TO r;")]
    public void SamePrivilegesSpelledDifferentlyProduceNoChange(string projectGrant, string liveGrant)
    {
        const string objects = "create schema s; create table s.t (id integer); create sequence s.q; create function s.f() returns integer language sql as $$ select 1 $$; ";

        AssertNoGrantChange(Build(objects + liveGrant), Build(objects + projectGrant));
    }

    /// <summary>A privilege the full set does not contain is a real difference, and so is a smaller set.</summary>
    [TestMethod]
    [DataRow("GRANT USAGE ON SCHEMA s TO r;", "GRANT ALL PRIVILEGES ON SCHEMA s TO r;")]
    [DataRow("GRANT CREATE ON SCHEMA s TO r;", "GRANT USAGE ON SCHEMA s TO r;")]
    [DataRow("GRANT SELECT ON ALL TABLES IN SCHEMA s TO r;", "GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA s TO r;")]
    [DataRow("GRANT SELECT, INSERT ON ALL TABLES IN SCHEMA s TO r;", "GRANT SELECT ON ALL TABLES IN SCHEMA s TO r;")]
    [DataRow("GRANT USAGE ON ALL SEQUENCES IN SCHEMA s TO r;", "GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA s TO r;")]
    public void GenuinelyDifferentPrivilegesAreStillReported(string projectGrant, string liveGrant)
    {
        const string objects = "create schema s; create table s.t (id integer); create sequence s.q; ";

        var changes = new DiffEngine().Diff(Build(objects + liveGrant), Build(objects + projectGrant));

        Assert.IsGreaterThan(0, GrantChangeCount(changes), $"{projectGrant} vs {liveGrant}");
    }

    // ---- (2) PostgreSQL's implicit grants ----

    [TestMethod]
    public void PublicExecuteOnFunctionsThatTheProjectDoesNotListIsNotRevoked()
    {
        const string objects = "create schema s; create function s.f() returns integer language sql as $$ select 1 $$; ";

        AssertNoGrantChange(
            Build(objects + "GRANT ALL PRIVILEGES ON ALL FUNCTIONS IN SCHEMA s TO PUBLIC; GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA s TO r;"),
            Build(objects + "GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA s TO r;"));
    }

    [TestMethod]
    public void ProjectThatStatesPublicExecuteOnFunctionsProducesNoChangeEitherWay()
    {
        const string objects = "create schema s; create function s.f() returns integer language sql as $$ select 1 $$; ";
        var stated = Build(objects + "GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA s TO PUBLIC;");
        var withDefault = Build(objects + "GRANT ALL PRIVILEGES ON ALL FUNCTIONS IN SCHEMA s TO PUBLIC;");
        var without = Build(objects);

        AssertNoGrantChange(withDefault, stated);
        AssertNoGrantChange(without, stated);
    }

    /// <summary>Only the implicit ones are exempt: a grant to any other role that the project does not list is still revoked.</summary>
    [TestMethod]
    public void ExplicitGrantToAnotherRoleThatTheProjectDoesNotListIsStillRevoked()
    {
        const string objects = "create schema s; create function s.f() returns integer language sql as $$ select 1 $$; ";

        var changes = new DiffEngine().Diff(
            Build(objects + "GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA s TO alice;"),
            Build(objects));

        Assert.AreEqual(1, changes.Removed.Count(static removed => removed.NodeKind == ChangeNodeKind.Grant), "The grant to alice must be reported for revoking.");
    }

    [TestMethod]
    public void PublicGrantsOtherThanExecuteOnFunctionsAreNotExempt()
    {
        var changes = new DiffEngine().Diff(
            Build("create schema s; GRANT USAGE ON SCHEMA s TO PUBLIC;"),
            Build("create schema s;"));

        Assert.AreEqual(1, changes.Removed.Count(static removed => removed.NodeKind == ChangeNodeKind.Grant));
    }

    // ---- (5) G-86: PostgreSQL's automatic default on the public schema ----

    [TestMethod]
    public void PublicUsageOnThePublicSchemaThatTheProjectDoesNotListIsNotRevoked()
    {
        AssertNoGrantChange(
            Build("GRANT USAGE ON SCHEMA public TO PUBLIC;"),
            Build(string.Empty));
    }

    [TestMethod]
    public void ProjectThatStatesPublicUsageOnThePublicSchemaProducesNoChangeEitherWay()
    {
        // Unlike EXECUTE on a routine (routines have exactly one privilege), a schema's full privilege set is
        // CREATE and USAGE together, so "ALL PRIVILEGES" is a real difference from "USAGE alone" (CREATE is not
        // part of PostgreSQL's default) - the equivalent live starting points here are USAGE alone, or nothing.
        var stated = Build("GRANT USAGE ON SCHEMA public TO PUBLIC;");
        var withDefault = Build("GRANT USAGE ON SCHEMA public TO PUBLIC;");
        var without = Build(string.Empty);

        AssertNoGrantChange(withDefault, stated);
        AssertNoGrantChange(without, stated);
    }

    /// <summary>The exemption is exactly PostgreSQL's own default: USAGE only, not the CREATE that would also come along with ALL PRIVILEGES.</summary>
    [TestMethod]
    public void CreateOnThePublicSchemaForPublicIsStillRevoked()
    {
        var changes = new DiffEngine().Diff(
            Build("GRANT CREATE ON SCHEMA public TO PUBLIC;"),
            Build(string.Empty));

        Assert.AreEqual(1, changes.Removed.Count(static removed => removed.NodeKind == ChangeNodeKind.Grant));
    }

    /// <summary>A WITH GRANT OPTION grant is not PostgreSQL's plain, non-grantable default, so it is still revoked.</summary>
    [TestMethod]
    public void PublicUsageOnThePublicSchemaWithGrantOptionIsStillRevoked()
    {
        var changes = new DiffEngine().Diff(
            Build("GRANT USAGE ON SCHEMA public TO PUBLIC WITH GRANT OPTION;"),
            Build(string.Empty));

        Assert.AreEqual(1, changes.Removed.Count(static removed => removed.NodeKind == ChangeNodeKind.Grant));
    }

    // Only the schema literally named "public" gets this default; a project-created schema does not - already
    // covered by PublicGrantsOtherThanExecuteOnFunctionsAreNotExempt above, which uses schema "s".

    // ---- (3) grants over nothing ----

    [TestMethod]
    [DataRow("GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA e TO r;")]
    [DataRow("GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA e TO r;")]
    [DataRow("GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA e TO r;")]
    public void GrantOverAnEmptySchemaCanNeverBeSeenLiveAndIsNotAdded(string grant)
    {
        var project = Build("create schema e; " + grant);

        AssertNoGrantChange(Build("create schema e;"), project);
    }

    [TestMethod]
    [DataRow("create table e.t (id integer);", "GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA e TO r;")]
    [DataRow("create view e.v as select 1 as n;", "GRANT SELECT ON ALL TABLES IN SCHEMA e TO r;")]
    [DataRow("create sequence e.q;", "GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA e TO r;")]
    [DataRow("create function e.f() returns integer language sql as $$ select 1 $$;", "GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA e TO r;")]
    public void GrantOverASchemaThatHasSuchObjectsIsAddedWhenLiveLacksIt(string objects, string grant)
    {
        var changes = new DiffEngine().Diff(Build("create schema e; " + objects), Build("create schema e; " + objects + grant));

        Assert.AreEqual(1, changes.Added.Count(static added => added.Node is GrantDefinition), $"{objects} {grant}");
    }

    // ---- helpers ----

    private static void AssertNoGrantChange(DatabaseModel live, DatabaseModel project)
    {
        var changes = new DiffEngine().Diff(live, project);
        Assert.AreEqual(0, GrantChangeCount(changes), Describe(changes));
    }

    private static int GrantChangeCount(ChangeSet changes)
        => changes.Added.Count(static added => added.Node is GrantDefinition)
            + changes.Removed.Count(static removed => removed.NodeKind == ChangeNodeKind.Grant)
            + changes.Modified.Count(static modified => modified.NewNode is GrantDefinition);

    private static string Describe(ChangeSet changes)
        => "added: " + string.Join("; ", changes.Added.Where(static added => added.Node is GrantDefinition).Select(static added => added.NodeId))
            + " | removed: " + string.Join("; ", changes.Removed.Where(static removed => removed.NodeKind == ChangeNodeKind.Grant).Select(static removed => removed.NodeId))
            + " | modified: " + string.Join("; ", changes.Modified.Where(static modified => modified.NewNode is GrantDefinition).Select(static modified => modified.NodeId));

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
