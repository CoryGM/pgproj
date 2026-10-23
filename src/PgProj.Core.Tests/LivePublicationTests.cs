using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core.Tests;

/// <summary>
/// G-134: a live database's publications are written to the snapshot and to <c>init</c>'s scaffold as <c>CREATE PUBLICATION</c>, in a file of their own at the top, and read
/// back as publications that compare with a project's: the operations it publishes, its tables with their column lists and row filters, and its schemas.
/// </summary>
[TestClass]
public sealed class LivePublicationTests
{
    private static readonly LiveTableSnapshot Table = new("public", "t", [new LiveColumnSnapshot("id", "integer", false, null, false, null, null), new LiveColumnSnapshot("n", "integer", true, null, false, null, null)]);

    private static LivePublicationSnapshot Publication(string name = "p") => new(name, false, true, true, true, true, false, "n", [], []);

    [TestMethod]
    public void AnEmptyPublicationAndForAllTablesAreWrittenWithoutOptions()
    {
        Assert.AreEqual("CREATE PUBLICATION p;", LiveDatabaseModelLoader.BuildCreatePublicationStatement(Publication()));
        Assert.AreEqual("CREATE PUBLICATION p FOR ALL TABLES;", LiveDatabaseModelLoader.BuildCreatePublicationStatement(Publication() with { AllTables = true }));
    }

    [TestMethod]
    public void TablesColumnListsRowFiltersSchemasAndOptionsAreWritten()
    {
        var sql = LiveDatabaseModelLoader.BuildCreatePublicationStatement(Publication() with
        {
            Insert = true,
            Update = true,
            Delete = false,
            Truncate = false,
            ViaRoot = true,
            GeneratedColumns = "s",
            Tables = [new LivePublicationTable("public", "t", ["id", "n"], "(n > 0)"), new LivePublicationTable("public", "u", [], null)],
            Schemas = ["app"],
        });

        Assert.AreEqual(
            "CREATE PUBLICATION p FOR TABLE public.t (id, n) WHERE ((n > 0)), public.u, TABLES IN SCHEMA app WITH (publish = 'insert, update', publish_via_partition_root = true, publish_generated_columns = stored);",
            sql);
    }

    [TestMethod]
    public void APublicationIsReadBackAndComparesWithTheProjectsOwn()
    {
        var live = Load(Publication() with { Tables = [new LivePublicationTable("public", "t", ["id", "n"], "(n > 0)")], Insert = true, Update = false, Delete = false, Truncate = false });
        var project = new SemanticPipeline().Execute([new PgQueryParser().Parse(
            "CREATE TABLE public.t (id integer NOT NULL, n integer); CREATE PUBLICATION p FOR TABLE public.t (n, id) WHERE (n > 0) WITH (publish = 'insert');")]);

        var publication = live.Publications.Values.Single();
        var projectPublication = project.Publications.Values.Single();
        Assert.AreEqual(projectPublication.Key, publication.Key);
        CollectionAssert.AreEqual(projectPublication.NormalizedOptions.ToArray(), publication.NormalizedOptions.ToArray());
        Assert.AreEqual(projectPublication.Tables.Single().NormalizedFilter, publication.Tables.Single().NormalizedFilter, "PostgreSQL's parentheses are not a difference.");
        CollectionAssert.AreEqual(projectPublication.Tables.Single().NormalizedColumns.ToArray(), publication.Tables.Single().NormalizedColumns.ToArray());
        Assert.AreEqual(0, live.Diagnostics.Count, string.Join("; ", live.Diagnostics.Select(static d => d.Message)));
    }

    [TestMethod]
    public void TheScaffoldPutsEachPublicationInAFileOfItsOwnAtTheTop()
    {
        var files = LiveDatabaseModelLoader.BuildScaffoldFiles(
            [new LiveSchemaSnapshot("public")], [Table], [], [], [], [], [], [], [], [], [],
            publications: [Publication("p"), Publication("q") with { AllTables = true }]);

        var file = files.Single(static f => f.RelativePath == "Publication/q.sql");
        StringAssert.Contains(file.Content, "CREATE PUBLICATION q FOR ALL TABLES;");
        Assert.IsTrue(files.Any(static f => f.RelativePath == "Publication/p.sql"));
        Assert.IsEmpty(new LiveDatabaseModelLoader(new PgQueryParser(), new SemanticBuilder()).FindUnparseableFiles([file]));
    }

    [TestMethod]
    public void ADatabaseWithoutPublicationsScaffoldsNoPublicationFile()
    {
        var files = LiveDatabaseModelLoader.BuildScaffoldFiles([new LiveSchemaSnapshot("public")], [Table], [], [], [], [], [], [], [], [], []);

        Assert.IsFalse(files.Any(static f => f.RelativePath.StartsWith("Publication", StringComparison.Ordinal)));
    }

    private static DatabaseModel Load(LivePublicationSnapshot publication)
    {
        var sql = LiveDatabaseModelLoader.GenerateSnapshotSql([new LiveSchemaSnapshot("public")], [Table], [], [], publications: [publication]);
        return new LiveDatabaseModelLoader(new PgQueryParser(), new SemanticBuilder()).BuildModelFromSnapshotSql(sql);
    }
}
