using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-340: <c>PUBLIC</c>'s <c>USAGE</c> in a default-privilege rule on types and its <c>EXECUTE</c> in one on functions, with no <c>IN SCHEMA</c>, are PostgreSQL's
/// built-in defaults (it stores them in the list once such a rule exists), so their atoms are implicit, as the same privileges on an object are (G-61, G-337).
/// In one schema a rule granting to <c>PUBLIC</c> is a real rule, and on the other kinds there is no such default.
/// </summary>
[TestClass]
public sealed class DefaultPrivilegePublicDefaultTests
{
    [TestMethod]
    [DataRow("ALTER DEFAULT PRIVILEGES GRANT USAGE ON TYPES TO PUBLIC;", "default types")]
    [DataRow("ALTER DEFAULT PRIVILEGES GRANT EXECUTE ON FUNCTIONS TO PUBLIC;", "default functions")]
    [DataRow("ALTER DEFAULT PRIVILEGES FOR ROLE o GRANT USAGE ON TYPES TO PUBLIC;", "default types for role:o")]
    [DataRow("ALTER DEFAULT PRIVILEGES FOR ROLE o GRANT EXECUTE ON ROUTINES TO PUBLIC;", "default functions for role:o")]
    public void PublicsBuiltInPrivilegeInAGlobalRuleIsImplicit(string rule, string target)
    {
        var atom = Atoms(rule).Single();

        Assert.AreEqual(target, atom.Target);
        Assert.IsTrue(atom.IsImplicit);
    }

    [TestMethod]
    [DataRow("ALTER DEFAULT PRIVILEGES IN SCHEMA app GRANT USAGE ON TYPES TO PUBLIC;", "one schema's rule")]
    [DataRow("ALTER DEFAULT PRIVILEGES IN SCHEMA app GRANT EXECUTE ON FUNCTIONS TO PUBLIC;", "one schema's rule")]
    [DataRow("ALTER DEFAULT PRIVILEGES GRANT USAGE ON TYPES TO r;", "another grantee")]
    [DataRow("ALTER DEFAULT PRIVILEGES GRANT EXECUTE ON FUNCTIONS TO r;", "another grantee")]
    [DataRow("ALTER DEFAULT PRIVILEGES GRANT SELECT ON TABLES TO PUBLIC;", "no such default on tables")]
    [DataRow("ALTER DEFAULT PRIVILEGES GRANT USAGE ON SEQUENCES TO PUBLIC;", "no such default on sequences")]
    [DataRow("ALTER DEFAULT PRIVILEGES GRANT USAGE ON SCHEMAS TO PUBLIC;", "no such default on schemas")]
    public void AnyOtherRulesPrivilegeIsNot(string rule, string why)
    {
        Assert.IsTrue(Atoms(rule).All(static atom => !atom.IsImplicit), why);
    }

    private static IReadOnlyList<GrantAtom> Atoms(string sql)
    {
        var model = new SemanticPipeline().Execute([new PgQueryParser().Parse("CREATE SCHEMA app; " + sql)]);
        return model.Grants.Values.SelectMany(grant => GrantExpansion.Expand(grant, model)).ToArray();
    }
}
