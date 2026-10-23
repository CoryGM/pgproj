using PgProj.Parser;

namespace PgProj.Parser.Tests;

/// <summary>
/// G-65: grants are read from pg_query's AST, not from the SQL text. Every form the register lists must reach the syntax tree
/// with what it says (targets, privileges, grantees, options), and text that only looks like a grant must not.
/// </summary>
[TestClass]
public sealed class GrantParsingTests
{
    [TestMethod]
    public void TableGrantWithSeveralGranteesAndGrantOptionIsRead()
    {
        var grant = Single("GRANT SELECT, INSERT ON TABLE public.t TO r1, r2 WITH GRANT OPTION;");

        Assert.AreEqual(GrantForm.Object, grant.Form);
        Assert.AreEqual("table", grant.ObjectKind);
        Assert.IsTrue(grant.IsGrant);
        Assert.IsTrue(grant.WithGrantOption);
        CollectionAssert.AreEqual(new[] { "r1", "r2" }, grant.Grantees.ToArray());
        CollectionAssert.AreEqual(new[] { "SELECT", "INSERT" }, grant.Privileges.Select(static privilege => privilege.Name).ToArray());
        Assert.AreEqual(new GrantTarget("public", "t"), grant.Targets.Single());
    }

    [TestMethod]
    public void TableGrantWithoutTheTableKeywordIsTheSameGrant()
    {
        var grant = Single("GRANT SELECT ON public.t TO r;");

        Assert.AreEqual("table", grant.ObjectKind);
        Assert.AreEqual(new GrantTarget("public", "t"), grant.Targets.Single());
    }

    [TestMethod]
    public void SeveralTablesInOneStatementAreAllTargets()
    {
        var grant = Single("GRANT SELECT ON public.a, public.b, c TO r;");

        CollectionAssert.AreEqual(
            new[] { new GrantTarget("public", "a"), new GrantTarget("public", "b"), new GrantTarget("public", "c") { IsSchemaDefaulted = true } }, // an unqualified table is the one in public (G-114), marked as defaulted (G-93),
            grant.Targets.ToArray());
    }

    [TestMethod]
    public void ColumnPrivilegesKeepTheirOwnColumns()
    {
        var grant = Single("GRANT SELECT (id, name), UPDATE (name) ON public.t TO r;");

        Assert.AreEqual(2, grant.Privileges.Count);
        CollectionAssert.AreEqual(new[] { "id", "name" }, grant.Privileges[0].Columns.ToArray());
        CollectionAssert.AreEqual(new[] { "name" }, grant.Privileges[1].Columns.ToArray());
    }

    [TestMethod]
    public void GrantWithoutPrivilegesListIsAllPrivileges()
    {
        var grant = Single("GRANT ALL ON SCHEMA s TO r;");

        Assert.AreEqual("schema", grant.ObjectKind);
        Assert.IsEmpty(grant.Privileges);
    }

    [TestMethod]
    public void AllInSchemaIsReadWithItsSchemaAndPublicGrantee()
    {
        var grant = Single("GRANT SELECT ON ALL TABLES IN SCHEMA s TO PUBLIC;");

        Assert.AreEqual(GrantForm.AllInSchema, grant.Form);
        Assert.AreEqual("table", grant.ObjectKind);
        Assert.AreEqual(new GrantTarget(null, "s"), grant.Targets.Single());
        CollectionAssert.AreEqual(new[] { "PUBLIC" }, grant.Grantees.ToArray());
    }

    [TestMethod]
    public void FunctionGrantKeepsTheOverloadsArgumentTypes()
    {
        var grant = Single("GRANT EXECUTE ON FUNCTION public.f(integer, text[]) TO r;");

        Assert.AreEqual("function", grant.ObjectKind);
        var target = grant.Targets.Single();
        Assert.AreEqual("public", target.SchemaName);
        Assert.AreEqual("f", target.Name);
        CollectionAssert.AreEqual(new[] { "pg_catalog.int4", "text[]" }, target.ArgumentTypes!.ToArray());
    }

    [TestMethod]
    public void FunctionWithoutArgumentsHasAnEmptyArgumentListNotNull()
    {
        var target = Single("GRANT EXECUTE ON FUNCTION public.f() TO r;").Targets.Single();

        Assert.IsNotNull(target.ArgumentTypes);
        Assert.IsEmpty(target.ArgumentTypes);
    }

    [TestMethod]
    public void SequenceGrantIsRead()
    {
        var grant = Single("GRANT USAGE ON SEQUENCE public.s TO r;");

        Assert.AreEqual("sequence", grant.ObjectKind);
        Assert.AreEqual(new GrantTarget("public", "s"), grant.Targets.Single());
    }

