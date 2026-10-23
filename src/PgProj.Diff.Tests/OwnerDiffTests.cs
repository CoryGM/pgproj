using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-146. The first argument of a diff is the database, the second the project. A stated owner that differs is a change of that one object's owner; a stated
/// owner the database does not have (a new object) is an addition; and an owner only the database has is <em>not</em> a change, because a project states an owner
/// for the objects it chooses to (decision 1). The same owner written another way is nothing, and a real difference is something.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class OwnerDiffTests
{
    private const string Objects = """
        CREATE SCHEMA app;
        CREATE TABLE app.t (id int PRIMARY KEY);
        CREATE VIEW app.v AS SELECT id FROM app.t;
        CREATE FUNCTION app.f(a integer) RETURNS integer LANGUAGE sql AS $$ SELECT a $$;
        """;

    [TestMethod]
    public void AModelWithOwnersDiffedWithItselfIsEmpty()
    {
        // Arrange
        var model = Build(Objects + "ALTER TABLE app.t OWNER TO lc_owner; ALTER SCHEMA app OWNER TO lc_owner;");

        // Act / Assert
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(model, model)));
    }

    [TestMethod]
    [DataRow("lc_owner", "LC_OWNER")]
    [DataRow("lc_owner", "Lc_Owner")]
    public void TheSameRoleInAnotherCaseIsNotAChange(string database, string project)
    {
        // Act
        var forward = new DiffEngine().Diff(Build(Objects + $"ALTER TABLE app.t OWNER TO {database};"), Build(Objects + $"ALTER TABLE app.t OWNER TO {project};"));
        var backward = new DiffEngine().Diff(Build(Objects + $"ALTER TABLE app.t OWNER TO {project};"), Build(Objects + $"ALTER TABLE app.t OWNER TO {database};"));

        // Assert
        Assert.IsTrue(IsEmpty(forward));
        Assert.IsTrue(IsEmpty(backward));
    }

    [TestMethod]
    public void TheOwnersStatedInAnotherOrderAreNotAChange()
    {
        // Arrange
        var first = Build(Objects + "ALTER TABLE app.t OWNER TO o; ALTER VIEW app.v OWNER TO o; ALTER SCHEMA app OWNER TO o;");
        var second = Build(Objects + "ALTER SCHEMA app OWNER TO o; ALTER VIEW app.v OWNER TO o; ALTER TABLE app.t OWNER TO o;");

        // Act / Assert
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(first, second)));
    }

    [TestMethod]
    public void AnotherRoleIsOneModificationOfThatObjectsOwner()
    {
        // Act
        var diff = new DiffEngine().Diff(Build(Objects + "ALTER TABLE app.t OWNER TO old_owner;"), Build(Objects + "ALTER TABLE app.t OWNER TO new_owner;"));

        // Assert: not an add and a remove, since the owner of the table is one thing whoever it is.
        Assert.AreEqual(0, diff.Added.Count);
        Assert.AreEqual(0, diff.Removed.Count);
        var modified = diff.Modified.Single();
        Assert.AreEqual(ChangeNodeKind.Owner, modified.NodeKind);
        Assert.AreEqual("owner:table:app.t", modified.NodeId);
        var detail = modified.Details.Single();
        Assert.AreEqual(nameof(OwnerDefinition.OwnerRole), detail.PropertyName);
        Assert.AreEqual("old_owner", detail.OldValue);
        Assert.AreEqual("new_owner", detail.NewValue);
    }

    [TestMethod]
    [DataRow("ALTER SCHEMA app OWNER TO {0};", "owner:schema:app")]
    [DataRow("ALTER TABLE app.t OWNER TO {0};", "owner:table:app.t")]
    [DataRow("ALTER VIEW app.v OWNER TO {0};", "owner:view:app.v")]
    [DataRow("ALTER FUNCTION app.f(int) OWNER TO {0};", "owner:function:app.f(int4)")]
    public void ARealDifferenceIsFoundForEachKind(string statement, string nodeId)
    {
        // Act
        var diff = new DiffEngine().Diff(Build(Objects + string.Format(null, statement, "a")), Build(Objects + string.Format(null, statement, "b")));

        // Assert
        Assert.AreEqual(nodeId, diff.Modified.Single().NodeId);
    }

    [TestMethod]
    public void AnOwnerTheProjectStatesForAnObjectTheDatabaseHasNoOwnerNodeForIsAnAddition()
    {
        // Act: the database model has the table but no owner node (an owner is not read, or the object's kind is one the loader does not read owners of).
        var diff = new DiffEngine().Diff(Build(Objects), Build(Objects + "ALTER TABLE app.t OWNER TO lc_owner;"));

        // Assert
        var added = diff.Added.Single();
        Assert.AreEqual(ChangeNodeKind.Owner, added.NodeKind);
        Assert.AreEqual("owner:table:app.t", added.NodeId);
        Assert.AreEqual(0, diff.Removed.Count + diff.Modified.Count);
    }

    [TestMethod]
    public void AnOwnerOnlyTheDatabaseHasIsNotAChange()
    {
        // Act: the database has an owner for the table and the project says nothing, which is every project that does not manage owners.
        var diff = new DiffEngine().Diff(Build(Objects + "ALTER TABLE app.t OWNER TO lc_owner; ALTER SCHEMA app OWNER TO lc_owner;"), Build(Objects));

        // Assert: there is no removed owner, the way there is a removed table.
        Assert.IsTrue(IsEmpty(diff));
        Assert.IsEmpty(new DiffEngine().Execute(Build(Objects + "ALTER TABLE app.t OWNER TO lc_owner;"), Build(Objects)).RemovedOwners);
    }

    [TestMethod]
    public void ANewObjectWithAnOwnerIsTheObjectAndItsOwnerAdded()
    {
        // Act
        var diff = new DiffEngine().Diff(Build("CREATE SCHEMA app;"), Build("CREATE SCHEMA app; CREATE TABLE app.t (id int); ALTER TABLE app.t OWNER TO lc_owner;"));

        // Assert: the table (with its columns and constraints, which a diff lists too) and exactly one owner are added.
        var kinds = diff.Added.Select(static change => change.NodeKind).ToArray();
        CollectionAssert.Contains(kinds, ChangeNodeKind.Table);
        Assert.AreEqual(1, kinds.Count(static kind => kind == ChangeNodeKind.Owner));
        Assert.AreEqual("owner:table:app.t", diff.Added.Single(static change => change.NodeKind == ChangeNodeKind.Owner).NodeId);
        Assert.AreEqual(0, diff.Removed.Count + diff.Modified.Count);
    }

    [TestMethod]
    public void ADroppedObjectsOwnerIsNotReportedBesideTheObject()
    {
        // Act: the project no longer has the table; the database still does, owned.
        var diff = new DiffEngine().Diff(Build("CREATE SCHEMA app; CREATE TABLE app.t (id int); ALTER TABLE app.t OWNER TO lc_owner;"), Build("CREATE SCHEMA app;"));

        // Assert: the table (and its columns and constraints) are removed; the owner is not a removal of its own, and nothing else changes.
        CollectionAssert.Contains(diff.Removed.Select(static change => change.NodeKind).ToArray(), ChangeNodeKind.Table);
        Assert.IsFalse(diff.Removed.Any(static change => change.NodeKind == ChangeNodeKind.Owner));
        Assert.AreEqual(0, diff.Added.Count + diff.Modified.Count);
    }

    [TestMethod]
    public void TheOwnerOfAnObjectThatAlsoChangesIsReportedBesideTheChange()
    {
        // Act
        var diff = new DiffEngine().Diff(
            Build("CREATE SCHEMA app; CREATE TABLE app.t (id int); ALTER TABLE app.t OWNER TO old_owner;"),
            Build("CREATE SCHEMA app; CREATE TABLE app.t (id int, extra text); ALTER TABLE app.t OWNER TO new_owner;"));

        // Assert: the table's own change and the owner's, each by itself.
        CollectionAssert.AreEquivalent(new[] { ChangeNodeKind.Table, ChangeNodeKind.Owner }, diff.Modified.Select(static change => change.NodeKind).ToArray());
    }

    private static bool IsEmpty(ChangeSet diff)
        => diff.Added.Count + diff.Removed.Count + diff.Modified.Count == 0;

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
