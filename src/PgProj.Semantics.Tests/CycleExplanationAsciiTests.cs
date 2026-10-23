using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-285: a dependency cycle was explained with <c>→</c>, which a Windows console turns into byte <c>0x1A</c> when the output is redirected to a file or a CI log.
/// The explanation uses <c>-&gt;</c>.
/// </summary>
[TestClass]
public sealed class CycleExplanationAsciiTests
{
    [TestMethod]
    public void ACycleIsExplainedWithAnAsciiArrow()
    {
        var model = new SemanticPipeline().Execute([new PgQueryParser().Parse("""
            CREATE FUNCTION public.a(n int) RETURNS int LANGUAGE sql AS $$ SELECT CASE WHEN n > 0 THEN public.b(n - 1) ELSE 0 END $$;
            CREATE FUNCTION public.b(n int) RETURNS int LANGUAGE sql AS $$ SELECT CASE WHEN n > 0 THEN public.a(n - 1) ELSE 1 END $$;
            """)]);

        var explanation = model.Diagnostics.Single(static diagnostic => diagnostic.Message.StartsWith("Dependency cycle explanation:", StringComparison.Ordinal)).Message;
        StringAssert.Contains(explanation, " -> ");
        Assert.IsTrue(explanation.All(static character => character < 128), explanation);
    }
}
