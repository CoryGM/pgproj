using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-340: a project that states <c>PUBLIC</c>'s built-in privilege in a global rule on types or functions asks for what PostgreSQL does anyway, and the live read
/// leaves that entry out, so it is no change either way. A rule granting to <c>PUBLIC</c> in one schema, or on tables, is still compared.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class DefaultPrivilegePublicDefaultDiffTests
{
    private const string Schema = "CREATE SCHEMA app; ";

    [TestMethod]
    [DataRow("ALTER DEFAULT PRIVILEGES GRANT USAGE ON TYPES TO PUBLIC;")]
    [DataRow("ALTER DEFAULT PRIVILEGES GRANT EXECUTE ON FUNCTIONS TO PUBLIC;")]
    [DataRow("ALTER DEFAULT PRIVILEGES FOR ROLE o GRANT USAGE ON TYPES TO PUBLIC;")]
    public void PublicsBuiltInPrivilegeInAGlobalRuleIsNoChange(string rule)
    {
        Assert.AreEqual(0, GrantChanges(Build(Schema), Build(Schema + rule)), "Stated, it asks for nothing.");
        Assert.AreEqual(0, GrantChanges(Build(Schema + rule), Build(Schema)), "Read, it is not a rule to revoke.");
    }

    [TestMethod]
    public void AGlobalRuleOnTypesForAnotherGranteeIsStillCompared()
    {
        Assert.AreEqual(1, GrantChanges(Build(Schema), Build(Schema + "ALTER DEFAULT PRIVILEGES GRANT USAGE ON TYPES TO r;")));
    }

    [TestMethod]
    [DataRow("ALTER DEFAULT PRIVILEGES IN SCHEMA app GRANT USAGE ON TYPES TO PUBLIC;")]
    [DataRow("ALTER DEFAULT PRIVILEGES IN SCHEMA app GRANT EXECUTE ON FUNCTIONS TO PUBLIC;")]
    [DataRow("ALTER DEFAULT PRIVILEGES GRANT SELECT ON TABLES TO PUBLIC;")]
    public void ARuleGrantingToPublicThatIsNotTheDefaultIsStillCompared(string rule)
    {
        Assert.AreEqual(1, GrantChanges(Build(Schema), Build(Schema + rule)), "Added.");
        Assert.AreEqual(1, GrantChanges(Build(Schema + rule), Build(Schema)), "Removed.");
    }

    private static int GrantChanges(DatabaseModel live, DatabaseModel project)
    {
        var changes = new DiffEngine().Diff(live, project);
        return changes.Added.Count(static added => added.Node is GrantDefinition) + changes.Removed.Count(static removed => removed.NodeKind == ChangeNodeKind.Grant);
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
