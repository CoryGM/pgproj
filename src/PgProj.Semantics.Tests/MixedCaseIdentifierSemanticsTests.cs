using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-70: PostgreSQL keeps the case of a quoted identifier, and the model folded every name to lower case, so the name pgproj deployed was
/// not the name the project declared. The model now keeps the spelling of a name (it is what generated SQL writes), while identity stays
/// case-insensitive as it always was: keys and lookups are lower case, so two spellings of one name are one object.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class MixedCaseIdentifierSemanticsTests
{
    private const string Project = """
        CREATE SCHEMA "MySchema";
        CREATE TYPE "MySchema"."Mood" AS ENUM ('sad', 'ok');
        CREATE SEQUENCE "MySchema"."TicketSeq";
        CREATE TABLE "MySchema"."Parent" ("Id" integer PRIMARY KEY, "Code" text UNIQUE, "State" "MySchema"."Mood" DEFAULT 'ok');
        CREATE TABLE "MySchema"."Child" (
            "Id" integer,
            "ParentId" integer REFERENCES "MySchema"."Parent" ("Id"),
            "Amount" integer CHECK ("Amount" > 0),
            CONSTRAINT "Child_Pk" PRIMARY KEY ("Id"),
            CONSTRAINT "Child_Uq" UNIQUE ("ParentId", "Amount"));
        CREATE INDEX "IxChild" ON "MySchema"."Child" ("Amount", lower("ParentId"::text)) INCLUDE ("Id") WHERE "Amount" > 0;
        CREATE VIEW "MySchema"."ChildView" AS SELECT "Id", "Amount" FROM "MySchema"."Child";
        CREATE FUNCTION "MySchema"."DoIt"() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$;
        CREATE TRIGGER "TrgChild" AFTER UPDATE OF "Amount", "ParentId" ON "MySchema"."Child" FOR EACH ROW EXECUTE FUNCTION "MySchema"."DoIt"();
        """;

    [TestMethod]
    public void TheModelHasNoErrors()
    {
        var model = Build(Project);

        Assert.IsFalse(HasErrors(model), Dump(model));
    }

    [TestMethod]
    public void EveryKindOfObjectKeepsTheSpellingOfItsName()
    {
        var model = Build(Project);

        var parent = model.Tables.Values.Single(static table => string.Equals(table.Name, "Parent", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual("Parent", parent.Name);
        Assert.AreEqual("MySchema", parent.SchemaName);
        CollectionAssert.AreEquivalent(new[] { "Id", "Code", "State" }, parent.Columns.Select(static column => column.Name).ToArray());
        Assert.IsTrue(parent.Columns.All(static column => column.TableName == "Parent" && column.SchemaName == "MySchema"));

        Assert.AreEqual("ChildView", model.Views.Values.Single().Name);
        Assert.AreEqual("MySchema", model.Views.Values.Single().SchemaName);
        Assert.AreEqual("DoIt", model.Functions.Values.Single().Name);
        Assert.AreEqual("TicketSeq", model.Sequences.Values.Single().Name);
        Assert.AreEqual("Mood", model.Types.Values.Single().Name);
        Assert.AreEqual("MySchema", model.Types.Values.Single().SchemaName);

        var trigger = model.Triggers.Values.Single();
        Assert.AreEqual("TrgChild", trigger.Name);
        Assert.AreEqual("Child", trigger.TableName);
        CollectionAssert.AreEqual(new[] { "Amount", "ParentId" }, trigger.UpdateColumns.ToArray());
    }

    [TestMethod]
    public void ConstraintsAndIndexesKeepTheSpellingOfTheirNamesAndColumns()
    {
        var model = Build(Project);

        var byName = model.Constraints.Values.ToDictionary(static constraint => constraint.Name, StringComparer.OrdinalIgnoreCase);
        Assert.AreEqual("Child_Pk", byName["Child_Pk"].Name);
        CollectionAssert.AreEqual(new[] { "Id" }, byName["Child_Pk"].PrimaryKeyColumns.ToArray());
        CollectionAssert.AreEqual(new[] { "ParentId", "Amount" }, byName["Child_Uq"].UniqueColumns.ToArray(), "The columns keep their spelling and the order written (G-112).");
        Assert.AreEqual("Child", byName["Child_Pk"].TableName);

        var foreignKey = byName["Child_ParentId_fkey"];
        CollectionAssert.AreEqual(new[] { "ParentId" }, foreignKey.ForeignKeyColumns.ToArray());
        CollectionAssert.AreEqual(new[] { "Id" }, foreignKey.ForeignKeyReferencedColumns.ToArray());
        Assert.AreEqual("Parent", foreignKey.ForeignKeyReferencedTable);
        Assert.AreEqual("MySchema", foreignKey.ForeignKeyReferencedSchemaName);

        var index = model.Indexes.Values.Single();
        Assert.AreEqual("IxChild", index.Name);
        Assert.AreEqual("Child", index.TableName);
        CollectionAssert.AreEqual(new[] { "Id" }, index.IncludeColumns.ToArray());
        Assert.AreEqual("Amount", index.KeyElements[0].Name);
    }

    [TestMethod]
    public void KeysKeepTheNamesAsPostgreSqlHasThem()
    {
        var model = Build(Project);

        // PostgreSQL keeps a quoted name's case and compares names exactly (G-206): only the spelling it has finds the object.
        Assert.IsTrue(model.Tables.ContainsKey("MySchema.Parent"));
        Assert.IsFalse(model.Tables.ContainsKey("myschema.parent"));
        Assert.IsFalse(model.Tables.ContainsKey("MYSCHEMA.PARENT"));
        Assert.IsTrue(model.Types.ContainsKey("MySchema.Mood"));
        CollectionAssert.Contains(model.Constraints.Keys.ToArray(), "MySchema.Parent.Parent_pkey");
    }

    [TestMethod]
    public void ExpressionsThatNameQuotedColumnsAreNotReportedAsMissingColumns()
    {
        var model = Build(
            "CREATE TABLE t (\"Amount\" integer CHECK (\"Amount\" > 0), \"Order\" integer, \"Total\" integer DEFAULT 5);"
            + " CREATE INDEX ix ON t (\"Amount\", lower(\"Order\"::text)) WHERE \"Order\" > 0;"
            + " CREATE VIEW v AS SELECT \"Amount\" FROM t WHERE \"Order\" > 0;");

        Assert.IsFalse(HasErrors(model), Dump(model));
    }

    [TestMethod]
    public void ACheckOnAColumnThatDoesNotExistIsStillReported()
    {
        // schemas is populated so G-83's schema-scope leniency doesn't mask this genuine missing column.
        var model = Build(["public"], "CREATE TABLE t (\"Amount\" integer, CHECK (\"Nothing\" > 0));");

        Assert.IsTrue(HasErrors(model), "A quoted name that resolves to no column is still a missing column.");
    }

    [TestMethod]
    public void ATypeDeclaredWithQuotedNamesIsFoundByTheColumnsThatUseIt()
    {
        var model = Build("CREATE TYPE \"MySchema\".\"Mood\" AS ENUM ('a'); CREATE TABLE \"MySchema\".\"T\" (\"M\" \"MySchema\".\"Mood\", \"Ms\" \"MySchema\".\"Mood\"[]);");

        Assert.IsFalse(HasErrors(model), Dump(model));
        Assert.AreEqual("Mood", model.Types.Values.Single().Name);
        CollectionAssert.AreEqual(new[] { "a" }, model.Types.Values.Single().EnumLabels.ToArray());
    }

    [TestMethod]
    public void ADomainAndAnExtensionAreDeclaredWithTheirSchemas()
    {
        var model = Build("CREATE EXTENSION \"uuid-ossp\" SCHEMA \"MySchema\"; CREATE EXTENSION citext; CREATE EXTENSION plpgsql;");

        var extensions = model.Types.Values.Where(static type => type.TypeKind == "extension").ToDictionary(static type => type.Name);
        Assert.AreEqual("MySchema", extensions["uuid-ossp"].SchemaName);
        // No schema is stated, so PostgreSQL chooses it (G-213).
        Assert.IsNull(extensions["citext"].SchemaName);
        Assert.HasCount(2, extensions);
    }

    [TestMethod]
    public void NamesThatDifferOnlyByCaseAreTwoObjects()
    {
        var model = Build("CREATE TABLE \"T\" (a integer); CREATE TABLE t (a integer);");

        // PostgreSQL allows both (G-206): "T" keeps its case and t is folded to lower case, so they are different tables.
        Assert.IsFalse(HasErrors(model), Dump(model));
        CollectionAssert.AreEquivalent(new[] { "public.T", "public.t" }, model.Tables.Keys.ToArray());
    }

    [TestMethod]
    public void AColumnAddedByAlterTableKeepsItsSpelling()
    {
        var model = Build("CREATE TABLE \"T\" (\"Id\" integer); ALTER TABLE \"T\" ADD COLUMN \"Extra Col\" text;");

        CollectionAssert.AreEquivalent(new[] { "Id", "Extra Col" }, model.Tables.Values.Single().Columns.Select(static column => column.Name).ToArray());
    }

    [TestMethod]
    public void ADefaultOfAQualifiedEnumTypeIsTheSameAsPostgreSqlPrintsIt()
    {
        var written = Build("CREATE TYPE \"MySchema\".\"Mood\" AS ENUM ('ok'); CREATE TABLE t (m \"MySchema\".\"Mood\" DEFAULT 'ok');");
        var printed = Build("CREATE TYPE \"MySchema\".\"Mood\" AS ENUM ('ok'); CREATE TABLE t (m \"MySchema\".\"Mood\" DEFAULT 'ok'::\"MySchema\".\"Mood\");");
        var lowercase = Build("CREATE TYPE app.mood AS ENUM ('ok'); CREATE TABLE t (m app.mood DEFAULT 'ok'::app.mood);");
        var searchPath = Build("CREATE TYPE public.mood AS ENUM ('ok'); CREATE TABLE t (m public.mood DEFAULT 'ok'::mood);");

        Assert.AreEqual(Default(written), Default(printed));
        Assert.AreEqual("'ok'", Default(lowercase));
        Assert.AreEqual("'ok'", Default(searchPath));
    }

    [TestMethod]
    public void ACastToAnotherTypeIsNotFoldedAway()
    {
        var model = Build("CREATE TYPE \"MySchema\".\"Mood\" AS ENUM ('ok'); CREATE TYPE \"MySchema\".\"Other\" AS ENUM ('ok'); CREATE TABLE t (m \"MySchema\".\"Mood\" DEFAULT 'ok'::\"MySchema\".\"Other\");");

        StringAssert.Contains(Default(model), "\"Other\"");
    }

    [TestMethod]
    public void NamesThatOnlyDifferInCaseWithinASetAreTheSameSetWhateverTheirOrder()
    {
        var one = Build("CREATE TABLE t (a integer, \"B\" integer, c integer, UNIQUE (c, \"B\", a));");
        var other = Build("CREATE TABLE t (a integer, \"B\" integer, c integer, UNIQUE (a, c, \"B\"));");

        // The same set (the diff compares it as one), each kept in the order written for the publish (G-112).
        CollectionAssert.AreEquivalent(
            one.Constraints.Values.Single().UniqueColumns.ToArray(),
            other.Constraints.Values.Single().UniqueColumns.ToArray());
        CollectionAssert.AreEqual(new[] { "c", "B", "a" }, one.Constraints.Values.Single().UniqueColumns.ToArray());
    }

    [TestMethod]
    public void PlainLowerCaseProjectsAreUnchanged()
    {
        var model = Build("CREATE TABLE public.orders (id integer PRIMARY KEY, total integer CHECK (total > 0)); CREATE INDEX ix ON public.orders (total);");

        Assert.AreEqual("orders", model.Tables.Values.Single().Name);
        Assert.AreEqual("public.orders", model.Tables.Keys.Single());
        Assert.AreEqual("total > 0", model.Constraints.Values.Single(static constraint => constraint.ConstraintType == "check").RawCheckExpression);
        Assert.IsFalse(HasErrors(model), Dump(model));
    }

    private static string? Default(DatabaseModel model)
        => model.Tables.Values.Single().Columns.Single().GetComparableDefaultExpression();

    private static DatabaseModel Build(params string[] files)
        => Build(null, files);

    private static DatabaseModel Build(IReadOnlyList<string>? schemas, params string[] files)
    {
        var parser = new PgQueryParser();
        return new SemanticPipeline().Execute(files.Select(parser.Parse).ToArray(), schemas);
    }

    private static bool HasErrors(DatabaseModel model) => model.Diagnostics.Any(static diagnostic => diagnostic.Severity == SemanticDiagnosticSeverity.Error);

    private static string Dump(DatabaseModel model) => string.Join(Environment.NewLine, model.Diagnostics.Select(static diagnostic => $"{diagnostic.Severity}: {diagnostic.Message}"));
}
