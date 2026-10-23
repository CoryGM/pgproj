using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-272: the semantic passes walk expression trees recursively. A view, check, or index expression a few hundred levels deep overflowed a
/// 1 MB stack (the main thread's on Windows) and ended the process; the pipeline now runs on a large stack.
/// </summary>
[TestClass]
public sealed class DeepStatementSemanticsTests
{
    private static string Sum(int terms) => string.Join(" + ", Enumerable.Repeat("a", terms));

    [TestMethod]
    public void DeepExpressionsAreModeledFromAThreadWithADefaultStack()
    {
        var sql = $"""
            CREATE TABLE public.t (a int, CONSTRAINT c CHECK (({Sum(300)}) > 0));
            CREATE INDEX i ON public.t (({Sum(300)}));
            CREATE VIEW public.v AS SELECT {Sum(300)} AS x FROM public.t;
            CREATE VIEW public.w AS {string.Concat(Enumerable.Repeat("SELECT * FROM (", 150))}SELECT a FROM public.t{string.Concat(Enumerable.Repeat(") s", 150))};
            """;
        DatabaseModel? model = null;
        var thread = new Thread(() => model = new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]), 1024 * 1024);
        thread.Start();
        thread.Join();

        Assert.IsNotNull(model);
        Assert.IsFalse(model.HasErrors, string.Join(Environment.NewLine, model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.AreEqual(2, model.Views.Count);
        Assert.IsTrue(model.Views.ContainsKey("public.v"));
        Assert.IsTrue(model.Constraints.Values.Any(static constraint => constraint.Name == "c"));
        Assert.IsTrue(model.Indexes.Values.Any(static index => index.Name == "i"));
    }

    [TestMethod]
    public void ADeepViewDiffsAgainstItselfAsNoChange()
    {
        var sql = $"CREATE TABLE public.t (a int);\nCREATE VIEW public.v AS SELECT {Sum(300)} AS x FROM public.t;";

        var first = new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
        var second = new SemanticPipeline().Execute([new PgQueryParser().Parse(sql.Replace(" + ", "+", StringComparison.Ordinal))]);

        Assert.IsFalse(first.HasErrors);
        CollectionAssert.AreEqual(first.Views["public.v"].NormalizedExpressions.ToArray(), second.Views["public.v"].NormalizedExpressions.ToArray());
        Assert.IsNotEmpty(first.Views["public.v"].NormalizedExpressions);
    }
}
