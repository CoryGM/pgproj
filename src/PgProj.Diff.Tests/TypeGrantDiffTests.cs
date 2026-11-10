using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-337: privileges on types and domains are compared. A grant to a role is compared by what it grants, as the other kinds' are; PostgreSQL's default, <c>USAGE</c>
/// for <c>PUBLIC</c>, is implicit, and whether a type lacks it is a revoked default compared by its key (option (B)). Both directions: the same privileges written
/// another way are no change, and a real difference is reported. A type's privileges have no order, so there is no reordering case.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class TypeGrantDiffTests
{
    private const string Types = "CREATE SCHEMA app; CREATE TYPE app.mood AS ENUM ('a'); CREATE DOMAIN app.pos AS integer; ";

    [TestMethod]
    [DataRow("GRANT USAGE ON TYPE app.mood TO r;", "GRANT ALL ON TYPE app.mood TO r;")]
    [DataRow("GRANT USAGE ON DOMAIN app.pos TO r;", "GRANT USAGE ON TYPE app.pos TO r;")]
    [DataRow("GRANT USAGE ON TYPE app.mood TO r1, r2;", "GRANT USAGE ON TYPE app.mood TO r2; GRANT USAGE ON TYPE app.mood TO r1;")]
    [DataRow("GRANT USAGE ON TYPE app.MOOD TO r;", "GRANT USAGE ON TYPE app.mood TO r;")]
    [DataRow("REVOKE USAGE ON TYPE app.mood FROM PUBLIC;", "REVOKE ALL ON TYPE app.mood FROM public;")]
    [DataRow("REVOKE USAGE ON DOMAIN app.pos FROM PUBLIC;", "REVOKE ALL ON TYPE app.pos FROM PUBLIC;")]
    [DataRow("", "GRANT USAGE ON TYPE app.mood TO PUBLIC;")] // the default, written or not
    public void TheSamePrivilegesWrittenDifferentlyAreNoChange(string one, string other)
    {
        AssertNoGrantChange(Build(Types + one), Build(Types + other));
        AssertNoGrantChange(Build(Types + other), Build(Types + one));
    }

    [TestMethod]
    public void EveryFormAgainstItselfIsNoChange()
    {
        var model = Build(Types
            + "REVOKE ALL ON TYPE app.mood FROM PUBLIC; GRANT USAGE ON TYPE app.mood TO r1 WITH GRANT OPTION;"
            + "REVOKE USAGE ON DOMAIN app.pos FROM PUBLIC; GRANT USAGE ON DOMAIN app.pos TO r1, r2;");

        AssertNoGrantChange(model, model);
    }

    [TestMethod]
    public void ARevokedDefaultOnlyTheProjectHasIsAdded()
    {
        var changes = new DiffEngine().Diff(Build(Types), Build(Types + "REVOKE ALL ON TYPE app.mood FROM PUBLIC;"));

        var added = changes.Added.Single(static added => added.Node is GrantDefinition);
        Assert.IsTrue(((GrantDefinition)added.Node).RevokesDefault);
        Assert.AreEqual(1, GrantChangeCount(changes), Describe(changes));
    }

    [TestMethod]
    public void ARevokedDefaultOnlyTheDatabaseHasIsRemoved()
    {
        var changes = new DiffEngine().Diff(Build(Types + "REVOKE ALL ON TYPE app.mood FROM PUBLIC;"), Build(Types));

        var removed = changes.Removed.Single(static removed => removed.NodeKind == ChangeNodeKind.Grant);
        Assert.IsTrue(((GrantDefinition)removed.Node!).RevokesDefault);
        Assert.AreEqual(1, GrantChangeCount(changes), Describe(changes));
    }

    [TestMethod]
    public void ARevokedDefaultOnATypeTheProjectDropsIsNotGivenBack()
    {
        var changes = new DiffEngine().Diff(Build(Types + "REVOKE ALL ON TYPE app.mood FROM PUBLIC;"), Build("CREATE SCHEMA app; CREATE DOMAIN app.pos AS integer;"));

        Assert.AreEqual(0, GrantChangeCount(changes), "Dropping the type takes its privileges with it.\n" + Describe(changes));
        Assert.IsTrue(changes.Removed.Any(static removed => removed.NodeKind == ChangeNodeKind.Type));
    }

    [TestMethod]
    [DataRow("GRANT USAGE ON TYPE app.mood TO r;", "GRANT USAGE ON TYPE app.mood TO r WITH GRANT OPTION;")]
    [DataRow("GRANT USAGE ON TYPE app.mood TO r;", "GRANT USAGE ON TYPE app.mood TO other;")]
    [DataRow("GRANT USAGE ON TYPE app.mood TO r;", "GRANT USAGE ON TYPE app.pos TO r;")]
    [DataRow("", "GRANT USAGE ON DOMAIN app.pos TO r;")]
    [DataRow("REVOKE ALL ON TYPE app.mood FROM PUBLIC;", "REVOKE ALL ON TYPE app.pos FROM PUBLIC;")]
    public void ARealDifferenceIsReported(string live, string project)
    {
        var changes = new DiffEngine().Diff(Build(Types + live), Build(Types + project));

        Assert.IsTrue(GrantChangeCount(changes) > 0, $"{live} -> {project} must be a change.");
    }

    [TestMethod]
    public void AGrantToARoleIsNotARevokedDefault()
    {
        // The two are separate things: revoking PUBLIC's default does not take a role's grant away, nor the other way round.
        var changes = new DiffEngine().Diff(
            Build(Types + "REVOKE ALL ON TYPE app.mood FROM PUBLIC; GRANT USAGE ON TYPE app.mood TO r;"),
            Build(Types + "GRANT USAGE ON TYPE app.mood TO r;"));

        var removed = changes.Removed.Single(static removed => removed.NodeKind == ChangeNodeKind.Grant);
        Assert.IsTrue(((GrantDefinition)removed.Node!).RevokesDefault, Describe(changes));
        Assert.AreEqual(1, GrantChangeCount(changes), Describe(changes));
    }

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
