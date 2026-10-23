using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-72 and G-73. Every CHECK constraint that combined conditions, and every index predicate or key expression that was not a bare
/// comparison, failed the build with a bogus "missing column" or "missing function" (a keyword or a whole text read as one name). What an
/// expression refers to is now read from pg_query's tree, and what is compared is the tree's canonical form, so the same condition
/// spelled the way the author wrote it and the way PostgreSQL prints it back is one condition, and a different condition is not.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class CheckAndIndexExpressionSemanticsTests
{
    private const string Table = "CREATE TABLE public.t (a integer, b integer, n text, v varchar(20), arr integer[], CONSTRAINT c CHECK ({0}));";

    [TestMethod]
    [DataRow("a > 0 AND a < 9")]
    [DataRow("a > 0 OR b > 0")]
    [DataRow("a BETWEEN 1 AND 9")]
    [DataRow("a NOT BETWEEN 1 AND 9")]
    [DataRow("a IN (1, 2)")]
    [DataRow("a NOT IN (1, 2)")]
    [DataRow("a = ANY (ARRAY[1, 2])")]
    [DataRow("NOT (a > 0)")]
    [DataRow("CASE WHEN a > 0 THEN true ELSE false END")]
    [DataRow("(a > 0) IS TRUE")]
    [DataRow("coalesce(a, 0) > 0")]
    [DataRow("arr[1] > 0")]
    [DataRow("a IS DISTINCT FROM b")]
    [DataRow("a IS NOT DISTINCT FROM b")]
    [DataRow("n COLLATE \"C\" > 'a'")]
    [DataRow("n ~ '^a' AND length(n) < 5")]
    [DataRow("n LIKE 'a%' OR n IS NULL")]
    [DataRow("a IS NOT NULL")]
    [DataRow("a % 2 = 0")]
    [DataRow("a > 0 AND (b IS NULL OR b > a)")]
    [DataRow("a::bigint > 0")]
    [DataRow("abs(a) BETWEEN 1 AND 9")]
    [DataRow("-a < 5")]
    [DataRow("t.a > 0")]
    [DataRow("public.t.a > 0")]
    public void ACheckThatCombinesConditionsBuilds(string condition)
    {
        var model = Build(string.Format(Table, condition));

        Assert.IsEmpty(Errors(model), condition);
    }

    [TestMethod]
    public void TheColumnsAndFunctionsACheckUsesAreItsReferencesAndNothingElse()
    {
        var model = Build(string.Format(Table, "a > 0 AND (n IS NULL OR length(n) BETWEEN 1 AND 5) AND coalesce(b, 0) IN (1, 2)"));

        var constraint = model.Constraints.Values.Single();
        CollectionAssert.AreEquivalent(new[] { "a", "b", "n" }, constraint.ReferencedColumns.ToArray());
        CollectionAssert.AreEquivalent(new[] { "length" }, constraint.ReferencedFunctions.ToArray());
    }

    [TestMethod]
    public void AColumnThatDoesNotExistIsStillReported()
    {
        // schemas is populated so G-83's schema-scope leniency doesn't mask this genuine, in-schema typo.
        var model = Build(string.Format(Table, "a > 0 AND nope < 9"), schemas: ["public"]);

        Assert.IsTrue(Errors(model).Any(static message => message.Contains("missing column 'nope'", StringComparison.Ordinal)), string.Join("; ", Errors(model)));
    }

    [TestMethod]
    public void AFunctionThatDoesNotExistIsStillReported()
    {
        var model = Build(string.Format(Table, "a > 0 AND public.nofunc(b) < 9"), schemas: ["public"]);

        Assert.IsTrue(Errors(model).Any(static message => message.Contains("nofunc", StringComparison.Ordinal)), string.Join("; ", Errors(model)));
    }

    [TestMethod]
    public void AProjectFunctionInACheckIsAReferenceSoItIsCreatedFirst()
    {
        var model = Build(
            string.Format(Table, "public.zz(a) > 0 AND b > 0")
            + " CREATE FUNCTION public.zz(x integer) RETURNS integer LANGUAGE sql IMMUTABLE AS $$ SELECT x $$;");

        Assert.IsEmpty(Errors(model));
        Assert.IsTrue(
            model.Constraints.Values.Single().ReferencedFunctions.Any(static function => function.Contains("zz", StringComparison.Ordinal)),
            string.Join(", ", model.Constraints.Values.Single().ReferencedFunctions));
    }

    [TestMethod]
    [DataRow("CREATE INDEX i ON public.t (a) WHERE a > 0 AND b IS NOT NULL")]
    [DataRow("CREATE INDEX i ON public.t (a) WHERE a IN (1, 2)")]
    [DataRow("CREATE INDEX i ON public.t (a) WHERE n LIKE 'x%' OR a IS NULL")]
    [DataRow("CREATE INDEX i ON public.t (a) WHERE a BETWEEN 1 AND 9")]
    [DataRow("CREATE INDEX i ON public.t ((arr[1]))")]
    [DataRow("CREATE INDEX i ON public.t ((a BETWEEN 1 AND 2)) WHERE n LIKE 'x%' OR a IS NULL")]
    [DataRow("CREATE INDEX i ON public.t ((CASE WHEN a > 0 THEN 1 ELSE 2 END))")]
    [DataRow("CREATE INDEX i ON public.t (lower(n), (a + b) DESC, a) WHERE a > 0 AND b IS NOT NULL")]
    public void AnIndexWithACompoundPredicateOrExpressionBuilds(string index)
    {
        var model = Build("CREATE TABLE public.t (a integer, b integer, n text, arr integer[]);" + index + ";");

        Assert.IsEmpty(Errors(model), index);
    }

    [TestMethod]
    public void AnIndexReferencesTheColumnsAndFunctionsOfItsKeyAndPredicate()
    {
        var model = Build("CREATE TABLE public.t (a integer, b integer, n text); CREATE INDEX i ON public.t (lower(n), (a + b)) WHERE b IN (1, 2) AND a IS NOT NULL;");

        var index = model.Indexes.Values.Single();
        CollectionAssert.AreEquivalent(
            new[] { "public.t.a", "public.t.b", "public.t.n" },
            index.ReferencedColumns.Where(static column => column.StartsWith("public.", StringComparison.Ordinal)).ToArray());
        CollectionAssert.Contains(index.ReferencedFunctions.ToArray(), "lower");
    }

    // (what the author wrote, what PostgreSQL prints back): one condition.
    [TestMethod]
    [DataRow("a > 0 AND a < 9", "((a > 0) AND (a < 9))")]
    [DataRow("a BETWEEN 1 AND 9", "((a >= 1) AND (a <= 9))")]
    [DataRow("a IN (1, 2)", "(a = ANY (ARRAY[1, 2]))")]
    [DataRow("a NOT IN (1, 2)", "(a <> ALL (ARRAY[1, 2]))")]
    [DataRow("n LIKE 'a%'", "(n ~~ 'a%'::text)")]
    [DataRow("v = 'x'", "((v)::text = 'x'::text)")]
    [DataRow("a::bigint > 0", "((a)::bigint > 0)")]
    [DataRow("a > 0 OR b > 0 OR a < 5", "(((a > 0) OR (b > 0)) OR (a < 5))")]
    [DataRow("(a > 0)", "a > 0")]
    [DataRow("a>0   AND\n b>0", "a > 0 AND b > 0")]
    [DataRow("a IS DISTINCT FROM b", "(a IS DISTINCT FROM b)")]
    public void TheSameConditionInTwoSpellingsCompareEqual(string written, string printed)
    {
        Assert.IsNotNull(Normalized(written), "A condition with a parsed tree has a canonical form.");
        Assert.AreEqual(Normalized(written), Normalized(printed), $"{written}  vs  {printed}");
    }

    [TestMethod]
    [DataRow("a > 0 AND a < 9", "a > 0 OR a < 9")]
    [DataRow("a BETWEEN 1 AND 9", "a BETWEEN 1 AND 10")]
    [DataRow("a > 0", "a >= 0")]
    [DataRow("a IN (1, 2)", "a IN (1, 3)")]
    [DataRow("a IN (1, 2)", "a NOT IN (1, 2)")]
    [DataRow("v = 'x'", "v = 'y'")]
    [DataRow("n LIKE 'a%'", "n LIKE 'A%'")]
    [DataRow("n LIKE 'a%'", "n ILIKE 'a%'")]
    [DataRow("a > 0 AND b > 0", "a > 0 AND a > 0")]
    [DataRow("a > 0 AND (b > 0 OR a < 5)", "(a > 0 AND b > 0) OR a < 5")]
    [DataRow("a + b * 2 > 3", "(a + b) * 2 > 3")]
    [DataRow("coalesce(a, 0) > 0", "coalesce(a, 1) > 0")]
    [DataRow("a IS NULL", "a IS NOT NULL")]
    [DataRow("CASE WHEN a > 0 THEN true ELSE false END", "CASE WHEN a > 0 THEN false ELSE true END")]
    [DataRow("arr[1] > 0", "arr[2] > 0")]
    [DataRow("n COLLATE \"C\" > 'a'", "n COLLATE \"POSIX\" > 'a'")]
    [DataRow("(a > 0) IS TRUE", "(a > 0) IS FALSE")]
    [DataRow("a > 0", "b > 0")]
    public void ADifferentConditionIsNotTheSameCondition(string one, string other)
    {
        Assert.AreNotEqual(Normalized(one), Normalized(other), $"{one}  vs  {other}");
    }

    [TestMethod]
    public void AnIndexPredicateAndKeyExpressionHaveACanonicalForm()
    {
        var one = Build("CREATE TABLE public.t (a integer, n text); CREATE INDEX i ON public.t ((a + 1), lower(n)) WHERE a BETWEEN 1 AND 9;").Indexes.Values.Single();
        var other = Build("CREATE TABLE public.t (a integer, n text); CREATE INDEX i ON public.t (((a + 1)), lower(n)) WHERE ((a >= 1) AND (a <= 9));").Indexes.Values.Single();

        Assert.IsNotNull(one.NormalizedPredicateExpression);
        Assert.AreEqual(one.NormalizedPredicateExpression, other.NormalizedPredicateExpression);
        Assert.AreEqual(one.KeyElements[0].NormalizedExpression, other.KeyElements[0].NormalizedExpression);
        Assert.AreEqual(one.KeyElements[1].NormalizedExpression, other.KeyElements[1].NormalizedExpression);
        Assert.IsNull(Build("CREATE TABLE public.t (a integer); CREATE INDEX i ON public.t (a);").Indexes.Values.Single().KeyElements[0].NormalizedExpression);
    }

    private static string? Normalized(string condition)
        => Build(string.Format(Table, condition)).Constraints.Values.Single().NormalizedCheckExpression;

    private static string[] Errors(DatabaseModel model)
        => model.Diagnostics.Where(static d => d.Severity == SemanticDiagnosticSeverity.Error).Select(static d => d.Message).ToArray();

    private static DatabaseModel Build(string sql, IReadOnlyList<string>? schemas = null)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)], schemas);
}

