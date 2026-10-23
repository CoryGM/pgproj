using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-238: a clause whose expression is a subquery keeps the subquery's own parentheses. They were stripped as if they were the clause's, so
/// <c>WITH CHECK ((SELECT ...))</c> was deployed as <c>WITH CHECK (SELECT ...)</c>, which PostgreSQL refuses.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SubqueryClauseDeployTests
{
    private const string Tables = "CREATE TABLE public.m (c1 integer); CREATE TABLE public.t (c integer, b integer); ";

    [TestMethod]
    public void APolicyWithCheckSubqueryKeepsItsParentheses()
    {
        var script = Script(Tables + "CREATE POLICY p ON public.t FOR UPDATE USING (true) WITH CHECK ((SELECT t.c <= m.c1 FROM public.m));");

        StringAssert.Contains(script, "WITH CHECK ((SELECT t.c <= m.c1 FROM public.m))");
        StringAssert.Contains(script, "USING (true)");
    }

    [TestMethod]
    public void APolicyWithUsingSubqueryKeepsItsParentheses()
    {
        var script = Script(Tables + "CREATE POLICY p ON public.t USING (c IN (SELECT c1 FROM public.m)) ;");

        StringAssert.Contains(script, "USING (c IN (SELECT c1 FROM public.m))");
    }

    [TestMethod]
    [DataRow("(SELECT max(c1) FROM public.m) > c")]
    [DataRow("(c > 0) AND (b > 0)")]
    [DataRow("c > 0")]
    public void OtherPolicyExpressionsAreDeployedAsBefore(string condition)
    {
        var script = Script(Tables + $"CREATE POLICY p ON public.t USING ({condition});");

        StringAssert.Contains(script, $"USING ({condition})");
    }

    [TestMethod]
    public void AnExtraPairOfParenthesesAroundAnOrdinaryExpressionIsStillRemoved()
    {
        var script = Script(Tables + "CREATE POLICY p ON public.t USING ((c > 0));");

        StringAssert.Contains(script, "USING (c > 0)");
    }

    private static string Script(string sql)
    {
        var oldModel = DatabaseModel.Empty;
        var newModel = new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));
        return string.Join("\n", batch.Operations.Select(static operation => operation.GenerateSql().Replace("\r\n", "\n", StringComparison.Ordinal)));
    }
}
