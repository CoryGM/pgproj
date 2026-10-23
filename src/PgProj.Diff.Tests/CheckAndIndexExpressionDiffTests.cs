using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-73: a CHECK, an index predicate, and an index key expression are compared by their canonical form, so a rewritten but equivalent
/// condition (or the way PostgreSQL prints it back) reports nothing, and a different condition is reported with both texts as written.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class CheckAndIndexExpressionDiffTests
{
    private const string Table = "CREATE TABLE public.t (a integer, b integer, n text, v varchar(20), CONSTRAINT c CHECK ({0}));";
    private const string IndexTable = "CREATE TABLE public.t (a integer, b integer, n text); {0};";

    [TestMethod]
    public void ACheckDiffedWithItselfIsEmpty()
    {
        var model = Build(string.Format(Table, "a BETWEEN 1 AND 9 AND (b IN (1, 2) OR n LIKE 'x%')"));

        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(model, model)));
    }

    [TestMethod]
    [DataRow("a > 0 AND a < 9", "((a > 0) AND (a < 9))")]
    [DataRow("a BETWEEN 1 AND 9", "((a >= 1) AND (a <= 9))")]
    [DataRow("a IN (1, 2)", "(a = ANY (ARRAY[1, 2]))")]
    [DataRow("n LIKE 'a%'", "(n ~~ 'a%'::text)")]
    [DataRow("v = 'x'", "((v)::text = 'x'::text)")]
    [DataRow("a   >   0", "a > 0")]
    public void ACheckWrittenTwoWaysReportsNothing(string written, string printed)
    {
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(Build(string.Format(Table, printed)), Build(string.Format(Table, written)))), written);
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(Build(string.Format(Table, written)), Build(string.Format(Table, printed)))), written);
    }

    [TestMethod]
    [DataRow("a > 0 AND a < 9", "a > 0 OR a < 9")]
    [DataRow("a BETWEEN 1 AND 9", "a BETWEEN 1 AND 10")]
    [DataRow("a IN (1, 2)", "a IN (1, 3)")]
    [DataRow("v = 'x'", "v = 'y'")]
    [DataRow("a > 0", "a >= 0")]
    public void AChangedCheckIsReportedWithBothTextsAsWritten(string before, string after)
    {
        var diff = new DiffEngine().Diff(Build(string.Format(Table, before)), Build(string.Format(Table, after)));

        var detail = diff.Modified.Single(static change => change.NodeId.EndsWith(".c", StringComparison.OrdinalIgnoreCase)).Details.Single();
        Assert.AreEqual(nameof(ConstraintDefinition.CheckExpressionNode), detail.PropertyName);
        StringAssert.Contains(detail.OldValue?.ToString(), before);
        StringAssert.Contains(detail.NewValue?.ToString(), after);
    }

    [TestMethod]
    [DataRow("CREATE INDEX i ON public.t (a) WHERE a BETWEEN 1 AND 9", "CREATE INDEX i ON public.t (a) WHERE ((a >= 1) AND (a <= 9))")]
    [DataRow("CREATE INDEX i ON public.t (a) WHERE a IN (1, 2)", "CREATE INDEX i ON public.t (a) WHERE (a = ANY (ARRAY[1, 2]))")]
    [DataRow("CREATE INDEX i ON public.t ((a + b))", "CREATE INDEX i ON public.t (((a + b)))")]
    [DataRow("CREATE INDEX i ON public.t (lower(n))", "CREATE INDEX i ON public.t (lower(n))")]
    [DataRow("CREATE INDEX i ON public.t ((a BETWEEN 1 AND 2))", "CREATE INDEX i ON public.t ((((a >= 1) AND (a <= 2))))")]
    public void AnIndexWrittenTwoWaysReportsNothing(string written, string printed)
    {
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(Build(string.Format(IndexTable, printed)), Build(string.Format(IndexTable, written)))), written);
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(Build(string.Format(IndexTable, written)), Build(string.Format(IndexTable, written)))), written);
    }

    [TestMethod]
    [DataRow("CREATE INDEX i ON public.t (a) WHERE a BETWEEN 1 AND 9", "CREATE INDEX i ON public.t (a) WHERE a BETWEEN 1 AND 10", "PredicateExpressionNode")]
    [DataRow("CREATE INDEX i ON public.t (a) WHERE a IN (1, 2)", "CREATE INDEX i ON public.t (a) WHERE a IN (1, 2, 3)", "PredicateExpressionNode")]
    [DataRow("CREATE INDEX i ON public.t (a)", "CREATE INDEX i ON public.t (a) WHERE a > 0", "PredicateExpressionNode")]
    [DataRow("CREATE INDEX i ON public.t ((a + b))", "CREATE INDEX i ON public.t ((a - b))", "KeyElements")]
    [DataRow("CREATE INDEX i ON public.t (lower(n))", "CREATE INDEX i ON public.t (upper(n))", "KeyElements")]
    [DataRow("CREATE INDEX i ON public.t ((a + b))", "CREATE INDEX i ON public.t ((b + a))", "KeyElements")]
    public void AChangedIndexPredicateOrKeyExpressionIsReported(string before, string after, string property)
    {
        var diff = new DiffEngine().Diff(Build(string.Format(IndexTable, before)), Build(string.Format(IndexTable, after)));

        var change = diff.Modified.Single(static candidate => candidate.NodeId.EndsWith(".i", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(change.Details.Any(detail => detail.PropertyName == property), string.Join(", ", change.Details.Select(static detail => detail.PropertyName)));
    }

    private static bool IsEmpty(ChangeSet diff)
        => diff.Added.Count + diff.Removed.Count + diff.Modified.Count == 0;

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