    [TestMethod]
    public void QuotedNamesKeepTheirCase()
    {
        var grant = Single("GRANT USAGE ON SCHEMA \"My Schema\" TO \"App Role\", plain;");

        Assert.AreEqual("My Schema", grant.Targets.Single().Name);
        CollectionAssert.AreEqual(new[] { "App Role", "plain" }, grant.Grantees.ToArray());
    }

    [TestMethod]
    public void RoleMembershipIsReadWithItsAdminOption()
    {
        var grant = Single("GRANT r1, r2 TO r3 WITH ADMIN OPTION;");

        Assert.AreEqual(GrantForm.RoleMembership, grant.Form);
        CollectionAssert.AreEqual(new[] { "r1", "r2" }, grant.Roles.ToArray());
        CollectionAssert.AreEqual(new[] { "r3" }, grant.Grantees.ToArray());
        Assert.IsTrue(grant.WithGrantOption);
        Assert.IsEmpty(grant.UnsupportedParts);
    }

    [TestMethod]
    public void RoleMembershipOptionsTheModelHasNoPlaceForAreReported()
    {
        var grant = Single("GRANT r1 TO r2 WITH INHERIT FALSE;");

        Assert.IsNotEmpty(grant.UnsupportedParts);
    }

    [TestMethod]
    public void DefaultPrivilegesKeepTheirRoleSchemaAndOption()
    {
        var grant = Single("ALTER DEFAULT PRIVILEGES FOR ROLE owner IN SCHEMA s GRANT SELECT ON TABLES TO r WITH GRANT OPTION;");

        Assert.AreEqual(GrantForm.DefaultPrivileges, grant.Form);
        Assert.AreEqual("table", grant.ObjectKind);
        CollectionAssert.AreEqual(new[] { "owner" }, grant.Roles.ToArray());
        CollectionAssert.AreEqual(new[] { "s" }, grant.Schemas.ToArray());
        Assert.IsTrue(grant.WithGrantOption);
    }

    [TestMethod]
    public void DefaultPrivilegesOnTypesAreRead()
    {
        var grant = Single("ALTER DEFAULT PRIVILEGES GRANT USAGE ON TYPES TO r;");

        Assert.AreEqual(GrantForm.DefaultPrivileges, grant.Form);
        Assert.AreEqual("type", grant.ObjectKind);
        Assert.IsEmpty(grant.Roles);
        Assert.IsEmpty(grant.Schemas);
    }

    [TestMethod]
    public void RevokeIsMarkedAsNotAGrant()
    {
        var grant = Single("REVOKE SELECT ON public.t FROM r;");

        Assert.IsFalse(grant.IsGrant);
    }

    [TestMethod]
    public void CurrentUserGranteeCannotBeNamedAndIsReported()
    {
        var grant = Single("GRANT SELECT ON public.t TO CURRENT_USER, r;");

        CollectionAssert.AreEqual(new[] { "r" }, grant.Grantees.ToArray());
        Assert.IsNotEmpty(grant.UnsupportedParts);
    }

    /// <summary>The G-60 defect: the privileges of one statement must never include text from its neighbours.</summary>
    [TestMethod]
    public void NeighbouringStatementsKeepTheirOwnPrivileges()
    {
        var grants = new PgQueryParser()
            .Parse("GRANT SELECT ON ALL TABLES IN SCHEMA s TO r1; GRANT USAGE ON SCHEMA s TO r2; GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA s TO r3;")
            .Grants
            .ToArray();

        Assert.AreEqual(3, grants.Length);
        CollectionAssert.AreEqual(new[] { "SELECT" }, grants[0].Privileges.Select(static privilege => privilege.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "USAGE" }, grants[1].Privileges.Select(static privilege => privilege.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "EXECUTE" }, grants[2].Privileges.Select(static privilege => privilege.Name).ToArray());
    }

    [TestMethod]
    [DataRow("-- GRANT ALL ON SCHEMA s TO r;\nCREATE TABLE s.t (id integer);")]
    [DataRow("/* GRANT ALL ON SCHEMA s TO r; */ CREATE TABLE s.t (id integer);")]
    [DataRow("CREATE TABLE s.t (note text DEFAULT 'GRANT ALL ON SCHEMA s TO r;');")]
    [DataRow("CREATE FUNCTION s.f() RETURNS void LANGUAGE plpgsql AS $$ BEGIN EXECUTE 'GRANT ALL ON ALL TABLES IN SCHEMA s TO r;'; END $$;")]
    public void TextThatLooksLikeAGrantIsNotOne(string sql)
    {
        Assert.IsEmpty(new PgQueryParser().Parse(sql).Grants.ToArray());
    }

    private static GrantNode Single(string sql)
        => new PgQueryParser().Parse(sql).Grants.Single();
}
