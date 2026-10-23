using PgProj.Diff;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

[TestClass]
public sealed class GrantOperationTests
{
    [TestMethod]
    public void CreateOperationEmitsSchemaGrant()
    {
        var grant = CreateGrant("schema:app", "app_user", ["USAGE", "CREATE"]);

        var sql = GenerateCreateSql(grant);

        Assert.AreEqual("GRANT USAGE, CREATE ON SCHEMA \"app\" TO \"app_user\";", sql);
    }

    [TestMethod]
    public void CreateOperationEmitsSchemaGrantToPublic()
    {
        var grant = CreateGrant("schema:app", "PUBLIC", ["USAGE"]);

        var sql = GenerateCreateSql(grant);

        Assert.AreEqual("GRANT USAGE ON SCHEMA \"app\" TO PUBLIC;", sql);
    }

    [TestMethod]
    public void CreateOperationEmitsAllObjectsInSchemaGrant()
    {
        var grant = CreateGrant("all tables in schema:app", "app_user", ["SELECT"]);

        var sql = GenerateCreateSql(grant);

        Assert.AreEqual("GRANT SELECT ON ALL TABLES IN SCHEMA \"app\" TO \"app_user\";", sql);
    }

    [TestMethod]
    public void CreateOperationEmitsDefaultPrivilegesGrantWithSchema()
    {
        var grant = CreateGrant("default tables in schema:app", "app_user", ["SELECT", "INSERT"]);

        var sql = GenerateCreateSql(grant);

        Assert.AreEqual(
            "ALTER DEFAULT PRIVILEGES IN SCHEMA \"app\" GRANT SELECT, INSERT ON TABLES TO \"app_user\";",
            sql);
    }

    [TestMethod]
    public void CreateOperationEmitsDefaultPrivilegesGrantWithoutSchema()
    {
        var grant = CreateGrant("default tables", "app_user", ["SELECT"]);

        var sql = GenerateCreateSql(grant);

        Assert.AreEqual("ALTER DEFAULT PRIVILEGES GRANT SELECT ON TABLES TO \"app_user\";", sql);
    }

    [TestMethod]
    public void CreateOperationEmitsAllPrivilegesWhenPrivilegeListIsEmpty()
    {
        var grant = CreateGrant("schema:app", "app_user", []);

        var sql = GenerateCreateSql(grant);

        Assert.AreEqual("GRANT ALL PRIVILEGES ON SCHEMA \"app\" TO \"app_user\";", sql);
    }

    [TestMethod]
    public void DropOperationEmitsSchemaRevoke()
    {
        var grant = CreateGrant("schema:app", "app_user", ["USAGE"]);

        var sql = GenerateDropSql(grant);

        Assert.AreEqual("REVOKE USAGE ON SCHEMA \"app\" FROM \"app_user\";", sql);
    }

    [TestMethod]
    public void DropOperationEmitsAllObjectsInSchemaRevoke()
    {
        var grant = CreateGrant("all tables in schema:app", "app_user", ["SELECT"]);

        var sql = GenerateDropSql(grant);

        Assert.AreEqual("REVOKE SELECT ON ALL TABLES IN SCHEMA \"app\" FROM \"app_user\";", sql);
    }

    [TestMethod]
    public void DropOperationEmitsDefaultPrivilegesRevoke()
    {
        var grant = CreateGrant("default tables in schema:app", "app_user", ["SELECT"]);

        var sql = GenerateDropSql(grant);

        Assert.AreEqual(
            "ALTER DEFAULT PRIVILEGES IN SCHEMA \"app\" REVOKE SELECT ON TABLES FROM \"app_user\";",
            sql);
    }

    [TestMethod]
    public void CreateOperationFallsBackToTodoWhenGranteeIsMissing()
    {
        var grant = CreateGrant("schema:app", null, ["USAGE"]);

        var sql = GenerateCreateSql(grant);

        StringAssert.Contains(sql, "TODO");
    }

    private static string GenerateCreateSql(GrantDefinition grant)
        => new CreateOperation
        {
            NodeId = grant.Key,
            NodeKind = ChangeNodeKind.Grant,
            Node = grant,
        }.GenerateSql();

    private static string GenerateDropSql(GrantDefinition grant)
        => new DropOperation
        {
            NodeId = grant.Key,
            NodeKind = ChangeNodeKind.Grant,
            Node = grant,
        }.GenerateSql();

    private static GrantDefinition CreateGrant(string objectKey, string? grantee, IReadOnlyList<string> privileges)
        => new(
            $"public.{objectKey}->{grantee}",
            $"{objectKey}->{grantee}",
            "public",
            objectKey,
            grantee,
            privileges,
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            []);
}
