using PgProj.Core;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core.Tests;

/// <summary>
/// G-341: the statements the live loader writes for the <c>public</c> schema without <c>PUBLIC</c>'s <c>USAGE</c> or <c>CREATE</c>
/// (<c>ReadPublicSchemaRevokedDefaultsAsync</c>) read back as revoked defaults through the same parser as a project, and <c>init</c> leaves out the <c>CREATE</c> one
/// on PostgreSQL 15 and later, where it is the server's own default. (The catalog query is checked against a real server by the integration tests.)
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class LivePublicSchemaRevokeSnapshotTests
{
    private static readonly LiveGrantSnapshot Usage = new("public", "schema:public", "PUBLIC", "REVOKE USAGE ON SCHEMA public FROM PUBLIC");
    private static readonly LiveGrantSnapshot Create = new("public", "schema:public", "PUBLIC", "REVOKE CREATE ON SCHEMA public FROM PUBLIC");

    [TestMethod]
    public void TheRenderingsReadBackAsRevokedDefaults()
    {
        var loader = new LiveDatabaseModelLoader(new PgQueryParser(), new SemanticBuilder());
        var sql = LiveDatabaseModelLoader.GenerateSnapshotSql([new LiveSchemaSnapshot("public")], [], [], [], grants: [Usage, Create]);
        var grants = loader.BuildModelFromSnapshotSql(sql).Grants.Values.ToArray();

        Assert.HasCount(2, grants);
        Assert.IsTrue(grants.All(static grant => grant.RevokesDefault && grant.ObjectKey == "schema:public"));
        CollectionAssert.AreEquivalent(new[] { "USAGE", "CREATE" }, grants.Select(static grant => grant.Privileges.Single()).ToArray());
    }

    [TestMethod]
    [DataRow(14, 2)]
    [DataRow(15, 1)]
    [DataRow(18, 1)]
    public void InitLeavesOutTheCreateRevokeWhereItIsTheServersDefault(int serverMajorVersion, int kept)
    {
        var grants = LiveDatabaseModelLoader.WithoutServerDefaultRevokes([Usage, Create], serverMajorVersion);

        Assert.HasCount(kept, grants);
        Assert.IsTrue(grants.Contains(Usage), "USAGE is a revoke someone made on every version.");
    }

    [TestMethod]
    public void InitWritesThePublicSchemasRevokeInItsOwnFile()
    {
        var files = LiveDatabaseModelLoader.BuildScaffoldFiles([new LiveSchemaSnapshot("public")], [], [], [], [], [], [], [Usage], [], [], []);

        var file = files.Single(static file => file.RelativePath == "Schema/public/public.sql");
        StringAssert.Contains(file.Content, "REVOKE USAGE ON SCHEMA public FROM PUBLIC;");
    }
}
