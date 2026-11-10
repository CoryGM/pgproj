using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-337: a grant on a type or a domain is declared as a grant (it was a warning, "GRANT ... ON TYPE is recognized but not modeled"), one grant however it is
/// written (<c>ON TYPE</c> or <c>ON DOMAIN</c>, <c>USAGE</c> or <c>ALL</c>); and <c>REVOKE USAGE|ALL ... FROM PUBLIC</c> on one is the one revoke pgproj reads
/// (option (B), the user's decision of 2026-11-09), declared as a <see cref="GrantDefinition.RevokesDefault"/> definition. Every other revoke stays a warning.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class TypeGrantDeclarationTests
{
    private const string Types = "CREATE SCHEMA app; CREATE TYPE app.mood AS ENUM ('a'); CREATE DOMAIN app.pos AS integer; ";

    [TestMethod]
    [DataRow("GRANT USAGE ON TYPE app.mood TO r;", "type:app.mood->r = ALL PRIVILEGES")]
    [DataRow("GRANT ALL ON TYPE app.mood TO r;", "type:app.mood->r = ALL PRIVILEGES")]
    [DataRow("GRANT ALL PRIVILEGES ON TYPE app.mood TO r;", "type:app.mood->r = ALL PRIVILEGES")]
    [DataRow("GRANT USAGE ON DOMAIN app.pos TO r;", "type:app.pos->r = ALL PRIVILEGES")]
    [DataRow("GRANT USAGE ON TYPE app.pos TO r;", "type:app.pos->r = ALL PRIVILEGES")] // a domain is a type, and pg_dump writes it ON TYPE
    [DataRow("GRANT USAGE ON TYPE APP.MOOD TO r;", "type:app.mood->r = ALL PRIVILEGES")] // unquoted names fold
    public void AGrantOnATypeOrDomainIsDeclared(string sql, string expected)
    {
        var model = Build(Types + sql);

        Assert.AreEqual(expected, Describe(model).Single());
        Assert.IsFalse(model.Diagnostics.Any(static diagnostic => diagnostic.Severity == SemanticDiagnosticSeverity.Warning), string.Join("\n", model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    [TestMethod]
    public void TheSameGrantWrittenOnTypeOrOnDomainHasOneKey()
    {
        Assert.AreEqual(
            Build(Types + "GRANT USAGE ON DOMAIN app.pos TO r WITH GRANT OPTION;").Grants.Keys.Single(),
            Build(Types + "GRANT ALL ON TYPE app.pos TO r WITH GRANT OPTION;").Grants.Keys.Single());
    }

    [TestMethod]
    public void AnUnqualifiedTypeIsTheOneTheSearchPathFinds()
    {
        var model = new SemanticPipeline().Execute([new PgQueryParser().Parse(Types + "GRANT USAGE ON TYPE mood TO r;")], searchPath: ["app"]);

        Assert.AreEqual("type:app.mood->r = ALL PRIVILEGES", Describe(model).Single());
    }

    [TestMethod]
    [DataRow("REVOKE ALL ON TYPE app.mood FROM PUBLIC;", "app.mood")]
    [DataRow("REVOKE USAGE ON TYPE app.mood FROM public;", "app.mood")]
    [DataRow("REVOKE ALL PRIVILEGES ON TYPE app.pos FROM PUBLIC;", "app.pos")]
    [DataRow("REVOKE USAGE ON DOMAIN app.pos FROM PUBLIC;", "app.pos")]
    public void ARevokeFromPublicOnATypeIsARevokedDefault(string sql, string type)
    {
        var model = Build(Types + sql);

        var revoked = model.Grants.Values.Single();
        Assert.IsTrue(revoked.RevokesDefault);
        Assert.AreEqual($"type:{type}", revoked.ObjectKey);
        Assert.AreEqual("PUBLIC", revoked.Grantee);
        Assert.IsFalse(revoked.WithGrantOption);
        Assert.IsEmpty(GrantExpansion.Expand(revoked, model), "A revoked default stands for no atom: it is compared by its key.");
        Assert.IsFalse(model.Diagnostics.Any(static diagnostic => diagnostic.Severity == SemanticDiagnosticSeverity.Warning), string.Join("\n", model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    [TestMethod]
    public void ARevokedDefaultIsNotTheGrantToPublicItUndoes()
    {
        var revoked = Build(Types + "REVOKE ALL ON TYPE app.mood FROM PUBLIC;").Grants.Keys.Single();
        var granted = Build(Types + "GRANT ALL ON TYPE app.mood TO PUBLIC;").Grants.Keys.Single();

        Assert.AreNotEqual(revoked, granted);
    }

    [TestMethod]
    public void ARevokeFromPublicAndARoleReadsOnlyPublicAndSaysSo()
    {
        var model = Build(Types + "REVOKE ALL ON TYPE app.mood FROM PUBLIC, r;");

        Assert.IsTrue(model.Grants.Values.Single().RevokesDefault);
        var warning = model.Diagnostics.Single(static diagnostic => diagnostic.Severity == SemanticDiagnosticSeverity.Warning);
        StringAssert.Contains(warning.Message, "REVOKE ... FROM r is not modeled");
        StringAssert.Contains(warning.Message, "Only REVOKE ... FROM PUBLIC is read on a type or domain.");
    }

    [TestMethod]
    [DataRow("REVOKE USAGE ON TYPE app.mood FROM r;")]
    [DataRow("REVOKE GRANT OPTION FOR USAGE ON TYPE app.mood FROM PUBLIC;")]
    [DataRow("REVOKE EXECUTE ON FUNCTION app.f() FROM PUBLIC;")] // routines are G-338
    public void EveryOtherRevokeIsStillAWarning(string sql)
    {
        var model = Build(Types + sql);

        Assert.IsEmpty(model.Grants);
        StringAssert.Contains(model.Diagnostics.Single(static diagnostic => diagnostic.Severity == SemanticDiagnosticSeverity.Warning).Message, "REVOKE is not modeled and is ignored");
    }

    [TestMethod]
    public void ExcludedGrantsLeaveOutARevokedDefaultToo()
    {
        var model = new SemanticPipeline().Execute([new PgQueryParser().Parse(Types + "REVOKE ALL ON TYPE app.mood FROM PUBLIC; GRANT USAGE ON TYPE app.mood TO r;")], excludeGrants: true);

        Assert.IsEmpty(model.Grants);
        Assert.IsFalse(model.Diagnostics.Any(static diagnostic => diagnostic.Severity == SemanticDiagnosticSeverity.Warning));
    }

    [TestMethod]
    public void PublicsUsageOnATypeIsImplicitAndAnyOtherIsNot()
    {
        var model = Build(Types + "GRANT USAGE ON TYPE app.mood TO PUBLIC; GRANT USAGE ON TYPE app.mood TO r;");
        var atoms = model.Grants.Values.SelectMany(grant => GrantExpansion.Expand(grant, model)).ToArray();

        Assert.IsTrue(atoms.Single(static atom => atom.Grantee == "public").IsImplicit, "PostgreSQL's default; a revoked default decides it.");
        Assert.IsFalse(atoms.Single(static atom => atom.Grantee == "r").IsImplicit);
        Assert.AreEqual("type:app.mood", atoms.Single(static atom => atom.Grantee == "r").Target);
    }

    private static string[] Describe(DatabaseModel model)
        => model.Grants.Values
            .Select(static grant => $"{grant.ObjectKey}->{grant.Grantee} = {string.Join(", ", grant.Privileges)}")
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
