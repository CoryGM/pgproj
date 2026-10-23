namespace PgProj.Core.Tests;

/// <summary>
/// G-157: which schemas <c>init</c> scaffolds (<c>--schemas</c> names them, <c>--exclude-schemas</c> leaves some out, neither takes all), the objects it records as modeled, the
/// Title Case folders it writes, and the search for references from the scaffold into a schema it left out.
/// </summary>
[TestClass]
public sealed class ScaffoldSchemaChoiceTests
{
    private static readonly string[] All = ["app", "audit", "public", "staging"];

    [TestMethod]
    public void NeitherListTakesEverySchema()
    {
        CollectionAssert.AreEqual(All, LiveDatabaseModelLoader.ChooseSchemas(All, new ScaffoldOptions()).ToArray());
    }

    [TestMethod]
    public void SchemasTakesOnlyThoseNamedAndPublicIsNotAssumed()
    {
        var chosen = LiveDatabaseModelLoader.ChooseSchemas(All, new ScaffoldOptions(IncludeSchemas: ["staging", "APP"]));

        CollectionAssert.AreEqual(new[] { "app", "staging" }, chosen.ToArray());
    }

    [TestMethod]
    public void AQuotedNameKeepsItsCase()
    {
        // G-206: a name typed in --schemas is read as PostgreSQL reads one: "Sales" keeps its case, Sales folds to sales.
        var all = new[] { "Sales", "sales" };

        CollectionAssert.AreEqual(new[] { "Sales" }, LiveDatabaseModelLoader.ChooseSchemas(all, new ScaffoldOptions(IncludeSchemas: ["\"Sales\""])).ToArray());
        CollectionAssert.AreEqual(new[] { "sales" }, LiveDatabaseModelLoader.ChooseSchemas(all, new ScaffoldOptions(IncludeSchemas: ["Sales"])).ToArray());
    }

    [TestMethod]
    public void ExcludeSchemasTakesTheRestAndANameTheDatabaseLacksIsIgnored()
    {
        var chosen = LiveDatabaseModelLoader.ChooseSchemas(All, new ScaffoldOptions(ExcludeSchemas: ["audit", "staging", "nope"]));

        CollectionAssert.AreEqual(new[] { "app", "public" }, chosen.ToArray());
    }

    [TestMethod]
    public void ASchemaToIncludeThatTheDatabaseLacksIsAnErrorThatNamesIt()
    {
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => LiveDatabaseModelLoader.ChooseSchemas(All, new ScaffoldOptions(IncludeSchemas: ["app", "nope", "NOPE", "gone"])));

        StringAssert.Contains(exception.Message, "--schemas names schemas the database does not have: nope, gone.");
    }

    [TestMethod]
    public void ExcludingEverySchemaIsAnError()
    {
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => LiveDatabaseModelLoader.ChooseSchemas(All, new ScaffoldOptions(ExcludeSchemas: All)));

        StringAssert.Contains(exception.Message, "leaves no schema to scaffold");
    }

    [TestMethod]
    public void OnlyOneOfTheListsMayBeGiven()
    {
        Assert.IsFalse(new ScaffoldOptions().IsScoped);
        Assert.IsTrue(new ScaffoldOptions(IncludeSchemas: ["app"]).IsScoped);
        Assert.IsTrue(new ScaffoldOptions(ExcludeSchemas: ["audit"]).IsScoped);
    }

    [TestMethod]
    public void AReferenceToAnExcludedSchemaIsFoundInTheFilesThatMakeIt()
    {
        ScaffoldedFile[] files =
        [
            new("Schema/app/Table/orders.sql", "ALTER TABLE app.orders ADD CONSTRAINT fk FOREIGN KEY (l) REFERENCES audit.log_entry (id);\nCREATE VIEW v AS SELECT * FROM \"audit\".\"log_entry\" JOIN audit.x USING (id);"),
            new("Schema/app/Table/customers.sql", "CREATE TABLE app.customers (note text); -- audited.note is not a reference\nSELECT 'unaudit.x';"),
        ];

        var references = LiveDatabaseModelLoader.FindReferencesToExcludedSchemas(files, ["audit", "staging"]);

        Assert.AreEqual(1, references.Count);
        Assert.AreEqual("Schema/app/Table/orders.sql", references[0].RelativePath);
        Assert.AreEqual("audit", references[0].ExcludedSchema);
        Assert.AreEqual(3, references[0].Count);
    }

    [TestMethod]
    public void TheScaffoldRecordsWhatItModeledAndWhereItWroteIt()
    {
        var modeled = new List<ScaffoldedObject>();
        LiveTableSnapshot table = new("app", "t", [new LiveColumnSnapshot("a", "integer", true, null, false, null, null)]);
        LiveViewSnapshot view = new("app", "v", "SELECT a FROM app.t", false);
        LiveIndexSnapshot index = new("app", "t", "t_a", "CREATE INDEX t_a ON app.t USING btree (a)");

        var files = LiveDatabaseModelLoader.BuildScaffoldFiles([new LiveSchemaSnapshot("app")], [table], [], [index], [view], [], [], [], [], [], [], modeled: modeled);

        var byType = modeled.ToDictionary(static item => item.ObjectType, StringComparer.Ordinal);
        Assert.AreEqual("Schema/app/Table/t.sql", byType["Table"].RelativePath);
        Assert.AreEqual("Schema/app/Table/t.sql", byType["Index"].RelativePath);
        Assert.AreEqual("Schema/app/View/v.sql", byType["View"].RelativePath);
        Assert.AreEqual("app", byType["View"].SchemaName);
        Assert.AreEqual("t_a", byType["Index"].Name);
        Assert.IsTrue(modeled.All(item => files.Any(file => file.RelativePath == item.RelativePath)), "Every recorded path is a file that was written.");
    }

    [TestMethod]
    public void EveryFolderTheScaffoldWritesIsTitleCased()
    {
        LiveTableSnapshot table = new("app", "t", [new LiveColumnSnapshot("a", "integer", true, null, false, null, null)]);
        var files = LiveDatabaseModelLoader.BuildScaffoldFiles(
            [new LiveSchemaSnapshot("app")],
            [table],
            [],
            [],
            [new LiveViewSnapshot("app", "v", "SELECT 1", false)],
            [],
            [],
            [],
            [new LiveSequenceSnapshot("app", "s", "bigint", "1", "1", "9", "1", false, "1", null, null, null)],
            [],
            [],
            collations: [new LiveCollationSnapshot("app", "ci", "icu", null, null, "und", true, null)],
            textSearch: new LiveTextSearchSnapshot(
                [new LiveTextSearchDictionarySnapshot("app", "d", "pg_catalog", "simple", string.Empty)],
                [new LiveTextSearchConfigurationSnapshot("app", "c", "pg_catalog", "default", [])]));

        foreach (var file in files)
        {
            foreach (var folder in file.RelativePath.Split('/')[..^1])
            {
                Assert.IsTrue(folder.Length > 0 && char.IsUpper(folder[0]) && folder != folder.ToUpperInvariant() || folder == "app", $"Folder '{folder}' of {file.RelativePath} is not Title Case.");
            }
        }

        var paths = files.Select(static file => file.RelativePath).ToList();
        CollectionAssert.IsSubsetOf(
            new[] { "Schema/app/Table/t.sql", "Schema/app/View/v.sql", "Schema/app/Sequence/s.sql", "Schema/app/Collation/ci.sql", "Schema/app/TextSearchDictionary/d.sql", "Schema/app/TextSearchConfiguration/c.sql" },
            paths);
    }
}
