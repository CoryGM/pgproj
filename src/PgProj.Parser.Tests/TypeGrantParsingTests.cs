using PgProj.Parser;

namespace PgProj.Parser.Tests;

/// <summary>
/// G-337: <c>GRANT ... ON TYPE</c> and <c>ON DOMAIN</c> name each object as a list of its name's parts in pg_query's tree, which the reader did not read, so every
/// such grant lost its target. A revoke from <c>PUBLIC</c> is read the same way, with what it says (no privilege named is <c>ALL</c>; <c>GRANT OPTION FOR</c>).
/// </summary>
[TestClass]
public sealed class TypeGrantParsingTests
{
    [TestMethod]
    public void AGrantOnATypeNamesTheType()
    {
        var grant = Single("GRANT USAGE ON TYPE app.mood TO r WITH GRANT OPTION;");

        Assert.AreEqual(GrantForm.Object, grant.Form);
        Assert.AreEqual("type", grant.ObjectKind);
        Assert.IsTrue(grant.IsGrant);
        Assert.IsTrue(grant.WithGrantOption);
        Assert.AreEqual(new GrantTarget("app", "mood"), grant.Targets.Single());
        Assert.AreEqual("USAGE", grant.Privileges.Single().Name);
        Assert.AreEqual("r", grant.Grantees.Single());
        Assert.IsEmpty(grant.UnsupportedParts);
    }

    [TestMethod]
    public void AGrantOnADomainNamesTheDomain()
    {
        var grant = Single("GRANT USAGE ON DOMAIN app.pos TO r;");

        Assert.AreEqual("domain", grant.ObjectKind);
        Assert.AreEqual(new GrantTarget("app", "pos"), grant.Targets.Single());
        Assert.IsEmpty(grant.UnsupportedParts);
    }

    [TestMethod]
    public void SeveralTypesAndAQuotedNameAreAllTargets()
    {
        var grant = Single("GRANT ALL ON TYPE app.a, \"App\".\"B c\" TO r;");

        CollectionAssert.AreEqual(new[] { new GrantTarget("app", "a"), new GrantTarget("App", "B c") }, grant.Targets.ToArray());
        Assert.IsEmpty(grant.UnsupportedParts);
    }

    [TestMethod]
    public void AnUnqualifiedTypeTakesTheDefaultSchema()
    {
        var target = Single("GRANT USAGE ON TYPE mood TO r;").Targets.Single();

        Assert.AreEqual("public", target.SchemaName);
        Assert.AreEqual("mood", target.Name);
        Assert.IsTrue(target.IsSchemaDefaulted);
    }

    [TestMethod]
    public void ARevokeFromPublicIsReadWithWhatItSays()
    {
        var all = Single("REVOKE ALL ON TYPE app.mood FROM PUBLIC;");
        Assert.IsFalse(all.IsGrant);
        Assert.AreEqual("PUBLIC", all.Grantees.Single());
        Assert.IsEmpty(all.Privileges, "ALL names no privilege.");
        Assert.AreEqual(new GrantTarget("app", "mood"), all.Targets.Single());
        Assert.IsFalse(all.WithGrantOption);

        var grantOption = Single("REVOKE GRANT OPTION FOR USAGE ON DOMAIN app.pos FROM PUBLIC;");
        Assert.IsFalse(grantOption.IsGrant);
        Assert.IsTrue(grantOption.WithGrantOption, "GRANT OPTION FOR is not the privilege itself.");
        Assert.AreEqual("domain", grantOption.ObjectKind);
    }

    private static GrantNode Single(string sql)
        => new PgQueryParser().Parse(sql).Grants.Single();
}
