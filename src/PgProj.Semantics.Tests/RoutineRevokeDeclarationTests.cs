using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-338: <c>REVOKE EXECUTE|ALL ON FUNCTION|PROCEDURE ... FROM PUBLIC</c>, the form <c>pg_dump</c> writes for a routine without PostgreSQL's default, is read as a
/// revoked default (G-337's option (B) for routines), and so is <c>ALTER DEFAULT PRIVILEGES [FOR ROLE r] REVOKE ... ON FUNCTIONS|ROUTINES|TYPES FROM PUBLIC</c>
/// with no <c>IN SCHEMA</c> (part (i)). Every other <c>REVOKE</c> stays a warning, and one with <c>IN SCHEMA</c> says it has no effect.
/// </summary>
[TestClass]
public sealed class RoutineRevokeDeclarationTests
{
    private const string Routines = "CREATE SCHEMA app; CREATE FUNCTION app.f(a integer, b text) RETURNS integer LANGUAGE sql AS $$ SELECT 1 $$; CREATE PROCEDURE app.p() LANGUAGE sql AS $$ SELECT 1 $$; ";

    [TestMethod]
    [DataRow("REVOKE EXECUTE ON FUNCTION app.f(integer, text) FROM PUBLIC;", "function:app.f(int4,text)")]
    [DataRow("REVOKE ALL ON FUNCTION app.f(int4, text) FROM public;", "function:app.f(int4,text)")]
    [DataRow("REVOKE ALL PRIVILEGES ON PROCEDURE app.p() FROM PUBLIC;", "procedure:app.p()")]
    public void ARoutinesRevokeFromPublicIsARevokedDefault(string sql, string objectKey)
    {
        var model = Build(Routines + sql);

        var grant = model.Grants.Values.Single();
        Assert.IsTrue(grant.RevokesDefault);
        Assert.AreEqual(objectKey, grant.ObjectKey);
        Assert.AreEqual("PUBLIC", grant.Grantee);
        Assert.IsEmpty(model.Diagnostics.Where(static diagnostic => diagnostic.Severity == SemanticDiagnosticSeverity.Warning));
    }

    [TestMethod]
    public void ARevokedDefaultIsNotTheGrantToPublicItUndoes()
    {
        Assert.AreNotEqual(
            Build(Routines + "REVOKE ALL ON FUNCTION app.f(integer, text) FROM PUBLIC;").Grants.Keys.Single(),
            Build(Routines + "GRANT ALL ON FUNCTION app.f(integer, text) TO PUBLIC;").Grants.Keys.Single());
    }

    [TestMethod]
    [DataRow("ALTER DEFAULT PRIVILEGES REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;", "default functions", null)]
    [DataRow("ALTER DEFAULT PRIVILEGES REVOKE ALL ON ROUTINES FROM PUBLIC;", "default functions", null)]
    [DataRow("ALTER DEFAULT PRIVILEGES FOR ROLE o REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;", "default functions", "o")]
    [DataRow("ALTER DEFAULT PRIVILEGES REVOKE USAGE ON TYPES FROM PUBLIC;", "default types", null)]
    public void ARuleThatRevokesPublicsDefaultIsARevokedDefault(string sql, string objectKey, string? forRole)
    {
        var model = Build(Routines + sql);

        var grant = model.Grants.Values.Single();
        Assert.IsTrue(grant.RevokesDefault);
        Assert.AreEqual(objectKey, grant.ObjectKey);
        Assert.AreEqual(forRole, grant.ForRole);
        Assert.IsNull(grant.SchemaName);
    }

    [TestMethod]
    [DataRow("ALTER DEFAULT PRIVILEGES IN SCHEMA app REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;")]
    [DataRow("ALTER DEFAULT PRIVILEGES IN SCHEMA app REVOKE USAGE ON TYPES FROM PUBLIC;")]
    public void ARuleInOneSchemaHasNoEffectAndSaysSo(string sql)
    {
        var model = Build(Routines + sql);

        Assert.IsEmpty(model.Grants);
        StringAssert.Contains(model.Diagnostics.Single(static diagnostic => diagnostic.Severity == SemanticDiagnosticSeverity.Warning).Message, "has no effect");
    }

    [TestMethod]
    [DataRow("REVOKE EXECUTE ON FUNCTION app.f(integer, text) FROM r;", "another role")]
    [DataRow("REVOKE GRANT OPTION FOR EXECUTE ON FUNCTION app.f(integer, text) FROM PUBLIC;", "a grant option")]
    [DataRow("REVOKE EXECUTE ON ROUTINE app.p() FROM PUBLIC;", "ON ROUTINE, which GRANT does not read either")]
    [DataRow("REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA app FROM PUBLIC;", "ALL FUNCTIONS IN SCHEMA")]
    [DataRow("ALTER DEFAULT PRIVILEGES REVOKE SELECT ON TABLES FROM PUBLIC;", "tables have no PUBLIC default")]
    [DataRow("ALTER DEFAULT PRIVILEGES REVOKE EXECUTE ON FUNCTIONS FROM r;", "another role in a rule")]
    public void EveryOtherRevokeIsStillAWarning(string sql, string why)
    {
        var model = Build(Routines + sql);

        Assert.IsEmpty(model.Grants, why);
        StringAssert.Contains(model.Diagnostics.Single(static diagnostic => diagnostic.Severity == SemanticDiagnosticSeverity.Warning).Message, "is not modeled and is ignored", why);
    }

    [TestMethod]
    public void ARevokeFromPublicAndARoleReadsOnlyPublicAndSaysSo()
    {
        var model = Build(Routines + "REVOKE ALL ON FUNCTION app.f(integer, text) FROM PUBLIC, r;");

        Assert.IsTrue(model.Grants.Values.Single().RevokesDefault);
        StringAssert.Contains(model.Diagnostics.Single(static diagnostic => diagnostic.Severity == SemanticDiagnosticSeverity.Warning).Message, "REVOKE ... FROM r is not modeled");
    }

    [TestMethod]
    public void ARevokeRuleGivesNothingAndIsNotToleratedAsAGrant()
    {
        var model = Build(Routines + "ALTER DEFAULT PRIVILEGES REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC; ALTER DEFAULT PRIVILEGES REVOKE USAGE ON TYPES FROM PUBLIC;");

        Assert.IsTrue(model.Grants.Values.All(grant => GrantExpansion.Expand(grant, model).Count == 0));
        var tolerated = GrantExpansion.DefaultPrivilegeTolerance(model);
        Assert.IsFalse(tolerated(new GrantAtom("function:app.f(int4,text)", "public", "EXECUTE", false)));
        Assert.IsTrue(GrantExpansion.RevokesDefaultByRule(model, "function:app.f(int4,text)"));
        Assert.IsTrue(GrantExpansion.RevokesDefaultByRule(model, "procedure:app.p()"));
        Assert.IsTrue(GrantExpansion.RevokesDefaultByRule(model, "type:app.mood"));
        Assert.IsFalse(GrantExpansion.RevokesDefaultByRule(model, "table:app.t"));
        Assert.IsFalse(GrantExpansion.RevokesDefaultByRule(Build(Routines), "function:app.f(int4,text)"));
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
