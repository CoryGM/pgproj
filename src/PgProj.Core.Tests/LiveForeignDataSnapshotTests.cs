using PgProj.Core;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core.Tests;

/// <summary>
/// G-132: the statements the live loader writes for foreign data read back as what a project writes; a password is never in them (the query masks it), and <c>init</c>
/// writes it as a variable; a server's mappings, grants, and owner go in its file; a mapping whose options cannot be read says so. (The catalog queries are checked
/// against a real server by the integration tests.)
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class LiveForeignDataSnapshotTests
{
    private static readonly LiveForeignDataSnapshot ForeignData = new(
        [new LiveForeignDataWrapperSnapshot("plain_fdw", null, null, [new("debug", "true")], null)],
        [new LiveForeignServerSnapshot("remote", "postgres_fdw", null, "18", [new("host", "db")], "the remote")],
        [
            new LiveUserMappingSnapshot("App User", "remote", [new("user", "u"), new("password", LiveDatabaseModelLoader.MaskedPassword)], OptionsUnreadable: false),
            new LiveUserMappingSnapshot("other", "remote", [], OptionsUnreadable: true),
        ]);

    [TestMethod]
    public void TheSnapshotReadsBackAsTheProjectWritesItAndNeverHoldsAPassword()
    {
        var sql = LiveDatabaseModelLoader.GenerateSnapshotSql([new LiveSchemaSnapshot("public")], [], [], [], foreignData: ForeignData);
        var live = new LiveDatabaseModelLoader(new PgQueryParser(), new SemanticBuilder()).BuildModelFromSnapshotSql(sql);

        Assert.IsTrue(live.ForeignDataWrappers.ContainsKey("foreign data wrapper:plain_fdw"));
        Assert.AreEqual("18 the remote", $"{live.ForeignServers["server:remote"].Version} {live.ForeignServers["server:remote"].Comment}");
        Assert.AreEqual("*****", live.UserMappings["user mapping:App User on remote"].Options.Single(static option => option.IsPassword).Value);
        StringAssert.Contains(sql, "CREATE USER MAPPING FOR \"App User\" SERVER remote OPTIONS (\"user\" 'u', password '*****');");
    }

    [TestMethod]
    public void InitWritesThePasswordAsAVariableAndTheMappingsInTheServersFile()
    {
        var files = LiveDatabaseModelLoader.BuildScaffoldFiles([new LiveSchemaSnapshot("public")], [], [], [], [], [], [], [new LiveGrantSnapshot(string.Empty, "server:remote", "app", "GRANT USAGE ON FOREIGN SERVER remote TO app")], [], [], [], foreignData: ForeignData);

        var server = files.Single(static file => file.RelativePath == "Server/remote.sql").Content;
        StringAssert.Contains(server, "password :'remote_app_user_password'");
        StringAssert.Contains(server, "GRANT USAGE ON FOREIGN SERVER remote TO app;");
        StringAssert.Contains(server, "-- pgproj: the role init connected as cannot read this mapping's options");
        Assert.DoesNotContain("*****", server);
        CollectionAssert.Contains(files.Select(static file => file.RelativePath).ToArray(), "ForeignDataWrapper/plain_fdw.sql");
    }
}
