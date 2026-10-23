using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Dependencies.Tests;

/// <summary>
/// G-146: an owner is set after the object exists, so it depends on the object (a schema is not a node, so a schema's owner has no edge), and a grant on an
/// object comes after that object's owner is set: changing an owner moves the old owner's privileges to the new one, so a grant issued first could be moved.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class OwnerDependencyTests
{
    private const string Objects =
        "create schema s; create table s.t (id integer); create view s.v as select id from s.t; create sequence s.q; "
        + "create function s.f(x integer) returns integer language sql as $$ select x $$; create type s.mood as enum ('a'); ";

    [TestMethod]
    [DataRow("ALTER TABLE s.t OWNER TO o;", "s.t")]
    [DataRow("ALTER VIEW s.v OWNER TO o;", "s.v")]
    [DataRow("ALTER SEQUENCE s.q OWNER TO o;", "s.q")]
    [DataRow("ALTER TYPE s.mood OWNER TO o;", "s.mood")]
    public void AnOwnerDependsOnItsObject(string statement, string expected)
    {
        // Arrange
        var model = Build(Objects + statement);

        // Act
        var dependencies = new DependencyAnalyzer().Analyze(model).GetDependencies(model.Owners.Values.Single().Key).Select(static node => node.NodeId).ToArray();

        // Assert
        CollectionAssert.AreEqual(new[] { expected }, dependencies);
    }

    [TestMethod]
    public void AFunctionsOwnerDependsOnTheOverloadItNames()
    {
        // Arrange
        var model = Build(Objects + "ALTER FUNCTION s.f(int) OWNER TO o;");

        // Act
        var owner = model.Owners.Values.Single();
        var dependencies = new DependencyAnalyzer().Analyze(model).GetDependencies(owner.Key).Select(static node => node.NodeId).ToArray();

        // Assert
        CollectionAssert.AreEqual(new[] { owner.ObjectKey }, dependencies);
        Assert.IsTrue(model.Functions.ContainsKey(owner.ObjectKey));
    }

    [TestMethod]
    public void ASchemasOwnerHasNoEdgeBecauseASchemaIsNotANode()
    {
        // Act
        var model = Build(Objects + "ALTER SCHEMA s OWNER TO o;");

        // Assert
        Assert.IsEmpty(new DependencyAnalyzer().Analyze(model).GetDependencies(model.Owners.Values.Single().Key));
    }

    [TestMethod]
    public void AGrantOnAnObjectComesAfterThatObjectsOwner()
    {
        // Arrange
        var model = Build(Objects + "ALTER TABLE s.t OWNER TO o; GRANT SELECT ON s.t TO r;");
        var graph = new DependencyAnalyzer().Analyze(model);
        var grant = model.Grants.Values.Single();
        var owner = model.Owners.Values.Single();

        // Act
        var dependencies = graph.GetDependencies(grant.Key).Select(static node => node.NodeId).Order(StringComparer.Ordinal).ToArray();
        var order = graph.TopologicalSort().Select(static node => node.NodeId).ToList();

        // Assert: the table, then its owner, then the grant.
        CollectionAssert.AreEqual(new[] { owner.Key, "s.t" }.Order(StringComparer.Ordinal).ToArray(), dependencies);
        Assert.IsLessThan(order.IndexOf(owner.Key), order.IndexOf("s.t"));
        Assert.IsLessThan(order.IndexOf(grant.Key), order.IndexOf(owner.Key));
    }

    [TestMethod]
    public void AGrantOnAnObjectWhoseOwnerIsNotStatedKeepsOnlyItsObject()
    {
        // Arrange: a project that states no owner for the table has the graph it always had.
        var model = Build(Objects + "ALTER VIEW s.v OWNER TO o; GRANT SELECT ON s.t TO r;");

        // Act
        var dependencies = new DependencyAnalyzer().Analyze(model).GetDependencies(model.Grants.Values.Single().Key).Select(static node => node.NodeId).ToArray();

        // Assert
        CollectionAssert.AreEqual(new[] { "s.t" }, dependencies);
    }

    [TestMethod]
    public void AGrantOnEveryTableOfASchemaComesAfterTheirOwners()
    {
        // Arrange
        var model = Build(Objects + "ALTER TABLE s.t OWNER TO o; ALTER VIEW s.v OWNER TO o; GRANT SELECT ON ALL TABLES IN SCHEMA s TO r;");

        // Act
        var dependencies = new DependencyAnalyzer().Analyze(model).GetDependencies(model.Grants.Values.Single().Key).Select(static node => node.NodeId).ToArray();

        // Assert
        CollectionAssert.Contains(dependencies, "owner:table:s.t");
        CollectionAssert.Contains(dependencies, "owner:view:s.v");
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
