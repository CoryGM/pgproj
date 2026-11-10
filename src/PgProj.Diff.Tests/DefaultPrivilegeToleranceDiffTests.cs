using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-339, option (c): a privilege one of the project's default-privilege rules would give an object is allowed and not required. An object that has it is no change
/// (the publish that created it under the rule used to report it as one to revoke), nor is one that lacks it (a rule is not retroactive). It does not hide a
/// real difference: a privilege no rule gives, a rule of another schema or kind, another grant option, and a rule the project dropped are still reported.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class DefaultPrivilegeToleranceDiffTests
{
    private const string Objects =
        "CREATE SCHEMA app; CREATE SCHEMA other; CREATE TABLE app.t (id integer); CREATE TABLE other.t (id integer); CREATE SEQUENCE app.q; "
        + "CREATE FUNCTION app.f() RETURNS integer LANGUAGE sql AS $$ SELECT 1 $$; CREATE TYPE app.mood AS ENUM ('a'); ";

    [TestMethod]
    [DataRow("ALTER DEFAULT PRIVILEGES IN SCHEMA app GRANT SELECT ON TABLES TO r;", "GRANT SELECT ON app.t TO r;")]
    [DataRow("ALTER DEFAULT PRIVILEGES GRANT SELECT ON TABLES TO r;", "GRANT SELECT ON app.t TO r;")]
    [DataRow("ALTER DEFAULT PRIVILEGES FOR ROLE o IN SCHEMA app GRANT SELECT ON TABLES TO r;", "GRANT SELECT ON app.t TO r;")]
    [DataRow("ALTER DEFAULT PRIVILEGES IN SCHEMA app GRANT ALL ON TABLES TO r;", "GRANT SELECT, INSERT ON app.t TO r;")]
    [DataRow("ALTER DEFAULT PRIVILEGES IN SCHEMA app GRANT USAGE ON SEQUENCES TO r;", "GRANT USAGE ON SEQUENCE app.q TO r;")]
    [DataRow("ALTER DEFAULT PRIVILEGES IN SCHEMA app GRANT EXECUTE ON FUNCTIONS TO r;", "GRANT EXECUTE ON FUNCTION app.f() TO r;")]
    [DataRow("ALTER DEFAULT PRIVILEGES IN SCHEMA app GRANT USAGE ON TYPES TO r;", "GRANT USAGE ON TYPE app.mood TO r;")]
    [DataRow("ALTER DEFAULT PRIVILEGES IN SCHEMA app GRANT SELECT ON TABLES TO PUBLIC;", "GRANT SELECT ON app.t TO PUBLIC;")]
    [DataRow("ALTER DEFAULT PRIVILEGES IN SCHEMA app GRANT SELECT ON TABLES TO r WITH GRANT OPTION;", "GRANT SELECT ON app.t TO r WITH GRANT OPTION;")]
    public void WhatARuleGivesIsAllowedAndNotRequired(string rule, string given)
    {
        var project = Build(Objects + rule);

        AssertNoGrantChange(Build(Objects + rule + given), project);
        AssertNoGrantChange(Build(Objects + rule), project);
    }

    [TestMethod]
    [DataRow("ALTER DEFAULT PRIVILEGES IN SCHEMA app GRANT SELECT ON TABLES TO r;", "GRANT INSERT ON app.t TO r;", "a privilege no rule gives")]
    [DataRow("ALTER DEFAULT PRIVILEGES IN SCHEMA app GRANT SELECT ON TABLES TO r;", "GRANT SELECT ON other.t TO r;", "another schema's table")]
    [DataRow("ALTER DEFAULT PRIVILEGES IN SCHEMA app GRANT SELECT ON TABLES TO r;", "GRANT SELECT ON app.t TO other_role;", "another grantee")]
    [DataRow("ALTER DEFAULT PRIVILEGES IN SCHEMA app GRANT SELECT ON TABLES TO r;", "GRANT SELECT ON app.t TO r WITH GRANT OPTION;", "another grant option")]
    [DataRow("ALTER DEFAULT PRIVILEGES IN SCHEMA app GRANT SELECT ON TABLES TO r;", "GRANT USAGE ON SEQUENCE app.q TO r;", "another kind")]
    [DataRow("ALTER DEFAULT PRIVILEGES IN SCHEMA app GRANT SELECT ON TABLES TO r;", "GRANT SELECT (id) ON app.t TO r;", "a column privilege")]
    public void WhatNoRuleGivesIsStillRevoked(string rule, string extra, string why)
    {
        var changes = new DiffEngine().Diff(Build(Objects + rule + extra), Build(Objects + rule));

        Assert.AreEqual(1, changes.Removed.Count(static removed => removed.NodeKind == ChangeNodeKind.Grant), why);
    }

    [TestMethod]
    public void ARuleTheProjectDropsNoLongerCoversWhatItGave()
    {
        const string rule = "ALTER DEFAULT PRIVILEGES IN SCHEMA app GRANT SELECT ON TABLES TO r;";
        var changes = new DiffEngine().Diff(Build(Objects + rule + "GRANT SELECT ON app.t TO r;"), Build(Objects));

        var revoked = changes.Removed.Where(static removed => removed.NodeKind == ChangeNodeKind.Grant).Select(static removed => ((GrantDefinition)removed.Node!).ObjectKey).Order(StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(new[] { "default tables in schema:app", "table:app.t" }, revoked);
    }

    [TestMethod]
    public void AGrantTheProjectStatesIsStillAddedWhereARuleCouldGiveIt()
    {
        const string rule = "ALTER DEFAULT PRIVILEGES IN SCHEMA app GRANT SELECT ON TABLES TO r;";
        var changes = new DiffEngine().Diff(Build(Objects + rule), Build(Objects + rule + "GRANT SELECT ON app.t TO r;"));

        Assert.AreEqual("table:app.t", ((GrantDefinition)changes.Added.Single(static added => added.Node is GrantDefinition).Node).ObjectKey, "Stated is wanted.");
    }

    private static void AssertNoGrantChange(DatabaseModel live, DatabaseModel project)
    {
        var changes = new DiffEngine().Diff(live, project);
        var count = changes.Added.Count(static added => added.Node is GrantDefinition) + changes.Removed.Count(static removed => removed.NodeKind == ChangeNodeKind.Grant);
        Assert.AreEqual(0, count, string.Join("; ", changes.Added.Select(static added => "+" + added.NodeId).Concat(changes.Removed.Select(static removed => "-" + removed.NodeId))));
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
