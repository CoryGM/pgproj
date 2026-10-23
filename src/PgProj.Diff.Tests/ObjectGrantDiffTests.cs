using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-65: grants on individual objects, several grantees, grant options, columns, and role membership are compared by what they
/// grant. PostgreSQL records grants per object, so a project that writes <c>ALL TABLES IN SCHEMA</c>, a list of tables, or
/// one statement per table must read back as the same thing (no phantom diff), and a real difference must still be reported.
/// Grants have no order-sensitive parts, so there is no reordering case.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ObjectGrantDiffTests
{
    private const string Objects =
        "create schema s; create table s.a (id integer, name text); create table s.b (id integer); create view s.v as select 1 as n; "
        + "create sequence s.q; create function s.f(x integer) returns integer language sql as $$ select x $$; ";

    // ---- the same grants, written differently, are no change ----

    [TestMethod]
    [DataRow("GRANT SELECT ON s.a, s.b, s.v TO r;", "GRANT SELECT ON ALL TABLES IN SCHEMA s TO r;")]
    [DataRow("GRANT SELECT ON s.a TO r; GRANT SELECT ON s.b TO r; GRANT SELECT ON s.v TO r;", "GRANT SELECT ON ALL TABLES IN SCHEMA s TO r;")]
    [DataRow("GRANT SELECT ON s.a TO r1, r2;", "GRANT SELECT ON s.a TO r1; GRANT SELECT ON s.a TO r2;")]
    [DataRow("GRANT SELECT, INSERT ON s.a TO r;", "GRANT INSERT ON s.a TO r; GRANT SELECT ON s.a TO r;")]
    [DataRow("GRANT SELECT (id, name) ON s.a TO r;", "GRANT SELECT (name, id) ON s.a TO r;")]
    [DataRow("GRANT SELECT (id) ON s.a TO r; GRANT SELECT (name) ON s.a TO r;", "GRANT SELECT (id, name) ON s.a TO r;")]
    [DataRow("GRANT ALL ON s.a TO r;", "GRANT SELECT, INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER ON s.a TO r;")]
    [DataRow("GRANT SELECT ON TABLE s.a TO r;", "GRANT SELECT ON s.a TO r;")]
    [DataRow("GRANT EXECUTE ON FUNCTION s.f(integer) TO r;", "GRANT EXECUTE ON FUNCTION s.f(int4) TO r;")]
    [DataRow("GRANT USAGE, SELECT, UPDATE ON SEQUENCE s.q TO r;", "GRANT ALL ON SEQUENCE s.q TO r;")]
    public void TheSameGrantsWrittenDifferentlyProduceNoChange(string one, string other)
    {
        AssertNoGrantChange(Build(Objects + one), Build(Objects + other));
        AssertNoGrantChange(Build(Objects + other), Build(Objects + one));
    }

    [TestMethod]
    public void EveryFormAgainstItselfIsNoChange()
    {
        var model = Build(Objects
            + "GRANT SELECT ON s.a TO r1, r2 WITH GRANT OPTION;"
            + "GRANT SELECT (id) ON s.a TO r3;"
            + "GRANT USAGE ON SEQUENCE s.q TO r1;"
            + "GRANT EXECUTE ON FUNCTION s.f(integer) TO r1;"
            + "GRANT reader TO r1 WITH ADMIN OPTION;"
            + "ALTER DEFAULT PRIVILEGES FOR ROLE o IN SCHEMA s GRANT SELECT ON TABLES TO r1;"
            + "ALTER DEFAULT PRIVILEGES GRANT USAGE ON TYPES TO r1;"
            + "GRANT SELECT ON ALL TABLES IN SCHEMA s TO r3;");

        AssertNoGrantChange(model, model);
    }

    [TestMethod]
    public void StatementOrderDoesNotMatter()
    {
        var forward = Build(Objects + "GRANT SELECT ON s.a TO r; GRANT USAGE ON SEQUENCE s.q TO r; GRANT reader TO r;");
        var backward = Build(Objects + "GRANT reader TO r; GRANT USAGE ON SEQUENCE s.q TO r; GRANT SELECT ON s.a TO r;");

        AssertNoGrantChange(forward, backward);
    }

    /// <summary>A grant the live loader reports per table, against a project that grants over the whole schema, and the reverse.</summary>
    [TestMethod]
    public void PerObjectLiveGrantsMatchAProjectGrantOverTheSchema()
    {
        var live = Build(Objects + "GRANT SELECT ON s.a TO r; GRANT SELECT ON s.b TO r; GRANT SELECT ON s.v TO r;");
        var project = Build(Objects + "GRANT SELECT ON ALL TABLES IN SCHEMA s TO r;");

        AssertNoGrantChange(live, project);
    }

    /// <summary>The only table of a schema: the live loader reads it as ALL TABLES, the project wrote one table.</summary>
    [TestMethod]
    public void OneTableGrantMatchesTheAllTablesGrantOfASchemaWithOneTable()
    {
        const string objects = "create schema one; create table one.t (id integer); ";

        AssertNoGrantChange(
            Build(objects + "GRANT SELECT ON ALL TABLES IN SCHEMA one TO r;"),
            Build(objects + "GRANT SELECT ON one.t TO r;"));
    }

    [TestMethod]
    public void ImplicitPublicExecuteOnARoutineIsNotRevokedAndNotRequired()
    {
        AssertNoGrantChange(Build(Objects + "GRANT EXECUTE ON FUNCTION s.f(integer) TO PUBLIC;"), Build(Objects));
        AssertNoGrantChange(Build(Objects), Build(Objects + "GRANT EXECUTE ON FUNCTION s.f(integer) TO PUBLIC;"));
    }

    // ---- what differs is still reported ----

    [TestMethod]
    [DataRow("GRANT SELECT ON s.a TO r;", "GRANT INSERT ON s.a TO r;", DisplayName = "privilege")]
    [DataRow("GRANT SELECT ON s.a TO r1;", "GRANT SELECT ON s.a TO r2;", DisplayName = "grantee")]
    [DataRow("GRANT SELECT ON s.a TO r;", "GRANT SELECT ON s.b TO r;", DisplayName = "object")]
    [DataRow("GRANT SELECT ON s.a TO r WITH GRANT OPTION;", "GRANT SELECT ON s.a TO r;", DisplayName = "grant option added")]
    [DataRow("GRANT SELECT ON s.a TO r;", "GRANT SELECT ON s.a TO r WITH GRANT OPTION;", DisplayName = "grant option removed")]
    [DataRow("GRANT SELECT (id) ON s.a TO r;", "GRANT SELECT (name) ON s.a TO r;", DisplayName = "column")]
    [DataRow("GRANT SELECT (id) ON s.a TO r;", "GRANT SELECT ON s.a TO r;", DisplayName = "column versus whole table")]
    [DataRow("GRANT SELECT ON s.a TO r;", "GRANT SELECT ON ALL TABLES IN SCHEMA s TO r;", DisplayName = "one table versus all tables")]
    [DataRow("GRANT USAGE ON SEQUENCE s.q TO r;", "GRANT SELECT ON SEQUENCE s.q TO r;", DisplayName = "sequence privilege")]
    [DataRow("GRANT EXECUTE ON FUNCTION s.f(integer) TO r;", "GRANT EXECUTE ON FUNCTION s.f(integer) TO other;", DisplayName = "function grantee")]
    [DataRow("ALTER DEFAULT PRIVILEGES FOR ROLE a IN SCHEMA s GRANT SELECT ON TABLES TO r;", "ALTER DEFAULT PRIVILEGES FOR ROLE b IN SCHEMA s GRANT SELECT ON TABLES TO r;", DisplayName = "default privileges for role")]
    [DataRow("ALTER DEFAULT PRIVILEGES IN SCHEMA s GRANT USAGE ON TYPES TO r;", "ALTER DEFAULT PRIVILEGES IN SCHEMA s GRANT USAGE ON TYPES TO other;", DisplayName = "default types")]
    public void GenuinelyDifferentGrantsAreStillReported(string project, string live)
    {
        var changes = new DiffEngine().Diff(Build(Objects + live), Build(Objects + project));

        Assert.IsGreaterThan(0, GrantChangeCount(changes), $"{project} vs {live}");
    }

    // ---- default privileges of the connected role ----

    /// <summary>
    /// Default privileges written without FOR ROLE are the connected role's, so live reports them without it; a project that
    /// says FOR ROLE naming that role (what pg_dump writes) is the same grant. Before this a publish revoked and granted it every time.
    /// </summary>
    [TestMethod]
    [DataRow("ALTER DEFAULT PRIVILEGES FOR ROLE postgres IN SCHEMA s GRANT SELECT ON TABLES TO r;")]
    [DataRow("ALTER DEFAULT PRIVILEGES FOR ROLE POSTGRES IN SCHEMA s GRANT SELECT ON TABLES TO r;")]
    [DataRow("ALTER DEFAULT PRIVILEGES IN SCHEMA s GRANT SELECT ON TABLES TO r;")]
    public void DefaultPrivilegesForTheConnectedRoleAreTheSameWhetherOrNotTheRoleIsNamed(string project)
    {
        var live = Build(Objects + "ALTER DEFAULT PRIVILEGES IN SCHEMA s GRANT SELECT ON TABLES TO r;");
        live.ConnectedRole = "postgres";

        AssertNoGrantChange(live, Build(Objects + project));
    }

    [TestMethod]
    public void DefaultPrivilegesForAnotherRoleAreNotTheConnectedRolesEvenWhenLiveKnowsWhoItIs()
    {
        var live = Build(Objects + "ALTER DEFAULT PRIVILEGES IN SCHEMA s GRANT SELECT ON TABLES TO r;");
        live.ConnectedRole = "postgres";

        var changes = new DiffEngine().Diff(live, Build(Objects + "ALTER DEFAULT PRIVILEGES FOR ROLE other IN SCHEMA s GRANT SELECT ON TABLES TO r;"));

        Assert.AreEqual(1, changes.Added.Count(static added => added.Node is GrantDefinition));
        Assert.AreEqual(1, changes.Removed.Count(static removed => removed.NodeKind == ChangeNodeKind.Grant));
    }

    /// <summary>Without knowing who the live model was read as, FOR ROLE cannot be told from the connected role, so it is a difference.</summary>
    [TestMethod]
    public void WithoutAConnectedRoleForRoleIsADifference()
    {
        var live = Build(Objects + "ALTER DEFAULT PRIVILEGES IN SCHEMA s GRANT SELECT ON TABLES TO r;");

        var changes = new DiffEngine().Diff(live, Build(Objects + "ALTER DEFAULT PRIVILEGES FOR ROLE postgres IN SCHEMA s GRANT SELECT ON TABLES TO r;"));

        Assert.IsGreaterThan(0, GrantChangeCount(changes));
    }

    [TestMethod]
    public void GrantMissingFromLiveIsAddedOnceWithoutAnythingRemoved()
    {
        var changes = new DiffEngine().Diff(Build(Objects), Build(Objects + "GRANT SELECT ON s.a TO r1, r2;"));

        Assert.AreEqual(2, changes.Added.Count(static added => added.Node is GrantDefinition));
        Assert.AreEqual(0, changes.Removed.Count(static removed => removed.NodeKind == ChangeNodeKind.Grant));
    }

    [TestMethod]
    public void GrantThatLiveHasAndTheProjectDoesNotWantIsRevoked()
    {
        var changes = new DiffEngine().Diff(Build(Objects + "GRANT SELECT ON s.a TO r;"), Build(Objects));

        Assert.AreEqual(1, changes.Removed.Count(static removed => removed.NodeKind == ChangeNodeKind.Grant));
        Assert.AreEqual(0, changes.Added.Count(static added => added.Node is GrantDefinition));
    }

    /// <summary>Live gives more than the project wants: the whole grant is revoked and what the project wants is applied again.</summary>
    [TestMethod]
    public void LiveGrantWithMoreThanTheProjectWantsIsRevokedAndTheWantedPartReapplied()
    {
        var changes = new DiffEngine().Diff(
            Build(Objects + "GRANT SELECT, INSERT ON s.a TO r;"),
            Build(Objects + "GRANT SELECT ON s.a TO r;"));

        Assert.AreEqual(1, changes.Removed.Count(static removed => removed.NodeKind == ChangeNodeKind.Grant));
        Assert.AreEqual(1, changes.Added.Count(static added => added.Node is GrantDefinition));
    }

    [TestMethod]
    public void ANewTableIsCoveredByAnAllTablesGrantThatLiveAlreadyHasForTheOldOnes()
    {
        var live = Build("create schema s; create table s.a (id integer); GRANT SELECT ON ALL TABLES IN SCHEMA s TO r;");
        var project = Build("create schema s; create table s.a (id integer); create table s.b (id integer); GRANT SELECT ON ALL TABLES IN SCHEMA s TO r;");

        var changes = new DiffEngine().Diff(live, project);

        Assert.AreEqual(1, changes.Added.Count(static added => added.Node is GrantDefinition), "The grant must be applied again for the new table.");
        Assert.AreEqual(0, changes.Removed.Count(static removed => removed.NodeKind == ChangeNodeKind.Grant));
    }

    // ---- what is not revoked ----

    /// <summary>
    /// The table is being dropped and the project no longer grants on it, so live's grant is one to remove. It is revoked as before
    /// (a grant over all tables of a schema is too, G-61); the dependency graph puts the REVOKE ahead of the DROP.
    /// </summary>
    [TestMethod]
    public void GrantOnATableTheProjectNoLongerHasIsRevokedWhileTheGrantOnTheOneItKeepsIsNot()
    {
        var live = Build("create schema s; create table s.gone (id integer); create table s.kept (id integer); GRANT SELECT ON s.gone TO r; GRANT SELECT ON s.kept TO r;");
        var project = Build("create schema s; create table s.kept (id integer); GRANT SELECT ON s.kept TO r;");

        var changes = new DiffEngine().Diff(live, project);

        CollectionAssert.AreEqual(
            new[] { "s.table:s.gone->r:SELECT" },
            changes.Removed.Where(static removed => removed.NodeKind == ChangeNodeKind.Grant).Select(static removed => removed.NodeId).ToArray());
        Assert.AreEqual(0, changes.Added.Count(static added => added.Node is GrantDefinition));
    }

    [TestMethod]
    public void RoleMembershipThatLiveHasAndTheProjectDoesNotListIsNotRevoked()
    {
        AssertNoGrantChange(Build(Objects + "GRANT reader TO alice;"), Build(Objects));
    }

    [TestMethod]
    public void RoleMembershipTheProjectDeclaresIsAddedWhenLiveLacksItAndNotWhenItHasIt()
    {
        var changes = new DiffEngine().Diff(Build(Objects), Build(Objects + "GRANT reader TO alice;"));
        Assert.AreEqual(1, changes.Added.Count(static added => added.Node is GrantDefinition));

        AssertNoGrantChange(Build(Objects + "GRANT reader TO alice;"), Build(Objects + "GRANT reader TO alice;"));
    }

    [TestMethod]
    public void RoleMembershipWithAdminOptionIsNotThePlainMembership()
    {
        var changes = new DiffEngine().Diff(Build(Objects + "GRANT reader TO alice;"), Build(Objects + "GRANT reader TO alice WITH ADMIN OPTION;"));

        Assert.AreEqual(1, changes.Added.Count(static added => added.Node is GrantDefinition));
    }

    /// <summary>The output must not depend on the order the grants were written in.</summary>
    [TestMethod]
    public void ChangesAreReportedInADeterministicOrder()
    {
        var one = new DiffEngine().Diff(Build(Objects), Build(Objects + "GRANT SELECT ON s.a TO b; GRANT SELECT ON s.a TO a; GRANT USAGE ON SEQUENCE s.q TO a;"));
        var other = new DiffEngine().Diff(Build(Objects), Build(Objects + "GRANT USAGE ON SEQUENCE s.q TO a; GRANT SELECT ON s.a TO a; GRANT SELECT ON s.a TO b;"));

        CollectionAssert.AreEqual(
            one.Added.Select(static added => added.NodeId).ToArray(),
            other.Added.Select(static added => added.NodeId).ToArray());
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
