using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Dependencies.Tests;

/// <summary>
/// G-337: a grant on a type or a domain, and a revoked default on one, depend on that type: they come after it is created, and they are among its dependents, so a
/// type dropped and created again gets them back (<c>InsertRegrants</c>). Only the type named.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class TypeGrantDependencyTests
{
    private const string Types = "CREATE SCHEMA app; CREATE TYPE app.mood AS ENUM ('a'); CREATE TYPE app.other AS ENUM ('b'); CREATE DOMAIN app.pos AS integer; CREATE TABLE app.mood_t (id integer); ";

    [TestMethod]
    [DataRow("GRANT USAGE ON TYPE app.mood TO r;", "app.mood")]
    [DataRow("GRANT USAGE ON DOMAIN app.pos TO r;", "app.pos")]
    [DataRow("GRANT USAGE ON TYPE app.pos TO r;", "app.pos")]
    [DataRow("REVOKE ALL ON TYPE app.mood FROM PUBLIC;", "app.mood")]
    public void ATypeGrantDependsOnlyOnTheTypeItNames(string grant, string expected)
    {
        var model = Build(Types + grant);

        var dependencies = new DependencyAnalyzer().Analyze(model).GetDependencies(model.Grants.Values.Single().Key).Select(static node => node.NodeId).ToArray();

        CollectionAssert.AreEqual(new[] { expected }, dependencies);
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
