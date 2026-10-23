using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core.Tests;

/// <summary>
/// G-147: the roles a project's grants name that the server lacks are found and named, so that <c>publish --execute</c> can refuse before the
/// server fails partway through the deployment. A role is cluster-wide, so pgproj never creates one; a role the pre-deploy script creates is
/// not missing.
/// </summary>
[TestClass]
public sealed class ServerRoleRequirementsTests
{
    private static readonly string[] BuiltIn = ["postgres", "pg_read_all_data"];

    private const string Project = """
        CREATE SCHEMA app;
        CREATE TABLE app.t (id int);
        GRANT SELECT ON TABLE app.t TO app_rw;
        GRANT USAGE ON SCHEMA app TO PUBLIC;
        """;

    [TestMethod]
    public void AProjectWithNoGrantsNeedsNoRole()
    {
        // Arrange
        var model = Build("CREATE SCHEMA app; CREATE TABLE app.t (id int);");

        // Act / Assert
        Assert.IsEmpty(ServerRoleRequirements.FindMissing(model, BuiltIn, null));
    }

    [TestMethod]
    public void ARoleTheServerHasIsNotMissingAndPublicIsNotARole()
    {
        // Arrange: app_rw is on the server, PUBLIC never is.
        var model = Build(Project);

        // Act / Assert
        Assert.IsEmpty(ServerRoleRequirements.FindMissing(model, [.. BuiltIn, "app_rw"], null));

        // A role is named exactly, as PostgreSQL names it (G-206): a server role "APP_RW" is not app_rw.
        Assert.IsNotEmpty(ServerRoleRequirements.FindMissing(model, [.. BuiltIn, "APP_RW"], null));
    }

    [TestMethod]
    public void AMissingGranteeIsNamedWithWhatNamesIt()
    {
        // Arrange
        var model = Build(Project);

        // Act
        var missing = ServerRoleRequirements.FindMissing(model, BuiltIn, null);

        // Assert: PUBLIC is not reported.
        Assert.AreEqual(1, missing.Count);
        Assert.AreEqual("app_rw", missing[0].RoleName);
        CollectionAssert.AreEqual(new[] { "table:app.t" }, missing[0].NamedBy.ToArray());
    }

    [TestMethod]
    public void BothSidesOfAMembershipAndTheRoleOfDefaultPrivilegesAreChecked()
    {
        // Arrange
        var model = Build("""
            CREATE SCHEMA app;
            GRANT app_admin TO app_member;
            ALTER DEFAULT PRIVILEGES FOR ROLE app_owner IN SCHEMA app GRANT SELECT ON TABLES TO postgres;
            """);

        // Act
        var missing = ServerRoleRequirements.FindMissing(model, BuiltIn, null);

        // Assert: in name order.
        CollectionAssert.AreEqual(new[] { "app_admin", "app_member", "app_owner" }, missing.Select(static m => m.RoleName).ToArray());
    }

    [TestMethod]
    public void AGrantTheProjectExcludedIsNotChecked()
    {
        // Arrange: excludeGrants and excludeRoleMemberships take the grants out of the model.
        var model = new SemanticPipeline().Execute(
            [new PgQueryParser().Parse(Project + "\nGRANT app_admin TO app_member;")],
            schemas: null,
            excludeGrants: true,
            excludeRoleMemberships: true);

        // Act / Assert
        Assert.IsEmpty(ServerRoleRequirements.FindMissing(model, BuiltIn, null));
    }

    [TestMethod]
    public void TheRoleAnObjectIsToBeOwnedByMustExistToo()
    {
        // Arrange (G-146): an owner is set with ALTER ... OWNER TO, which fails for a role the server lacks, like a grant.
        var model = Build("CREATE SCHEMA app; CREATE TABLE app.t (id int); ALTER TABLE app.t OWNER TO app_owner; ALTER SCHEMA app OWNER TO postgres;");

        // Act
        var missing = ServerRoleRequirements.FindMissing(model, BuiltIn, null);

        // Assert: postgres exists; app_owner does not, and the message says it is an owner.
        Assert.AreEqual("app_owner", missing.Single().RoleName);
        CollectionAssert.AreEqual(new[] { "table app.t (owner)" }, missing.Single().NamedBy.ToArray());
    }

    [TestMethod]
    public void AnOwnerRoleThePreDeployScriptCreatesIsNotMissing()
    {
        // Arrange
        var model = Build("CREATE SCHEMA app; CREATE TABLE app.t (id int); ALTER TABLE app.t OWNER TO app_owner;");

        // Act / Assert
        Assert.IsEmpty(ServerRoleRequirements.FindMissing(model, BuiltIn, "DO $$ BEGIN CREATE ROLE app_owner NOLOGIN; END $$;"));
    }

    [TestMethod]
    public void AnOwnerTheProjectLeavesOutIsNotChecked()
    {
        // Arrange: excludeOwners takes the owners out of the model, so the role they name is not needed.
        var model = new SemanticPipeline().Execute(
            [new PgQueryParser().Parse("CREATE SCHEMA app; CREATE TABLE app.t (id int); ALTER TABLE app.t OWNER TO app_owner;")],
            excludeOwners: true);

        // Act / Assert
        Assert.IsEmpty(ServerRoleRequirements.FindMissing(model, BuiltIn, null));
    }

    [TestMethod]
    public void ARoleThePreDeployScriptCreatesIsNotMissing()
    {
        // Arrange: the shape of the guarded pre-deploy role script the documentation describes.
        const string Script = """
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'app_rw') THEN
                    CREATE ROLE app_rw NOLOGIN;
                END IF;
            END
            $$;
            """;

        // Act / Assert
        Assert.IsEmpty(ServerRoleRequirements.FindMissing(Build(Project), BuiltIn, Script));
    }

    [TestMethod]
    public void ARoleAScriptBuildsTheStatementForIsNotMissing()
    {
        // Arrange: the statement is made from a list of names, so only the keyword and the name are in the text.
        const string Script = """
            DO $$ DECLARE r text; BEGIN FOREACH r IN ARRAY ARRAY['other', "App_RW"] LOOP EXECUTE format('CREATE USER %I', r); END LOOP; END $$;
            """;

        // Act / Assert
        Assert.IsEmpty(ServerRoleRequirements.FindMissing(Build(Project), BuiltIn, Script));
    }

    [TestMethod]
    public void AScriptThatOnlyMentionsTheRoleInACommentOrCreatesOthersDoesNotHideIt()
    {
        // Arrange: a name in a comment is not a creation; a script that creates a different role does not create this one.
        const string OnlyAComment = "-- the grants need app_rw, create it by hand\n/* app_rw */\nSELECT 1;";
        const string CreatesAnother = "CREATE ROLE somebody_else NOLOGIN;";

        // Act / Assert
        Assert.AreEqual(1, ServerRoleRequirements.FindMissing(Build(Project), BuiltIn, OnlyAComment).Count);
        Assert.AreEqual(1, ServerRoleRequirements.FindMissing(Build(Project), BuiltIn, CreatesAnother).Count);
        Assert.AreEqual(1, ServerRoleRequirements.FindMissing(Build(Project), BuiltIn, "-- CREATE ROLE app_rw;\nSELECT 1;").Count);
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
