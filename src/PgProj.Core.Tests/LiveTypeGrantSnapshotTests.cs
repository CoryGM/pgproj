using PgProj.Core;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core.Tests;

/// <summary>
/// G-337: the statements the live loader writes for a type's or a domain's privileges (<c>ReadTypeGrantsAsync</c>) read back as the grants they stand for, through the
/// same parser as a project: a <c>GRANT USAGE ON TYPE|DOMAIN</c>, and <c>REVOKE USAGE ... FROM PUBLIC</c> as a revoked default. With grants left out, they are kept as
/// the type's unmodeled privileges, so a type rebuilt under its columns gets them back. (The catalog query is checked against a real server by the integration tests.)
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class LiveTypeGrantSnapshotTests
{
    [TestMethod]
    [DataRow("GRANT USAGE ON TYPE public.mood TO r", "r", false, false)]
    [DataRow("GRANT USAGE ON DOMAIN public.mood TO r WITH GRANT OPTION", "r", true, false)]
    [DataRow("GRANT USAGE ON TYPE \"public\".\"Mood x\" TO \"App Role\"", "App Role", false, false)]
    [DataRow("REVOKE USAGE ON TYPE public.mood FROM PUBLIC", "PUBLIC", false, true)]
    [DataRow("REVOKE USAGE ON DOMAIN public.mood FROM PUBLIC", "PUBLIC", false, true)]
    public void ATypeGrantRenderingReadsBackAsTheGrant(string statement, string grantee, bool grantOption, bool revokesDefault)
    {
        var grant = Load(new LiveGrantSnapshot("public", "type:public.mood()", grantee, statement)).Single();

        StringAssert.StartsWith(grant.ObjectKey, "type:public.");
        Assert.AreEqual(grantee, grant.Grantee);
        Assert.AreEqual("ALL PRIVILEGES", string.Join(", ", grant.Privileges));
        Assert.AreEqual(grantOption, grant.WithGrantOption);
        Assert.AreEqual(revokesDefault, grant.RevokesDefault);
    }

    [TestMethod]
    public void WithGrantsLeftOutATypesPrivilegesAreKeptAsItsOwn()
    {
        var privileges = LiveDatabaseModelLoader.ToUnmodeledPrivileges(
        [
            new LiveGrantSnapshot("app", "type:app.mood()", "PUBLIC", "REVOKE USAGE ON TYPE app.mood FROM PUBLIC"),
            new LiveGrantSnapshot("app", "type:app.mood()", "r", "GRANT USAGE ON TYPE app.mood TO r"),
        ]);

        Assert.HasCount(2, privileges);
        Assert.IsTrue(privileges.All(static privilege => privilege is { ObjectKind: "type", SchemaName: "app", Name: "mood" }));
        CollectionAssert.AreEqual(new[] { "REVOKE USAGE ON TYPE app.mood FROM PUBLIC", "GRANT USAGE ON TYPE app.mood TO r" }, privileges.Select(static privilege => privilege.Statement).ToArray());
    }

    private static IReadOnlyList<GrantDefinition> Load(LiveGrantSnapshot snapshot)
    {
        var loader = new LiveDatabaseModelLoader(new PgQueryParser(), new SemanticBuilder());
        var sql = LiveDatabaseModelLoader.GenerateSnapshotSql([new LiveSchemaSnapshot("public")], [], [], [], grants: [snapshot]);
        return loader.BuildModelFromSnapshotSql(sql).Grants.Values.ToArray();
    }
}
