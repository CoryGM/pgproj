using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-341: the <c>public</c> schema's revoked defaults, one per privilege. <c>USAGE</c> is compared both ways (G-337's option (B)): one only the project has is added,
/// one only the database has is removed (<c>PUBLIC</c> gets it back). <c>CREATE</c>, <c>PUBLIC</c>'s default there only before PostgreSQL 15, is compared only when
/// the project states it: added when only the project has it, never removed.
/// </summary>
[TestClass]
public sealed class PublicSchemaRevokeDiffTests
{
    private const string RevokeUsage = "REVOKE USAGE ON SCHEMA public FROM PUBLIC;";
    private const string RevokeCreate = "REVOKE CREATE ON SCHEMA public FROM PUBLIC;";

    [TestMethod]
    [DataRow("REVOKE ALL ON SCHEMA public FROM PUBLIC;", "REVOKE USAGE ON SCHEMA public FROM public; REVOKE CREATE ON SCHEMA public FROM PUBLIC;")]
    [DataRow("REVOKE USAGE, CREATE ON SCHEMA public FROM PUBLIC;", "REVOKE ALL PRIVILEGES ON SCHEMA public FROM PUBLIC;")]
    [DataRow(RevokeUsage, "REVOKE usage ON SCHEMA PUBLIC FROM public;")]
    [DataRow("", "GRANT USAGE ON SCHEMA public TO PUBLIC;")] // the default, written or not
    public void TheSameWrittenDifferentlyIsNoChange(string one, string other)
    {
        Assert.AreEqual(0, GrantChanges(Build(one), Build(other)));
        Assert.AreEqual(0, GrantChanges(Build(other), Build(one)));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(RevokeUsage)]
    [DataRow("REVOKE ALL ON SCHEMA public FROM PUBLIC;")]
    public void AModelAgainstItselfIsNoChange(string sql)
    {
        Assert.AreEqual(0, GrantChanges(Build(sql), Build(sql)));
    }

    [TestMethod]
    public void AUsageRevokeOnlyTheProjectHasIsAddedAndOneOnlyTheDatabaseHasIsRemoved()
    {
        var added = new DiffEngine().Diff(Build(""), Build(RevokeUsage));
        var addedGrant = (GrantDefinition)added.Added.Single(static change => change.Node is GrantDefinition).Node;
        Assert.IsTrue(addedGrant.RevokesDefault);
        Assert.AreEqual("USAGE", addedGrant.Privileges.Single());
        Assert.AreEqual(1, GrantChanges(added));

        var removed = new DiffEngine().Diff(Build(RevokeUsage), Build(""));
        Assert.IsTrue(((GrantDefinition)removed.Removed.Single(static change => change.NodeKind == ChangeNodeKind.Grant).Node!).RevokesDefault);
        Assert.AreEqual(1, GrantChanges(removed));
    }

    [TestMethod]
    public void ACreateRevokeIsAddedWhenTheProjectStatesItAndNeverRemoved()
    {
        var added = new DiffEngine().Diff(Build(""), Build(RevokeCreate));
        Assert.AreEqual("CREATE", ((GrantDefinition)added.Added.Single(static change => change.Node is GrantDefinition).Node).Privileges.Single());
        Assert.AreEqual(1, GrantChanges(added));

        Assert.AreEqual(0, GrantChanges(Build(RevokeCreate), Build("")), "A project silent about CREATE does not give it back to PUBLIC.");
        Assert.AreEqual(0, GrantChanges(Build(RevokeCreate), Build(RevokeCreate)));
    }

    [TestMethod]
    public void RevokeAllAgainstADatabaseWithoutOnlyUsageAddsUsageAlone()
    {
        // A database on PostgreSQL 15 or later reads as without CREATE; one that also revoked USAGE then matches REVOKE ALL.
        var changes = new DiffEngine().Diff(Build(RevokeCreate), Build("REVOKE ALL ON SCHEMA public FROM PUBLIC;"));

        Assert.AreEqual("USAGE", ((GrantDefinition)changes.Added.Single(static change => change.Node is GrantDefinition).Node).Privileges.Single());
        Assert.AreEqual(1, GrantChanges(changes));
        Assert.AreEqual(0, GrantChanges(Build("REVOKE ALL ON SCHEMA public FROM PUBLIC;"), Build("REVOKE ALL ON SCHEMA public FROM PUBLIC;")));
    }

    [TestMethod]
    public void ARevokeOnPublicIsNotAGrantOnAnotherSchema()
    {
        const string App = "CREATE SCHEMA app; ";

        Assert.AreEqual(1, GrantChanges(Build(App), Build(App + RevokeUsage)));
        Assert.AreEqual(1, GrantChanges(Build(App + "GRANT USAGE ON SCHEMA app TO PUBLIC;"), Build(App)), "PUBLIC's USAGE on another schema is a grant, revoked as before.");
    }

    private static int GrantChanges(DatabaseModel live, DatabaseModel project) => GrantChanges(new DiffEngine().Diff(live, project));

    private static int GrantChanges(ChangeSet changes)
        => changes.Added.Count(static added => added.Node is GrantDefinition) + changes.Removed.Count(static removed => removed.NodeKind == ChangeNodeKind.Grant);

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
