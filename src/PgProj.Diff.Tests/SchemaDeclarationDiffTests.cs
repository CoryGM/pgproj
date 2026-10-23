using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-284: a schema the project declares with <c>CREATE SCHEMA</c> is added when the database lacks it, even with nothing in it, and is no change when the database has it.
/// A schema is never removed, and a declared schema's comment is compared only when the project states one (G-160).
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SchemaDeclarationDiffTests
{
    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);

    private static ChangeSet Diff(string live, string project) => new DiffEngine().Diff(Build(live), Build(project));

    private static IEnumerable<ChangeRecord> SchemaChanges(ChangeSet changes)
        => changes.Added.Cast<ChangeRecord>().Concat(changes.Modified).Concat(changes.Removed).Where(static change => change.NodeKind == ChangeNodeKind.SchemaComment);

    [TestMethod]
    public void ADeclaredSchemaTheDatabaseLacksIsAddedAsASchema()
    {
        var changes = Diff("CREATE SCHEMA IF NOT EXISTS b; CREATE TABLE b.t (id int);", "CREATE SCHEMA a; CREATE SCHEMA b; CREATE TABLE b.t (id int);");

        var added = SchemaChanges(changes).Cast<AddedChange>().Single();
        Assert.AreEqual("a", ((SchemaDefinition)added.Node).Name);
        Assert.AreEqual("Schema", added.Node.Kind);
        Assert.AreEqual(0, changes.Modified.Count + changes.Removed.Count);
    }

    [TestMethod]
    public void ADeclaredSchemaTheDatabaseHasIsNoChange()
    {
        var sql = "CREATE SCHEMA a;";

        Assert.AreEqual(0, SchemaChanges(Diff(sql, sql)).Count());
    }

    [TestMethod]
    public void CaseOfAnUnquotedNameAndIfNotExistsAreNoChange()
    {
        Assert.AreEqual(0, SchemaChanges(Diff("create schema app;", "CREATE SCHEMA IF NOT EXISTS App;")).Count());
    }

    [TestMethod]
    public void AQuotedNameIsADifferentSchema()
    {
        var added = SchemaChanges(Diff("CREATE SCHEMA app;", "CREATE SCHEMA \"App\";")).Cast<AddedChange>().Single();

        Assert.AreEqual("App", ((SchemaDefinition)added.Node).Name);
    }

    [TestMethod]
    public void ASchemaNamedByItsAuthorizationIsDeclared()
    {
        var added = SchemaChanges(Diff(string.Empty, "CREATE SCHEMA AUTHORIZATION app_owner;")).Cast<AddedChange>().Single();

        Assert.AreEqual("app_owner", ((SchemaDefinition)added.Node).Name);
    }

    [TestMethod]
    public void PublicAndTheSystemSchemasAreNeverAdded()
    {
        Assert.AreEqual(0, SchemaChanges(Diff(string.Empty, "CREATE SCHEMA IF NOT EXISTS public; CREATE SCHEMA IF NOT EXISTS pg_catalog;")).Count());
    }

    [TestMethod]
    public void ASchemaOnlyTheDatabaseDeclaresIsNotRemoved()
    {
        var changes = Diff("CREATE SCHEMA old;", string.Empty);

        Assert.AreEqual(0, changes.Removed.Count);
        Assert.AreEqual(0, SchemaChanges(changes).Count());
    }

    [TestMethod]
    public void ADeclaredSchemaKeepsTheDatabasesCommentWhenTheProjectStatesNone()
    {
        Assert.AreEqual(0, SchemaChanges(Diff("CREATE SCHEMA app; COMMENT ON SCHEMA app IS 'Old.';", "CREATE SCHEMA app;")).Count());
    }

    [TestMethod]
    public void ADeclaredSchemasStatedCommentIsStillCompared()
    {
        var modified = SchemaChanges(Diff("CREATE SCHEMA app; COMMENT ON SCHEMA app IS 'Old.';", "CREATE SCHEMA app; COMMENT ON SCHEMA app IS 'New.';")).Cast<ModifiedChange>().Single();

        Assert.AreEqual(nameof(SchemaDefinition.Comment), modified.Details.Single().PropertyName);
    }

    [TestMethod]
    public void ACommentWithoutCreateSchemaIsStillASchemaComment()
    {
        var added = SchemaChanges(Diff(string.Empty, "COMMENT ON SCHEMA app IS 'The app.';")).Cast<AddedChange>().Single();

        Assert.AreEqual("SchemaComment", added.Node.Kind);
    }

    [TestMethod]
    public void ACommentInAnEarlierFileThanTheCreateSchemaIsKept()
    {
        var project = new SemanticPipeline().Execute([new PgQueryParser().Parse("COMMENT ON SCHEMA app IS 'The app.';"), new PgQueryParser().Parse("CREATE SCHEMA app;")]);

        var schema = project.SchemaDefinitions[SchemaKeys.Create("app")];
        Assert.IsTrue(schema.IsDeclared);
        Assert.IsTrue(schema.IsCommentStated);
        Assert.AreEqual("The app.", schema.Comment);
    }
}
