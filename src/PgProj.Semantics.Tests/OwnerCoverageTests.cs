using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-146, decision 1: an object whose owner the project does not state is not compared, and so that this is seen and not silent, a project that states
/// some owners is told how many objects it leaves out. A project that states none is not (it does not manage owners; telling every existing project that all
/// of its objects state none would be noise on every build).
/// </summary>
[TestClass]
public sealed class OwnerCoverageTests
{
    private const string Objects = """
        CREATE SCHEMA app;
        CREATE TABLE app.t (id int PRIMARY KEY);
        CREATE FUNCTION app.f() RETURNS integer LANGUAGE sql AS $$ SELECT 1 $$;
        CREATE VIEW app.v AS SELECT id FROM app.t;
        """;

    [TestMethod]
    public void AProjectThatStatesNoOwnerIsToldNothing()
    {
        // Act / Assert
        Assert.IsNull(OwnerCoverage.Describe(Build(Objects)));
    }

    [TestMethod]
    public void AProjectThatStatesSomeIsToldHowManyObjectsItLeavesOut()
    {
        // Act: the schema, the function, and the view state none.
        var notice = OwnerCoverage.Describe(Build(Objects + "\nALTER TABLE app.t OWNER TO lc_owner;"));

        // Assert
        Assert.IsNotNull(notice);
        Assert.AreEqual(SemanticDiagnosticSeverity.Info, notice.Severity);
        Assert.AreEqual("3 objects state no owner (ALTER ... OWNER TO); their owners are not compared.", notice.Message);
        Assert.AreEqual(OwnerCoverage.DiagnosticSource, notice.Source);
    }

    [TestMethod]
    public void OneObjectIsSaidInTheSingular()
    {
        // Act
        var notice = OwnerCoverage.Describe(Build(Objects + "\nALTER SCHEMA app OWNER TO o; ALTER TABLE app.t OWNER TO o; ALTER FUNCTION app.f() OWNER TO o;"));

        // Assert
        Assert.AreEqual("1 object states no owner (ALTER ... OWNER TO); its owner is not compared.", notice!.Message);
    }

    [TestMethod]
    public void AProjectThatStatesEveryOwnerIsToldNothing()
    {
        // Act
        var model = Build(Objects + "\nALTER SCHEMA app OWNER TO o; ALTER TABLE app.t OWNER TO o; ALTER FUNCTION app.f() OWNER TO o; ALTER VIEW app.v OWNER TO o;");

        // Assert
        Assert.IsNull(OwnerCoverage.Describe(model));
        Assert.AreEqual(0, OwnerCoverage.CountWithoutOwner(model));
    }

    [TestMethod]
    public void ASchemaIsCountedOnceHoweverManyObjectsItHolds()
    {
        // Act: two tables in one schema, neither stated, plus one stated in another schema.
        var model = Build("CREATE SCHEMA a; CREATE SCHEMA b; CREATE TABLE a.x (i int); CREATE TABLE a.y (i int); CREATE TABLE b.z (i int); ALTER TABLE b.z OWNER TO o; ALTER SCHEMA b OWNER TO o;");

        // Assert: x, y, and schema a.
        Assert.AreEqual(3, OwnerCoverage.CountWithoutOwner(model));
    }

    [TestMethod]
    public void ASequenceAColumnOwnsHasNoOwnerToStateAndIsNotCounted()
    {
        // Act: the identity column's sequence follows its table.
        var model = Build("CREATE SCHEMA app; CREATE TABLE app.t (id int GENERATED ALWAYS AS IDENTITY); ALTER TABLE app.t OWNER TO o; ALTER SCHEMA app OWNER TO o;");

        // Assert
        Assert.AreEqual(0, OwnerCoverage.CountWithoutOwner(model), string.Join(", ", model.Sequences.Keys));
    }

    [TestMethod]
    public void AnExtensionIsNotCounted()
    {
        // Act
        var model = Build("CREATE EXTENSION citext; CREATE SCHEMA app; CREATE TABLE app.t (id int); ALTER TABLE app.t OWNER TO o; ALTER SCHEMA app OWNER TO o;");

        // Assert
        Assert.AreEqual(0, OwnerCoverage.CountWithoutOwner(model));
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
