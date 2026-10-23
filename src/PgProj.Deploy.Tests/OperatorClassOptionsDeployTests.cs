using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-239: an index key's operator class options keep their names. <c>tsvector_ops (siglen = 1)</c> was deployed as <c>tsvector_ops (1)</c>, which
/// PostgreSQL refuses.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class OperatorClassOptionsDeployTests
{
    private const string Table = "CREATE TABLE public.tv (a tsvector, b tsvector); ";

    [TestMethod]
    public void OptionsAreDeployedWithTheirNamesAsWritten()
    {
        var script = Script(DatabaseModel.Empty, Build(Table + "CREATE INDEX tv_i ON public.tv USING gist (a tsvector_ops (siglen = 1), b tsvector_ops (siglen=2));"));

        StringAssert.Contains(script, "tsvector_ops (siglen = 1)");
        StringAssert.Contains(script, "tsvector_ops (siglen=2)");
    }

    [TestMethod]
    public void SeveralOptionsKeepTheirOrderAndCommas()
    {
        var script = Script(DatabaseModel.Empty, Build(Table + "CREATE INDEX tv_i ON public.tv USING gist (a gist_trgm_ops (siglen = 32, other = 'x'));"));

        StringAssert.Contains(script, "(siglen = 32, other = 'x')");
    }

    [TestMethod]
    public void TheFormPostgreSqlPrintsIsTheSameIndex()
    {
        // pg_get_indexdef writes siglen='1' for siglen = 1.
        var written = Build(Table + "CREATE INDEX tv_i ON public.tv USING gist (a tsvector_ops (siglen = 1));");
        var printed = Build(Table + "CREATE INDEX tv_i ON public.tv USING gist (a tsvector_ops (siglen='1'));");

        Assert.AreEqual(string.Empty, Script(printed, written));
    }

    [TestMethod]
    public void AChangedOptionValueIsAChange()
    {
        var script = Script(
            Build(Table + "CREATE INDEX tv_i ON public.tv USING gist (a tsvector_ops (siglen = 1));"),
            Build(Table + "CREATE INDEX tv_i ON public.tv USING gist (a tsvector_ops (siglen = 2));"));

        StringAssert.Contains(script, "siglen = 2");
    }

    private static string Script(DatabaseModel oldModel, DatabaseModel newModel)
    {
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));
        return string.Join("\n", batch.Operations.Where(static operation => operation is not SchemaCreateOperation).Select(static operation => operation.GenerateSql().Replace("\r\n", "\n", StringComparison.Ordinal)));
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
