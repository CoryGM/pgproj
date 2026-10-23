using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-243: a function or procedure whose body is blank (<c>AS $p$ $p$</c>, a stub or a placeholder) is deployed as written. It was refused as having "no original body".
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class BlankRoutineBodyTests
{
    [TestMethod]
    public void ABlankProcedureBodyIsDeployed()
    {
        var script = Script("CREATE PROCEDURE public.nothing(IN integer) LANGUAGE sql AS $procedure$ $procedure$;");

        StringAssert.Contains(script, "CREATE OR REPLACE PROCEDURE \"public\".\"nothing\"(integer)");
        StringAssert.Contains(script, "AS $procedure$ $procedure$");
        Assert.IsFalse(script.Contains("TODO", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void ABlankFunctionBodyIsDeployed()
    {
        var script = Script("CREATE FUNCTION public.stub() RETURNS void LANGUAGE sql AS $$ $$;");

        Assert.IsFalse(script.Contains("TODO", StringComparison.Ordinal), script);
        StringAssert.Contains(script, "AS $$ $$");
    }

    private static string Script(string sql)
    {
        var model = new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(DatabaseModel.Empty, model), new DependencyAnalyzer().Analyze(DatabaseModel.Empty, model));
        Assert.IsEmpty(batch.GetUnsupportedOperations());
        return string.Join("\n", batch.Operations.Where(static operation => operation is not SchemaCreateOperation).Select(static operation => operation.GenerateSql().Replace("\r\n", "\n", StringComparison.Ordinal))).Trim();
    }
}
