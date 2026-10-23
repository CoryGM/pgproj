using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-249: an enum with no labels, <c>CREATE TYPE t AS ENUM ()</c>, is deployed as written. It was emitted as a TODO placeholder that <c>publish --execute</c> refuses.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class EmptyEnumTests
{
    [TestMethod]
    public void AnEnumWithNoLabelsIsDeployed()
    {
        var script = Script(DatabaseModel.Empty, Build("CREATE TYPE public.empty AS ENUM ();"));

        StringAssert.Contains(script, "CREATE TYPE \"public\".\"empty\" AS ENUM ();");
        Assert.IsFalse(script.Contains("TODO", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void LabelsAddedToAnEmptyEnumAreAddedInPlace()
    {
        var script = Script(Build("CREATE TYPE public.e AS ENUM ();"), Build("CREATE TYPE public.e AS ENUM ('a', 'b');"));

        StringAssert.Contains(script, "ADD VALUE");
        Assert.IsFalse(script.Contains("DROP TYPE", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void TheSameEmptyEnumOnBothSidesIsNoChange()
    {
        Assert.AreEqual(string.Empty, Script(Build("CREATE TYPE public.e AS ENUM ();"), Build("CREATE TYPE public.e AS ENUM ();")));
    }

    private static string Script(DatabaseModel oldModel, DatabaseModel newModel)
    {
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));
        Assert.IsEmpty(batch.GetUnsupportedOperations());
        return string.Join("\n", batch.Operations.Where(static operation => operation is not SchemaCreateOperation).Select(static operation => operation.GenerateSql().Replace("\r\n", "\n", StringComparison.Ordinal))).Trim();
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
