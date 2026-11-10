using PgProj.Core;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core.Tests;

/// <summary>
/// G-338: the statements the live loader writes for a routine without <c>PUBLIC</c>'s <c>EXECUTE</c> (<c>ReadRoutineRevokedDefaultsAsync</c>) and for a rule that
/// creates routines or types without it (<c>ReadDefaultPrivilegeRevokedDefaultsAsync</c>) read back as revoked defaults, through the same parser as a project. With
/// grants left out, a routine's is kept as its unmodeled privilege, so a routine dropped and created again gets it back. (The catalog queries are checked against a
/// real server by the integration tests.)
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class LiveRoutineRevokeSnapshotTests
{
    [TestMethod]
    [DataRow("function:public.f(a integer, b text)", "REVOKE ALL ON FUNCTION public.f(integer, text) FROM PUBLIC", "function:public.f(int4,text)", null)]
    [DataRow("procedure:public.p()", "REVOKE ALL ON PROCEDURE \"public\".\"p\"() FROM PUBLIC", "procedure:public.p()", null)]
    [DataRow("default functions", "ALTER DEFAULT PRIVILEGES REVOKE ALL ON FUNCTIONS FROM PUBLIC", "default functions", null)]
    [DataRow("default types", "ALTER DEFAULT PRIVILEGES FOR ROLE \"App Owner\" REVOKE ALL ON TYPES FROM PUBLIC", "default types", "App Owner")]
    public void ARevokedDefaultRenderingReadsBackAsARevokedDefault(string snapshotKey, string statement, string objectKey, string? forRole)
    {
        var grant = Load(new LiveGrantSnapshot(snapshotKey.StartsWith("default", StringComparison.Ordinal) ? string.Empty : "public", snapshotKey, "PUBLIC", statement)).Single();

        Assert.IsTrue(grant.RevokesDefault);
        Assert.AreEqual(objectKey, grant.ObjectKey);
        Assert.AreEqual(forRole, grant.ForRole);
    }

    [TestMethod]
    public void WithGrantsLeftOutARoutinesRevokeIsKeptAsItsOwn()
    {
        var privilege = LiveDatabaseModelLoader.ToUnmodeledPrivileges(
            [new LiveGrantSnapshot("app", "function:app.f(a integer)", "PUBLIC", "REVOKE ALL ON FUNCTION app.f(integer) FROM PUBLIC")]).Single();

        Assert.AreEqual("function", privilege.ObjectKind);
        Assert.AreEqual("app", privilege.SchemaName);
        Assert.AreEqual("f", privilege.Name);
    }

    private static IReadOnlyList<GrantDefinition> Load(LiveGrantSnapshot snapshot)
    {
        var loader = new LiveDatabaseModelLoader(new PgQueryParser(), new SemanticBuilder());
        var sql = LiveDatabaseModelLoader.GenerateSnapshotSql([new LiveSchemaSnapshot("public")], [], [], [], grants: [snapshot]);
        return loader.BuildModelFromSnapshotSql(sql).Grants.Values.ToArray();
    }
}
