using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>G-160: what a project states about a schema (its comment) is in the model, by schema.</summary>
[TestClass]
public sealed class SchemaCommentSemanticsTests
{
    private static DatabaseModel Build(params string[] files) => new SemanticPipeline().Execute(files.Select(static sql => new PgQueryParser().Parse(sql)).ToArray());

    [TestMethod]
    public void AStatedCommentIsAnObjectOfTheModelKeyedBySchema()
    {
        var model = Build("CREATE SCHEMA app; COMMENT ON SCHEMA app IS 'The app.';");

        var schema = model.SchemaDefinitions[SchemaKeys.Create("app")];
        Assert.AreEqual("app", schema.Name);
        Assert.AreEqual("app", schema.SchemaName);
        Assert.AreEqual("The app.", schema.Comment);
        Assert.AreEqual("Schema", schema.Kind, "A schema the project declares is a schema, with its comment (G-284).");
        Assert.AreEqual("SchemaComment", Build("COMMENT ON SCHEMA app IS 'The app.';").SchemaDefinitions[SchemaKeys.Create("app")].Kind);
        Assert.IsTrue(model.GetAllNodes().Contains(schema));
        Assert.AreEqual(0, model.Diagnostics.Count, string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
    }

    [TestMethod]
    public void ASchemaWithNoStatementHasNoDefinition()
    {
        // Since G-284 a CREATE SCHEMA declares the schema, with no comment stated; a schema with no statement of its own still has no definition.
        var model = Build("CREATE SCHEMA app; CREATE TABLE app.t (id int); CREATE TABLE other.t (id int);");

        var schema = model.SchemaDefinitions.Values.Single();
        Assert.AreEqual("app", schema.Name);
        Assert.IsTrue(schema.IsDeclared);
        Assert.IsFalse(schema.IsCommentStated);
        Assert.IsNull(schema.Comment);
    }

    [TestMethod]
    public void NullAndAnEmptyCommentStateNone()
    {
        var model = Build("COMMENT ON SCHEMA a IS NULL; COMMENT ON SCHEMA b IS '';");

        Assert.IsNull(model.SchemaDefinitions[SchemaKeys.Create("a")].Comment);
        Assert.IsNull(model.SchemaDefinitions[SchemaKeys.Create("b")].Comment, "PostgreSQL stores an empty comment as none.");
    }

    [TestMethod]
    public void TheLastStatementWinsEvenAcrossFiles()
    {
        var model = Build("COMMENT ON SCHEMA app IS 'first';", "CREATE SCHEMA app; COMMENT ON SCHEMA app IS 'second';");

        Assert.AreEqual("second", model.SchemaDefinitions[SchemaKeys.Create("app")].Comment);
        Assert.AreEqual(1, model.SchemaDefinitions.Count);
    }

    [TestMethod]
    public void TheCommentMayBeInAnotherFileFromTheSchemasObjects()
    {
        var model = Build("CREATE TABLE app.t (id int);", "COMMENT ON SCHEMA app IS 'In another file.';");

        Assert.AreEqual("In another file.", model.SchemaDefinitions[SchemaKeys.Create("app")].Comment);
        Assert.IsTrue(model.Tables.ContainsKey("app.t"));
    }

    [TestMethod]
    public void TheKeyIsNotTheKeyOfTheOperationThatCreatesTheSchema()
    {
        Assert.AreNotEqual("schema:app", SchemaKeys.Create("app"));
    }
}
