using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core.Tests;

/// <summary>
/// G-166: pgproj never drops a schema (the user's decision: warn, do not change the behaviour). <c>diff</c> and <c>publish</c> name the
/// schemas the database has objects in that the project no longer does, so the user knows what a publish leaves behind.
/// </summary>
[TestClass]
public sealed class UndroppedSchemasTests
{
    [TestMethod]
    public void ASchemaTheProjectStillUsesIsNotNamed()
    {
        // Arrange
        var live = Build("CREATE TABLE app.t (a int); CREATE TABLE app.u (a int);");
        var source = Build("CREATE TABLE app.t (a int);");

        // Act / Assert: app.u is a removed table, not a removed schema.
        Assert.IsEmpty(UndroppedSchemas.Find(live, source));
    }

    [TestMethod]
    public void ASchemaOnlyTheDatabaseUsesIsNamedInOrder()
    {
        // Arrange
        var live = Build("CREATE TABLE zed.t (a int); CREATE VIEW old.v AS SELECT 1 AS a; CREATE TABLE app.t (a int);");
        var source = Build("CREATE TABLE app.t (a int);");

        // Act
        var schemas = UndroppedSchemas.Find(live, source);

        // Assert
        CollectionAssert.AreEqual(new[] { "old", "zed" }, schemas.ToArray());
    }

    [TestMethod]
    public void PublicAndTheSystemSchemasAreNeverNamed()
    {
        // Arrange: public always exists and is never the project's to drop.
        var live = Build("CREATE TABLE public.t (a int); CREATE TABLE app.t (a int);");
        var source = Build("CREATE TABLE app.t (a int);");

        // Act / Assert
        Assert.IsEmpty(UndroppedSchemas.Find(live, source));
    }

    [TestMethod]
    public void ASchemaNameIsComparedWithoutRegardToCase()
    {
        // Arrange
        var live = Build("CREATE TABLE App.t (a int);");
        var source = Build("CREATE TABLE app.t (a int);");

        // Act / Assert
        Assert.IsEmpty(UndroppedSchemas.Find(live, source));
    }

    [TestMethod]
    public void ASchemaTheDatabaseOnlyDeclaresIsNotNamed()
    {
        // Arrange: the live read declares every schema it reads (G-284); one with nothing in it was never named, and still is not.
        var live = Build("CREATE SCHEMA IF NOT EXISTS empty; CREATE TABLE app.t (a int);");
        var source = Build("CREATE TABLE app.t (a int);");

        // Act / Assert
        Assert.IsEmpty(UndroppedSchemas.Find(live, source));
    }

    [TestMethod]
    public void ASchemaTheProjectDeclaresIsKeptEvenWithNothingInIt()
    {
        // Arrange: the project still declares app, so app is not left behind (G-284); its table is a removed table.
        var live = Build("CREATE SCHEMA IF NOT EXISTS app; CREATE TABLE app.t (a int);");
        var source = Build("CREATE SCHEMA app;");

        // Act / Assert
        Assert.IsEmpty(UndroppedSchemas.Find(live, source));
    }

    [TestMethod]
    public void TheWarningSaysSchemasAreNotDroppedAndNamesThem()
    {
        // Act
        var one = UndroppedSchemas.Describe(["old"]);
        var two = UndroppedSchemas.Describe(["old", "zed"]);

        // Assert
        StringAssert.StartsWith(one, "Warning: pgproj does not drop schemas; schema old ");
        StringAssert.StartsWith(two, "Warning: pgproj does not drop schemas; schemas old, zed ");
        StringAssert.Contains(one, "DROP SCHEMA");
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
