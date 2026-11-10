using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Dependencies.Tests;

/// <summary>
/// G-339: an object a default-privilege rule covers depends on the rule, so the rule runs first and the object is created with what it gives, as the project's SQL
/// would on an empty database. A rule covers its kind (<c>TABLES</c>: tables and views; <c>FUNCTIONS</c>: functions and procedures; <c>TYPES</c>: types and
/// domains; <c>SEQUENCES</c>), globally or in its schema, and nothing else.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class DefaultPrivilegeDependencyTests
{
    private const string Rules =
        "CREATE SCHEMA app; CREATE SCHEMA other; "
        + "ALTER DEFAULT PRIVILEGES IN SCHEMA app GRANT SELECT ON TABLES TO r; "
        + "ALTER DEFAULT PRIVILEGES GRANT USAGE ON SEQUENCES TO r; "
        + "ALTER DEFAULT PRIVILEGES IN SCHEMA app GRANT EXECUTE ON FUNCTIONS TO r; "
        + "ALTER DEFAULT PRIVILEGES FOR ROLE o IN SCHEMA app GRANT USAGE ON TYPES TO r; ";

    [TestMethod]
    [DataRow("CREATE TABLE app.t (id integer);", "app.t", "default tables in schema:app")]
    [DataRow("CREATE TABLE app.t (id integer); CREATE VIEW app.v AS SELECT id FROM app.t;", "app.v", "default tables in schema:app")]
    [DataRow("CREATE SEQUENCE other.q;", "other.q", "default sequences")]
    [DataRow("CREATE FUNCTION app.f() RETURNS integer LANGUAGE sql AS $$ SELECT 1 $$;", "app.f", "default functions in schema:app")]
    [DataRow("CREATE PROCEDURE app.p() LANGUAGE sql AS $$ SELECT 1 $$;", "app.p", "default functions in schema:app")]
    [DataRow("CREATE TYPE app.mood AS ENUM ('a');", "app.mood", "default types in schema:app")]
    [DataRow("CREATE DOMAIN app.pos AS integer;", "app.pos", "default types in schema:app")]
    public void AnObjectARuleCoversDependsOnTheRule(string objects, string objectKey, string ruleKey)
    {
        var model = Build(Rules + objects);
        var node = model.GetAllNodes().Single(candidate => string.Equals(candidate.Key, objectKey, StringComparison.Ordinal) || candidate.Key.StartsWith(objectKey + "(", StringComparison.Ordinal));

        var rules = RuleDependencies(model, node.Key);

        CollectionAssert.Contains(rules, ruleKey, string.Join(", ", rules));
    }

    [TestMethod]
    public void ARuleInAnotherSchemaOrOfAnotherKindIsNotADependency()
    {
        var model = Build(Rules + "CREATE TABLE other.t (id integer); CREATE TYPE other.mood AS ENUM ('a');");

        Assert.IsEmpty(RuleDependencies(model, "other.t"), "The tables rule is app's only.");
        Assert.IsEmpty(RuleDependencies(model, "other.mood"), "The types rule is app's only.");
    }

    [TestMethod]
    public void ARuleIsOrderedBeforeTheObjectsItCovers()
    {
        var model = Build(Rules + "CREATE TABLE app.t (id integer);");
        var order = new DependencyAnalyzer().Analyze(model).TopologicalSort().Select(static node => node.NodeId).ToList();
        var rule = model.Grants.Values.Single(static grant => grant.ObjectKey == "default tables in schema:app").Key;

        Assert.IsLessThan(order.IndexOf("app.t"), order.IndexOf(rule));
    }

    /// <summary>The <c>ObjectKey</c>s of the default-privilege rules <paramref name="nodeKey"/> depends on.</summary>
    private static string[] RuleDependencies(DatabaseModel model, string nodeKey)
        => new DependencyAnalyzer().Analyze(model).GetDependencies(nodeKey)
            .Select(node => model.Grants.TryGetValue(node.NodeId, out var grant) ? grant.ObjectKey : null)
            .OfType<string>()
            .Where(static key => key.StartsWith("default ", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