/// <summary>G-74: an operator class or collation on an index key is not a missing type.</summary>
[TestClass]
[DoNotParallelize]
public sealed class IndexOperatorClassSemanticsTests
{
    [TestMethod]
    [DataRow("(n text_pattern_ops)")]
    [DataRow("(n varchar_pattern_ops)")]
    [DataRow("(a int4_ops)")]
    [DataRow("(lower(n) text_pattern_ops)")]
    [DataRow("(n COLLATE \"C\" text_pattern_ops DESC NULLS FIRST, a)")]
    [DataRow("(n COLLATE \"C\")")]
    [DataRow("(a, n text_pattern_ops)")]
    public void AnIndexKeyWithABuiltInOperatorClassOrCollationBuilds(string keys)
    {
        var model = new SemanticPipeline().Execute([new PgQueryParser().Parse("CREATE TABLE public.t (a integer, n text); CREATE INDEX i ON public.t " + keys + ";")]);

        Assert.IsEmpty(model.Diagnostics.Where(static d => d.Severity == SemanticDiagnosticSeverity.Error).Select(static d => d.Message).ToArray());
    }

    [TestMethod]
    public void AnOperatorClassTheProjectDefinesAsATypeIsStillADependency()
    {
        var model = new SemanticPipeline().Execute([new PgQueryParser().Parse(
            "CREATE TYPE public.myops AS ENUM ('x'); CREATE TABLE public.t (n text); CREATE INDEX i ON public.t (n myops);")]);

        Assert.IsTrue(model.Indexes.Values.Single().ReferencedTypes.Any(static type => type.Contains("myops", StringComparison.Ordinal)));
    }
}
