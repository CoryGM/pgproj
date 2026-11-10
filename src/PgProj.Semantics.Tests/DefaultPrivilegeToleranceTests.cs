using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-339: <see cref="GrantExpansion.DefaultPrivilegeTolerance"/> says which privileges on an object the project's default-privilege rules may give it, by kind,
/// schema (or none, for a global rule), grantee, privilege, and grant option; <see cref="GrantExpansion.DefaultPrivilegeKind"/> reads a rule's kind from its key.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class DefaultPrivilegeToleranceTests
{
    [TestMethod]
    [DataRow("default tables", "tables")]
    [DataRow("default tables in schema:app", "tables")]
    [DataRow("default functions in schema:in schema", "functions")]
    [DataRow("default types", "types")]
    public void ARulesKindIsReadFromItsKey(string objectKey, string expected)
    {
        Assert.AreEqual(expected, GrantExpansion.DefaultPrivilegeKind(objectKey));
    }

    [TestMethod]
    public void WhatTheRulesGiveIsToleratedAndNothingElse()
    {
        var tolerated = GrantExpansion.DefaultPrivilegeTolerance(new SemanticPipeline().Execute([new PgQueryParser().Parse(
            "CREATE SCHEMA app; ALTER DEFAULT PRIVILEGES IN SCHEMA app GRANT SELECT ON TABLES TO r; ALTER DEFAULT PRIVILEGES GRANT EXECUTE ON FUNCTIONS TO PUBLIC;"
            + "ALTER DEFAULT PRIVILEGES FOR ROLE o GRANT USAGE ON SCHEMAS TO r;")]));

        Assert.IsTrue(tolerated(new GrantAtom("table:app.t", "r", "SELECT", false)));
        Assert.IsFalse(tolerated(new GrantAtom("table:other.t", "r", "SELECT", false)), "Another schema.");
        Assert.IsFalse(tolerated(new GrantAtom("table:app.t", "r", "INSERT", false)), "Another privilege.");
        Assert.IsFalse(tolerated(new GrantAtom("table:app.t", "r", "SELECT", true)), "Another grant option.");
        Assert.IsFalse(tolerated(new GrantAtom("column:app.t.id", "r", "SELECT", false)), "A rule gives no column privilege.");
        Assert.IsTrue(tolerated(new GrantAtom("function:anywhere.f()", "public", "EXECUTE", false)), "A global rule, for every schema.");
        Assert.IsTrue(tolerated(new GrantAtom("procedure:anywhere.p()", "public", "EXECUTE", false)), "FUNCTIONS covers procedures.");
        Assert.IsTrue(tolerated(new GrantAtom("schema:app", "r", "USAGE", false)), "A rule of any role counts.");
        Assert.IsFalse(tolerated(new GrantAtom("sequence:app.q", "r", "SELECT", false)), "No rule for sequences.");
    }

    [TestMethod]
    public void AProjectWithoutRulesToleratesNothing()
    {
        var tolerated = GrantExpansion.DefaultPrivilegeTolerance(new SemanticPipeline().Execute([new PgQueryParser().Parse("CREATE SCHEMA app;")]));

        Assert.IsFalse(tolerated(new GrantAtom("table:app.t", "r", "SELECT", false)));
    }
}
