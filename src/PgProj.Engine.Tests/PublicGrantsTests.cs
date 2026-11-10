using PgProj.Dependencies;
using PgProj.Deploy;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Engine.Tests;

/// <summary>
/// G-337, G-338: <c>publish</c> names each statement that gives <c>PUBLIC</c> a privilege on an object the database already has, which <c>--allow-destructive</c> does
/// not cover: a type's <c>USAGE</c> given back because the project does not revoke it, and a grant to <c>PUBLIC</c> the project adds on a table, sequence,
/// routine, type, or schema that exists. Not a grant on an object the script creates, one the database already had, or one to a role.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class PublicGrantsTests
{
    private const string Objects = "CREATE SCHEMA app; CREATE TYPE app.mood AS ENUM ('a'); CREATE TABLE app.t (id integer); CREATE FUNCTION app.f() RETURNS integer LANGUAGE sql AS $$ SELECT 1 $$; ";

    [TestMethod]
    public void ATypesDefaultGivenBackIsNamed()
    {
        var found = Find(Objects + "REVOKE ALL ON TYPE app.mood FROM PUBLIC;", Objects);

        CollectionAssert.AreEqual(new[] { "GRANT ALL PRIVILEGES ON TYPE \"app\".\"mood\" TO PUBLIC;" }, found.ToArray());
        StringAssert.Contains(PublicGrants.Describe(PublicGrants.Find(Batch(Objects + "REVOKE ALL ON TYPE app.mood FROM PUBLIC;", Objects), Build(Objects + "REVOKE ALL ON TYPE app.mood FROM PUBLIC;"))), "PUBLIC is every role");
    }

    [TestMethod]
    [DataRow("GRANT SELECT ON app.t TO PUBLIC;", "GRANT SELECT ON TABLE \"app\".\"t\" TO PUBLIC;")]
    [DataRow("GRANT USAGE ON TYPE app.mood TO PUBLIC WITH GRANT OPTION;", "GRANT ALL PRIVILEGES ON TYPE \"app\".\"mood\" TO PUBLIC WITH GRANT OPTION;")]
    [DataRow("GRANT CREATE ON SCHEMA app TO PUBLIC;", "GRANT CREATE ON SCHEMA \"app\" TO PUBLIC;")]
    [DataRow("GRANT SELECT ON ALL TABLES IN SCHEMA app TO PUBLIC;", "GRANT SELECT ON ALL TABLES IN SCHEMA \"app\" TO PUBLIC;")]
    public void AGrantToPublicTheProjectAddsOnSomethingThatExistsIsNamed(string grant, string expected)
    {
        var found = Find(Objects, Objects + grant);
        CollectionAssert.AreEqual(new[] { expected }, found.ToArray(), string.Join(" | ", found));
    }

    [TestMethod]
    [DataRow("", "GRANT SELECT ON app.t TO PUBLIC;", "a new table")]
    [DataRow("GRANT SELECT ON app.t TO PUBLIC;", "GRANT SELECT ON app.t TO PUBLIC;", "a grant the database had")]
    [DataRow("", "GRANT SELECT ON app.t TO r;", "a role, not PUBLIC")]
    [DataRow("", "ALTER DEFAULT PRIVILEGES GRANT SELECT ON TABLES TO PUBLIC;", "default privileges, on no existing object")]
    public void NothingElseIsNamed(string live, string project, string why)
    {
        var liveSql = live.Length == 0 && why == "a new table" ? "CREATE SCHEMA app; " : Objects + live;
        var projectSql = why == "a new table" ? "CREATE SCHEMA app; CREATE TABLE app.t (id integer); " + project : Objects + project;

        Assert.IsEmpty(Find(liveSql, projectSql), why);
    }

    [TestMethod]
    public void AGrantPutBackOnARecreatedObjectIsNotNamed()
    {
        var grant = Build(Objects + "GRANT SELECT ON app.t TO PUBLIC;").Grants.Values.Single();
        var batch = new OperationBatch
        {
            Operations = [new CreateOperation { NodeId = grant.Key, NodeKind = ChangeNodeKind.Grant, Node = grant, IsRestored = true }],
        };

        Assert.IsEmpty(PublicGrants.Find(batch, Build(Objects)));
    }

    [TestMethod]
    public void ARoutinesDefaultGivenBackIsNamedWithWhatItMeansAndHowToKeepItRevoked()
    {
        const string routines = "CREATE SCHEMA app; CREATE FUNCTION app.f() RETURNS integer LANGUAGE sql SECURITY DEFINER AS $$ SELECT 1 $$; CREATE PROCEDURE app.p() LANGUAGE sql AS $$ SELECT 1 $$; ";
        const string revokes = "REVOKE ALL ON FUNCTION app.f() FROM PUBLIC; REVOKE ALL ON PROCEDURE app.p() FROM PUBLIC;";
        var found = PublicGrants.Find(Batch(routines + revokes, routines), Build(routines + revokes));

        CollectionAssert.AreEquivalent(
            new[] { "GRANT ALL PRIVILEGES ON FUNCTION \"app\".\"f\"() TO PUBLIC;", "GRANT ALL PRIVILEGES ON PROCEDURE \"app\".\"p\"() TO PUBLIC;" },
            found.Select(static grant => grant.Statement).ToArray());
        Assert.IsTrue(found.All(static grant => grant.Reason == PublicGrantReason.DefaultGivenBack));

        var warning = PublicGrants.Describe(found);
        StringAssert.StartsWith(warning, "WARNING:");
        StringAssert.Contains(warning, "PUBLIC is every role, including roles created later");
        StringAssert.Contains(warning, "the database has it revoked and the project does not revoke it");
        StringAssert.Contains(warning, "function app.f() (SECURITY DEFINER: it runs with its owner's privileges, whoever calls it): every role can execute it.");
        StringAssert.Contains(warning, "procedure app.p(): every role can execute it.");
        Assert.IsFalse(warning.Contains("procedure app.p() (SECURITY DEFINER", StringComparison.Ordinal), "Only a SECURITY DEFINER routine says so.");
        StringAssert.Contains(warning, "To keep them revoked, add to the project:");
        StringAssert.Contains(warning, "REVOKE ALL PRIVILEGES ON FUNCTION \"app\".\"f\"() FROM PUBLIC;");
        StringAssert.Contains(warning, "pgproj init writes these REVOKE statements");
        StringAssert.Contains(warning, "If any of this is not what you want, do not run this publish.");
    }

    [TestMethod]
    public void ARulesDefaultGivenBackIsNamedForWhatIsCreatedFromNowOn()
    {
        const string rule = "ALTER DEFAULT PRIVILEGES REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;";
        var found = PublicGrants.Find(Batch(Objects + rule, Objects), Build(Objects + rule));

        Assert.AreEqual(PublicGrantReason.RuleGivenBack, found.Single().Reason);
        var warning = PublicGrants.Describe(found);
        StringAssert.Contains(warning, "every function and procedure created from now on: every role can execute it.");
        StringAssert.Contains(warning, "ALTER DEFAULT PRIVILEGES GRANT ALL PRIVILEGES ON FUNCTIONS TO PUBLIC;");
        StringAssert.Contains(warning, "ALTER DEFAULT PRIVILEGES REVOKE ALL PRIVILEGES ON FUNCTIONS FROM PUBLIC;");
    }

    [TestMethod]
    public void AGrantTheProjectAddsSaysWhatEveryRoleGets()
    {
        var found = PublicGrants.Find(Batch(Objects, Objects + "GRANT SELECT ON app.t TO PUBLIC;"), Build(Objects));

        var warning = PublicGrants.Describe(found);
        StringAssert.Contains(warning, "The project grants PUBLIC a privilege on something the database already has:");
        StringAssert.Contains(warning, "table app.t: every role gets SELECT.");
        StringAssert.Contains(warning, "remove the GRANT ... TO PUBLIC from the project");
    }

    private static IReadOnlyList<string> Find(string liveSql, string projectSql)
        => PublicGrants.Find(Batch(liveSql, projectSql), Build(liveSql)).Select(static grant => grant.Statement).ToArray();

    private static OperationBatch Batch(string liveSql, string projectSql)
    {
        var live = Build(liveSql);
        var project = Build(projectSql);
        return new ScriptGenerator().Generate(new DiffEngine().Diff(live, project), new DependencyAnalyzer().Analyze(live, project));
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
