using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-211: a routine in any language is deployed, its body as written. A C function keeps its link symbol, <c>AS 'library', 'symbol'</c>: it was
/// written without it, so PostgreSQL looked for the function's own name in the library.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RoutineLanguageDeployTests
{
    [TestMethod]
    public void ACFunctionIsDeployedWithItsLinkSymbol()
    {
        StringAssert.Contains(Script("CREATE FUNCTION public.h() RETURNS language_handler LANGUAGE c AS '$libdir/plpgsql', 'plpgsql_call_handler';"),
            "LANGUAGE c\nAS $$$libdir/plpgsql$$, 'plpgsql_call_handler';");
    }

    [TestMethod]
    public void APlPerlFunctionIsDeployedWithItsBodyAsWritten()
    {
        StringAssert.Contains(Script("CREATE FUNCTION public.add(a int, b int) RETURNS int LANGUAGE plperl AS $p$ return $_[0] + $_[1]; $p$;"),
            "LANGUAGE plperl\nAS $p$ return $_[0] + $_[1]; $p$;");
    }

    private static string Script(string sql)
    {
        var model = new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(DatabaseModel.Empty, model), new DependencyAnalyzer().Analyze(DatabaseModel.Empty, model));
        return string.Join("\n", batch.Operations.Select(static operation => operation.GenerateSql()));
    }
}
