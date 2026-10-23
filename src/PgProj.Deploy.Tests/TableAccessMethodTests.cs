using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-223: a table's <c>USING</c> access method was dropped from the deployed statement. It is deployed as written, compared only when the project states one (the
/// default is the server's <c>default_table_access_method</c>), and changed in place with <c>SET ACCESS METHOD</c>.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class TableAccessMethodTests
{
    [TestMethod]
    public void TheAccessMethodIsReadAndCreatedAsWritten()
    {
        var model = Build("CREATE TABLE public.t (a int) USING heap2 WITH (fillfactor = 70);");
        Assert.AreEqual("heap2", model.Tables["public.t"].AccessMethod);

        var script = ScriptFor(DatabaseModel.Empty, model);
        StringAssert.Contains(script, "CREATE TABLE \"public\".\"t\" (\n    \"a\" int\n)\nUSING \"heap2\"\nWITH (fillfactor = 70);", script);
    }

    [TestMethod]
    public void AChangedAccessMethodIsSetInPlace()
    {
        var script = ScriptFor(Build("CREATE TABLE public.t (a int) USING heap;"), Build("CREATE TABLE public.t (a int) USING heap2;"));

        StringAssert.Contains(script, "ALTER TABLE \"public\".\"t\" SET ACCESS METHOD \"heap2\";", script);
        Assert.IsFalse(script.Contains("DROP TABLE", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void AnAccessMethodTheProjectDoesNotStateIsNotCompared()
        => Assert.IsEmpty(new DiffEngine().Diff(Build("CREATE TABLE public.t (a int) USING heap2;"), Build("CREATE TABLE public.t (a int);")).Modified);

    private static string ScriptFor(DatabaseModel oldModel, DatabaseModel newModel)
        => new ScriptGenerator().GenerateSqlScript(new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel)));

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
