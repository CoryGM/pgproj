using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-341: <c>REVOKE USAGE|CREATE|ALL ON SCHEMA public FROM PUBLIC</c> is read as the <c>public</c> schema without PostgreSQL's default, one revoked default per
/// privilege; on any other schema, <c>PUBLIC</c> has no default, and the statement stays a warning.
/// </summary>
[TestClass]
public sealed class PublicSchemaRevokeDeclarationTests
{
    [TestMethod]
    [DataRow("REVOKE USAGE ON SCHEMA public FROM PUBLIC;", "USAGE")]
    [DataRow("REVOKE CREATE ON SCHEMA public FROM public;", "CREATE")]
    [DataRow("REVOKE ALL ON SCHEMA public FROM PUBLIC;", "CREATE,USAGE")]
    [DataRow("REVOKE ALL PRIVILEGES ON SCHEMA public FROM PUBLIC;", "CREATE,USAGE")]
    [DataRow("REVOKE USAGE, CREATE ON SCHEMA public FROM PUBLIC;", "CREATE,USAGE")]
    public void ARevokeFromPublicOnThePublicSchemaIsOneRevokedDefaultPerPrivilege(string sql, string privileges)
    {
        var model = Build(sql);

        var grants = model.Grants.Values.ToArray();
        Assert.IsTrue(grants.All(static grant => grant.RevokesDefault && grant.ObjectKey == "schema:public" && grant.ObjectName == "public" && grant.SchemaName == "public" && grant.Grantee == "PUBLIC"));
        Assert.AreEqual(privileges, string.Join(",", grants.Select(static grant => grant.Privileges.Single()).Order(StringComparer.Ordinal)));
        Assert.IsTrue(grants.All(grant => GrantExpansion.Expand(grant, model).Count == 0), "A revoked default stands for no atom: it is compared by its key.");
        Assert.IsEmpty(model.Diagnostics.Where(static diagnostic => diagnostic.Severity == SemanticDiagnosticSeverity.Warning));
    }

    [TestMethod]
    public void TheUsageAndCreateRevokesAreNotOneAnotherNorTheGrantsTheyUndo()
    {
        var keys = new[]
        {
            Build("REVOKE USAGE ON SCHEMA public FROM PUBLIC;").Grants.Keys.Single(),
            Build("REVOKE CREATE ON SCHEMA public FROM PUBLIC;").Grants.Keys.Single(),
            Build("GRANT USAGE ON SCHEMA public TO PUBLIC;").Grants.Keys.Single(),
            Build("GRANT CREATE ON SCHEMA public TO PUBLIC;").Grants.Keys.Single(),
        };

        Assert.HasCount(4, keys.Distinct(StringComparer.Ordinal));
    }

    [TestMethod]
    [DataRow("CREATE SCHEMA app; REVOKE USAGE ON SCHEMA app FROM PUBLIC;", "PUBLIC has no default on another schema")]
    [DataRow("REVOKE USAGE ON SCHEMA \"Public\" FROM PUBLIC;", "a schema named Public is not public")]
    [DataRow("REVOKE USAGE ON SCHEMA public FROM r;", "another role")]
    [DataRow("REVOKE GRANT OPTION FOR USAGE ON SCHEMA public FROM PUBLIC;", "a grant option")]
    public void EveryOtherRevokeOnASchemaIsStillAWarning(string sql, string why)
    {
        var model = Build(sql);

        Assert.IsEmpty(model.Grants, why);
        StringAssert.Contains(model.Diagnostics.Single(static diagnostic => diagnostic.Severity == SemanticDiagnosticSeverity.Warning).Message, "is not modeled and is ignored", why);
    }

    [TestMethod]
    public void ARevokeOnPublicAndAnotherSchemaReadsOnlyPublicAndSaysSo()
    {
        var model = Build("CREATE SCHEMA app; REVOKE USAGE ON SCHEMA public, app FROM PUBLIC, r;");

        Assert.AreEqual("schema:public", model.Grants.Values.Single().ObjectKey);
        var warnings = model.Diagnostics.Where(static diagnostic => diagnostic.Severity == SemanticDiagnosticSeverity.Warning).Select(static diagnostic => diagnostic.Message).ToArray();
        Assert.HasCount(2, warnings);
        Assert.IsTrue(warnings.Any(static message => message.Contains("REVOKE ... ON SCHEMA app FROM PUBLIC is not modeled", StringComparison.Ordinal)), string.Join("\n", warnings));
        Assert.IsTrue(warnings.Any(static message => message.Contains("REVOKE ... FROM r is not modeled", StringComparison.Ordinal)), string.Join("\n", warnings));
    }

    [TestMethod]
    public void ExcludedGrantsLeaveOutThePublicSchemasRevokeToo()
    {
        var model = new SemanticPipeline().Execute([new PgQueryParser().Parse("REVOKE ALL ON SCHEMA public FROM PUBLIC;")], excludeGrants: true);

        Assert.IsEmpty(model.Grants);
        Assert.IsEmpty(model.Diagnostics.Where(static diagnostic => diagnostic.Severity == SemanticDiagnosticSeverity.Warning));
    }

    [TestMethod]
    public void PublicsUsageOnThePublicSchemaIsImplicitAndOnAnotherSchemaIsNot()
    {
        var model = Build("CREATE SCHEMA app; GRANT USAGE ON SCHEMA public TO PUBLIC; GRANT USAGE ON SCHEMA app TO PUBLIC; GRANT CREATE ON SCHEMA public TO PUBLIC;");
        var atoms = model.Grants.Values.SelectMany(grant => GrantExpansion.Expand(grant, model)).ToArray();

        Assert.IsTrue(atoms.Single(static atom => atom.Target == "schema:public" && atom.Privilege == "USAGE").IsImplicit, "PostgreSQL's default; a revoked default decides it.");
        Assert.IsFalse(atoms.Single(static atom => atom.Target == "schema:app").IsImplicit);
        Assert.IsFalse(atoms.Single(static atom => atom.Privilege == "CREATE").IsImplicit, "A grant of CREATE to PUBLIC is a grant, as before.");
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
