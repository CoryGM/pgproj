using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-134: a publication is created after the tables it names and dropped before them; it is written with its column lists, row filters, schemas, and options; a change is made
/// in place with <c>ALTER PUBLICATION</c> (a drop and a create only when <c>FOR ALL TABLES</c> changes); and one that only disappears is a destructive change.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class PublicationDeployTests
{
    private const string Tables = "CREATE TABLE public.t (id int PRIMARY KEY, n int); CREATE TABLE public.u (id int PRIMARY KEY); CREATE SCHEMA app; ";

    private static string Publication(string body) => "CREATE PUBLICATION p " + body + ";";

    [TestMethod]
    public void APublicationIsCreatedAfterTheTablesItNames()
    {
        var statements = Statements(DatabaseModel.Empty, Build(Publication("FOR TABLE public.t, public.u") + Tables));

        var publicationAt = statements.FindIndex(static s => s.StartsWith("CREATE PUBLICATION", StringComparison.Ordinal));
        var tableAt = statements.FindIndex(static s => s.StartsWith("CREATE TABLE \"public\".\"u\"", StringComparison.Ordinal));
        Assert.IsTrue(tableAt >= 0 && publicationAt > tableAt, string.Join("\n---\n", statements));
    }

    [TestMethod]
    public void EverythingAPublicationCanNameIsWritten()
    {
        var statements = Statements(DatabaseModel.Empty, Build(Tables + Publication("FOR TABLE public.t (id, n) WHERE (n > 0), public.u WITH (publish = 'insert, update', publish_via_partition_root = true)")));

        var sql = statements.Single(static s => s.StartsWith("CREATE PUBLICATION", StringComparison.Ordinal));
        StringAssert.Contains(sql, "CREATE PUBLICATION \"p\"");
        StringAssert.Contains(sql, "FOR TABLE \"public\".\"t\" (\"id\", \"n\") WHERE (n > 0), \"public\".\"u\"");
        StringAssert.Contains(sql, "WITH (publish = 'insert, update', publish_via_partition_root = true)");
    }

    [TestMethod]
    public void ASchemaListAllTablesAndAnEmptyPublicationAreWritten()
    {
        var statements = Statements(DatabaseModel.Empty, Build(Tables + "CREATE PUBLICATION a FOR TABLES IN SCHEMA app; CREATE PUBLICATION b FOR ALL TABLES; CREATE PUBLICATION c;"));

        var script = string.Join("\n", statements);
        StringAssert.Contains(script, "CREATE PUBLICATION \"a\"\n    FOR TABLES IN SCHEMA \"app\";");
        StringAssert.Contains(script, "CREATE PUBLICATION \"b\"\n    FOR ALL TABLES;");
        StringAssert.Contains(script, "CREATE PUBLICATION \"c\";");
    }

    [TestMethod]
    public void AChangedOptionAndChangedTablesAreAlteredInPlace()
    {
        var batch = Generate(
            Build(Tables + Publication("FOR TABLE public.t, public.u WITH (publish = 'insert, update')")),
            Build(Tables + Publication("FOR TABLE public.t (id) WHERE (id > 0), TABLES IN SCHEMA app WITH (publish = 'insert', publish_via_partition_root = true)")));

        var script = string.Join("\n", Sql(batch));
        StringAssert.Contains(script, "ALTER PUBLICATION \"p\" SET (publish = 'insert', publish_via_partition_root = true);");
        StringAssert.Contains(script, "ALTER PUBLICATION \"p\" SET TABLE \"public\".\"t\" (\"id\") WHERE (id > 0), TABLES IN SCHEMA \"app\";");
        Assert.IsFalse(script.Contains("DROP PUBLICATION", StringComparison.Ordinal));
        Assert.IsEmpty(batch.GetDestructiveOperations());
    }

    [TestMethod]
    public void AnOptionThatWentAwayIsBackAtItsDefault()
    {
        var script = string.Join("\n", Sql(Generate(
            Build(Tables + Publication("FOR TABLE public.t WITH (publish = 'insert', publish_via_partition_root = true)")),
            Build(Tables + Publication("FOR TABLE public.t")))));

        StringAssert.Contains(script, "ALTER PUBLICATION \"p\" SET (publish = 'insert, update, delete, truncate', publish_via_partition_root = false);");
    }

    [TestMethod]
    public void APublicationThatNamesNothingAnyMoreHasItsTablesAndSchemasDropped()
    {
        var script = string.Join("\n", Sql(Generate(
            Build(Tables + Publication("FOR TABLE public.t, public.u")),
            Build(Tables + "CREATE PUBLICATION p;"))));

        StringAssert.Contains(script, "ALTER PUBLICATION \"p\" DROP TABLE \"public\".\"t\", \"public\".\"u\";");
    }

    [TestMethod]
    public void ForAllTablesChangingIsADropAndACreateAndIsNotDestructive()
    {
        var batch = Generate(Build(Tables + Publication("FOR TABLE public.t")), Build(Tables + Publication("FOR ALL TABLES")));

        var script = string.Join("\n", Sql(batch));
        StringAssert.Contains(script, "DROP PUBLICATION \"p\";");
        StringAssert.Contains(script, "CREATE PUBLICATION \"p\"\n    FOR ALL TABLES;");
        Assert.IsEmpty(batch.GetDestructiveOperations());
    }

    [TestMethod]
    public void ARemovedPublicationIsDroppedBeforeItsTablesAndIsDestructive()
    {
        var batch = Generate(Build(Tables + Publication("FOR TABLE public.t")), Build("CREATE TABLE public.keep (a integer);"));

        var statements = Sql(batch);
        var publicationAt = statements.FindIndex(static s => s.StartsWith("DROP PUBLICATION", StringComparison.Ordinal));
        var tableAt = statements.FindIndex(static s => s.StartsWith("DROP TABLE \"public\".\"t\"", StringComparison.Ordinal));
        Assert.IsTrue(publicationAt >= 0 && tableAt > publicationAt, string.Join("\n---\n", statements));
        Assert.IsTrue(batch.GetDestructiveOperations().Any(static d => d.Reasons.Any(static reason => reason.StartsWith("publication dropped", StringComparison.Ordinal))));
    }

    [TestMethod]
    public void TheSamePublicationWrittenTwoWaysDeploysNothing()
    {
        Assert.IsEmpty(Statements(
            Build(Tables + Publication("FOR TABLE public.t (id, n) WHERE (n > 0)")),
            Build(Tables + Publication("FOR TABLE t (n, id) WHERE ((n) > 0) WITH (publish = 'delete, update, insert, truncate')"))));
    }

    private static OperationBatch Generate(DatabaseModel oldModel, DatabaseModel newModel)
        => new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));

    private static List<string> Sql(OperationBatch batch)
        => batch.Operations
            .Where(static operation => operation is not SchemaCreateOperation)
            .Select(static operation => operation.GenerateSql().Trim().Replace("\r\n", "\n", StringComparison.Ordinal))
            .ToList();

    private static List<string> Statements(DatabaseModel oldModel, DatabaseModel newModel) => Sql(Generate(oldModel, newModel));

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
