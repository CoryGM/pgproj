using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-258: <c>ALTER PUBLICATION ... ADD TABLE | DROP TABLE | SET TABLE | SET (...)</c> is folded into the publication it names, as <c>pg_dump</c> writes a publication (an empty
/// <c>CREATE PUBLICATION</c> and one <c>ADD TABLE</c> per table). It used to be reported as not modeled, so the publication lost its tables.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class AlterPublicationTests
{
    private const string Tables = "CREATE TABLE public.t (a integer, b integer); CREATE TABLE public.u (id integer);";

    [TestMethod]
    public void AddTableGivesThePublicationItsTablesWithColumnListAndFilter()
    {
        var script = Script(Build(Tables + """
            CREATE PUBLICATION pub WITH (publish = 'insert');
            ALTER PUBLICATION pub ADD TABLE ONLY public.t (a) WHERE ((a > 0));
            ALTER PUBLICATION pub ADD TABLE ONLY public.u;
            """));

        StringAssert.Contains(script, "FOR TABLE \"public\".\"t\" (\"a\") WHERE (a > 0), \"public\".\"u\"");
        StringAssert.Contains(script, "publish = 'insert'");
    }

    [TestMethod]
    public void AddTablesInSchemaGivesThePublicationItsSchemas()
    {
        var script = Script(Build("CREATE SCHEMA s; CREATE PUBLICATION pub; ALTER PUBLICATION pub ADD TABLES IN SCHEMA s;"));

        StringAssert.Contains(script, "TABLES IN SCHEMA \"s\"");
    }

    [TestMethod]
    public void DropTableTakesATableOut()
    {
        var script = Script(Build(Tables + """
            CREATE PUBLICATION pub FOR TABLE public.t, public.u;
            ALTER PUBLICATION pub DROP TABLE public.u;
            """));

        StringAssert.Contains(script, "FOR TABLE \"public\".\"t\"");
        Assert.IsFalse(script[script.IndexOf("CREATE PUBLICATION", StringComparison.Ordinal)..].Contains("\"u\"", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void SetTableReplacesTheTablesAndSetOptionsChangesTheOptions()
    {
        var script = Script(Build(Tables + """
            CREATE PUBLICATION pub FOR TABLE public.t WITH (publish = 'insert');
            ALTER PUBLICATION pub SET TABLE public.u;
            ALTER PUBLICATION pub SET (publish = 'delete');
            """));

        StringAssert.Contains(script, "FOR TABLE \"public\".\"u\"");
        Assert.IsFalse(script[script.IndexOf("CREATE PUBLICATION", StringComparison.Ordinal)..].Contains("\"t\"", StringComparison.Ordinal), script);
        StringAssert.Contains(script, "publish = 'delete'");
    }

    [TestMethod]
    public void TheStatementAppliesWhateverFileItIsIn()
    {
        var model = new SemanticPipeline().Execute(
        [
            new PgQueryParser().Parse("ALTER PUBLICATION pub ADD TABLE public.t;"),
            new PgQueryParser().Parse(Tables + " CREATE PUBLICATION pub;"),
        ]);

        StringAssert.Contains(Script(model), "FOR TABLE \"public\".\"t\"");
        Assert.IsFalse(model.Diagnostics.Any(static d => d.Message.Contains("ignored", StringComparison.Ordinal)), string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
    }

    [TestMethod]
    public void AnUnqualifiedTableIsGivenTheDefaultSchema()
    {
        var script = Script(Build(Tables + "CREATE PUBLICATION pub; ALTER PUBLICATION pub ADD TABLE t;"));

        StringAssert.Contains(script, "FOR TABLE \"public\".\"t\"");
    }

    [TestMethod]
    public void APublicationTheProjectDoesNotDeclareIsAWarning()
    {
        var model = Build("ALTER PUBLICATION nope ADD TABLE public.t;");

        Assert.IsTrue(model.Diagnostics.Any(static d => d.Message.Contains("does not declare that publication", StringComparison.Ordinal)), string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
    }

    [TestMethod]
    public void AlterPublicationIsNoLongerReportedAsNotModeled()
    {
        var model = Build(Tables + "CREATE PUBLICATION pub; ALTER PUBLICATION pub ADD TABLE public.t;");

        Assert.IsFalse(model.Diagnostics.Any(static d => d.Message.Contains("not modeled", StringComparison.Ordinal)), string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
    }

    private static string Script(DatabaseModel newModel)
    {
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(DatabaseModel.Empty, newModel), new DependencyAnalyzer().Analyze(DatabaseModel.Empty, newModel));
        return string.Join("\n", batch.Operations.Where(static operation => operation is not SchemaCreateOperation).Select(static operation => operation.GenerateSql().Replace("\r\n", "\n", StringComparison.Ordinal))).Trim();
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
