using PgProj.Dependencies;
using PgProj.Deploy;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Engine.Tests;

/// <summary>
/// G-337: <c>publish</c> names each statement that gives <c>PUBLIC</c> a privilege on an object the database already has, which <c>--allow-destructive</c> does
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
        StringAssert.Contains(PublicGrants.Describe(found), "gives PUBLIC (every role) privileges on objects the database already has");
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

    private static IReadOnlyList<string> Find(string liveSql, string projectSql)
    {
        var live = Build(liveSql);
        var project = Build(projectSql);
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(live, project), new DependencyAnalyzer().Analyze(live, project));
        return PublicGrants.Find(batch, live);
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
