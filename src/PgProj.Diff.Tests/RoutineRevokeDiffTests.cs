using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-338: a routine's revoked default, and a rule that creates routines or types without <c>PUBLIC</c>'s default, compared by what they name. One only the project
/// has is added, one only the database has is removed (<c>PUBLIC</c> gets its privilege back), except on a routine the project drops, and except where a revoke
/// rule of the project allows it (part (i), tolerant like G-339). A rule <c>FOR ROLE</c> the role that deploys is the same as one without.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RoutineRevokeDiffTests
{
    private const string Objects = "CREATE SCHEMA app; CREATE FUNCTION app.f() RETURNS integer LANGUAGE sql AS $$ SELECT 1 $$; CREATE PROCEDURE app.p() LANGUAGE sql AS $$ SELECT 1 $$; CREATE TYPE app.mood AS ENUM ('a'); ";
    private const string RevokeF = "REVOKE ALL ON FUNCTION app.f() FROM PUBLIC;";
    private const string FunctionsRule = "ALTER DEFAULT PRIVILEGES REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;";

    [TestMethod]
    [DataRow("REVOKE EXECUTE ON FUNCTION app.f() FROM PUBLIC;", "REVOKE ALL ON FUNCTION app.f() FROM public;")]
    [DataRow("REVOKE ALL ON PROCEDURE app.p() FROM PUBLIC;", "REVOKE EXECUTE ON PROCEDURE app.p() FROM PUBLIC;")]
    [DataRow("ALTER DEFAULT PRIVILEGES REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;", "ALTER DEFAULT PRIVILEGES REVOKE ALL ON ROUTINES FROM PUBLIC;")]
    [DataRow("", "GRANT EXECUTE ON FUNCTION app.f() TO PUBLIC;")] // the default, written or not
    public void TheSameWrittenDifferentlyIsNoChange(string one, string other)
    {
        Assert.AreEqual(0, GrantChanges(Build(Objects + one), Build(Objects + other)));
        Assert.AreEqual(0, GrantChanges(Build(Objects + other), Build(Objects + one)));
    }

    [TestMethod]
    [DataRow(RevokeF)]
    [DataRow("REVOKE ALL ON PROCEDURE app.p() FROM PUBLIC;")]
    [DataRow(FunctionsRule)]
    [DataRow("ALTER DEFAULT PRIVILEGES FOR ROLE o REVOKE USAGE ON TYPES FROM PUBLIC;")]
    public void OneOnlyTheProjectHasIsAddedAndOneOnlyTheDatabaseHasIsRemoved(string revoke)
    {
        var added = new DiffEngine().Diff(Build(Objects), Build(Objects + revoke));
        Assert.IsTrue(((GrantDefinition)added.Added.Single(static change => change.Node is GrantDefinition).Node).RevokesDefault);
        Assert.AreEqual(1, GrantChanges(added));

        var removed = new DiffEngine().Diff(Build(Objects + revoke), Build(Objects));
        Assert.IsTrue(((GrantDefinition)removed.Removed.Single(static change => change.NodeKind == ChangeNodeKind.Grant).Node!).RevokesDefault);
        Assert.AreEqual(1, GrantChanges(removed));
    }

    [TestMethod]
    public void ARevokeOnARoutineTheProjectDropsGoesWithIt()
    {
        var changes = new DiffEngine().Diff(Build(Objects + RevokeF), Build("CREATE SCHEMA app; CREATE PROCEDURE app.p() LANGUAGE sql AS $$ SELECT 1 $$; CREATE TYPE app.mood AS ENUM ('a'); "));

        Assert.AreEqual(0, changes.Removed.Count(static change => change.NodeKind == ChangeNodeKind.Grant));
    }

    [TestMethod]
    public void ARuleForTheRoleThatDeploysIsTheSameAsOneWithoutForRole()
    {
        var live = Build(Objects + FunctionsRule);
        live.ConnectedRole = "deployer";

        Assert.AreEqual(0, GrantChanges(new DiffEngine().Diff(live, Build(Objects + "ALTER DEFAULT PRIVILEGES FOR ROLE deployer REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;"))));
        Assert.AreEqual(2, GrantChanges(new DiffEngine().Diff(live, Build(Objects + "ALTER DEFAULT PRIVILEGES FOR ROLE other REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;"))));
    }

    [TestMethod]
    [DataRow(FunctionsRule, RevokeF)]
    [DataRow(FunctionsRule, "REVOKE ALL ON PROCEDURE app.p() FROM PUBLIC;")]
    [DataRow("ALTER DEFAULT PRIVILEGES FOR ROLE o REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;", RevokeF)]
    [DataRow("ALTER DEFAULT PRIVILEGES REVOKE USAGE ON TYPES FROM PUBLIC;", "REVOKE ALL ON TYPE app.mood FROM PUBLIC;")]
    public void UnderTheProjectsRuleAnObjectWithOrWithoutTheDefaultIsNoChange(string rule, string revoke)
    {
        Assert.AreEqual(0, GrantChanges(Build(Objects + rule + revoke), Build(Objects + rule)), "Revoked, as the rule creates it: allowed.");
        Assert.AreEqual(0, GrantChanges(Build(Objects + rule), Build(Objects + rule)), "With the default, created before the rule: allowed.");
    }

    [TestMethod]
    public void UnderTheProjectsRuleARevokeTheProjectStatesIsStillAdded()
    {
        var changes = new DiffEngine().Diff(Build(Objects + FunctionsRule), Build(Objects + FunctionsRule + RevokeF));

        Assert.AreEqual("function:app.f()", ((GrantDefinition)changes.Added.Single(static change => change.Node is GrantDefinition).Node).ObjectKey);
    }

    [TestMethod]
    [DataRow("ALTER DEFAULT PRIVILEGES REVOKE USAGE ON TYPES FROM PUBLIC;", "a rule on types does not cover a routine")]
    [DataRow("ALTER DEFAULT PRIVILEGES GRANT EXECUTE ON FUNCTIONS TO r;", "a rule that grants does not revoke")]
    public void AnotherRuleDoesNotAllowARoutinesRevoke(string rule, string why)
    {
        Assert.AreEqual(1, GrantChanges(Build(Objects + rule + RevokeF), Build(Objects + rule)), why);
    }

    private static int GrantChanges(DatabaseModel live, DatabaseModel project) => GrantChanges(new DiffEngine().Diff(live, project));

    private static int GrantChanges(ChangeSet changes)
        => changes.Added.Count(static added => added.Node is GrantDefinition) + changes.Removed.Count(static removed => removed.NodeKind == ChangeNodeKind.Grant);

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
