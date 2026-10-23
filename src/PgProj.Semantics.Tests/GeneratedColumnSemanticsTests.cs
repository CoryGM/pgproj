using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-71: a generated column is modeled with its expression, kept as written beside a canonical comparison form (PostgreSQL prints a stored
/// expression fully parenthesized), and its expression is analyzed like any other so the functions it calls are dependencies.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class GeneratedColumnSemanticsTests
{
    private const string Table = "CREATE TABLE public.t (a integer, b integer, n text, v varchar(20), g integer GENERATED ALWAYS AS ({0}) STORED);";

    [TestMethod]
    public void AGeneratedColumnKeepsItsExpressionAsWrittenBesideAComparisonForm()
    {
        var column = G("a  *  2");

        Assert.IsTrue(column.IsGenerated);
        Assert.AreEqual("a  *  2", column.GeneratedExpression);
        Assert.IsFalse(string.IsNullOrWhiteSpace(column.NormalizedGeneratedExpression));
        Assert.IsNull(column.RawDefaultExpression);
    }

    [TestMethod]
    [DataRow("a * 2", "(a * 2)")]
    [DataRow("a * 2", "A*2")]
    [DataRow("a * 2 + b", "((a * 2) + b)")]
    [DataRow("(a + b) * 2", "((a + b) * 2)")]
    [DataRow("a + b + 1", "(((a + b)) + 1)")]
    [DataRow("a != 0", "(a <> 0)")]
    [DataRow("a in (1, 2)", "(a = ANY (ARRAY[1, 2]))")]
    [DataRow("a between 1 and 5", "((a >= 1) AND (a <= 5))")]
    [DataRow("n like 'h%'", "(n ~~ 'h%'::text)")]
    [DataRow("lower(n)", "lower(n)")]
    [DataRow("pg_catalog.lower(n)", "lower(n)")]
    [DataRow("upper(v)", "upper((v)::text)")]
    [DataRow("v || 'x'", "((v)::text || 'x'::text)")]
    [DataRow("a::bigint * 2", "(((a)::bigint) * 2)")]
    public void ExpressionsThatPostgreSqlTreatsAsTheSameHaveTheSameComparisonForm(string written, string rendered)
    {
        Assert.AreEqual(G(written).NormalizedGeneratedExpression, G(rendered).NormalizedGeneratedExpression, $"'{written}' and '{rendered}' must compare equal.");
    }

    [TestMethod]
    [DataRow("a * 2", "a * 3")]
    [DataRow("a * 2", "b * 2")]
    [DataRow("a + b * 2", "(a + b) * 2")]
    [DataRow("a + b", "a - b")]
    [DataRow("lower(n)", "upper(n)")]
    [DataRow("a::bigint", "a::integer")]
    [DataRow("n || 'x'", "n || 'y'")]
    [DataRow("n || 'x'", "n || 'X'")]
    public void ExpressionsThatDifferHaveDifferentComparisonForms(string one, string other)
    {
        Assert.AreNotEqual(G(one).NormalizedGeneratedExpression, G(other).NormalizedGeneratedExpression);
    }

    [TestMethod]
    public void ACastToTextIsKeptOnAColumnThatIsNotVarchar()
    {
        Assert.AreNotEqual(G("n::text").NormalizedGeneratedExpression, G("n").NormalizedGeneratedExpression);
        Assert.AreNotEqual(G("a::text || 'x'").NormalizedGeneratedExpression, G("(a || 'x')").NormalizedGeneratedExpression);
    }

    [TestMethod]
    public void AnOrdinaryColumnHasNoGeneratedExpression()
    {
        var model = Build(string.Format(Table, "a * 2"));

        var ordinary = model.Tables["public.t"].Columns.Single(static column => column.Name == "a");
        Assert.IsFalse(ordinary.IsGenerated);
        Assert.IsNull(ordinary.NormalizedGeneratedExpression);
    }

    [TestMethod]
    public void AFunctionTheExpressionCallsIsADependencyOfTheColumnAndTheTable()
    {
        var model = Build(
            "CREATE FUNCTION public.zz_double(x integer) RETURNS integer LANGUAGE sql IMMUTABLE AS $$ SELECT x * 2 $$;"
            + " CREATE TABLE public.t (a integer, g integer GENERATED ALWAYS AS (public.zz_double(a)) STORED);");

        Assert.IsFalse(HasErrors(model), Dump(model));
        Assert.IsTrue(model.Tables["public.t"].Columns.Single(static column => column.Name == "g").ReferencedFunctions.Any(static reference => reference.Contains("zz_double", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void AFunctionThatDoesNotExistIsReported()
    {
        // schemas is populated so G-83's schema-scope leniency doesn't mask this genuine, in-schema typo.
        var model = Build("CREATE TABLE public.t (a integer, g integer GENERATED ALWAYS AS (missing_fn(a)) STORED);", schemas: ["public"]);

        Assert.IsTrue(HasErrors(model));
        StringAssert.Contains(Dump(model), "missing_fn");
    }

    [TestMethod]
    public void BuiltInFunctionsAndColumnsInTheExpressionRaiseNoErrors()
    {
        var model = Build(string.Format(Table, "coalesce(abs(a - b), length(n)) + round(1.5)"));

        Assert.IsFalse(HasErrors(model), Dump(model));
    }

    [TestMethod]
    [DataRow("trim(both ' ' from n)")]
    [DataRow("trim(n)")]
    [DataRow("a = any(array[1, 2])")]
    [DataRow("(a = ANY (ARRAY[1, 2]))")]
    [DataRow("a > all(array[1, 2])")]
    [DataRow("a = some(array[1, 2])")]
    [DataRow("substring(n from 1 for 1)")]
    [DataRow("position('i' in n)")]
    public void SqlSyntaxConstructsThatLookLikeFunctionCallsAreNotMissingFunctions(string expression)
    {
        var model = Build(string.Format(Table, expression));

        Assert.IsFalse(HasErrors(model), Dump(model));
    }

    [TestMethod]
    public void TrimIsAcceptedInACheckAndAViewToo()
    {
        var model = Build("CREATE TABLE public.t (n text CHECK (trim(n) <> '')); CREATE VIEW public.v AS SELECT trim(n) AS m FROM public.t;");

        Assert.IsFalse(HasErrors(model), Dump(model));
    }

    [TestMethod]
    public void ANamedGeneratedColumnKeepsItsSpellingAndItsQuotedReferences()
    {
        var model = Build("CREATE TABLE t (\"Amount\" integer, \"Total Col\" integer GENERATED ALWAYS AS (\"Amount\" * 2) STORED);");

        var column = model.Tables.Values.Single().Columns.Single(static column => column.Name == "Total Col");
        Assert.AreEqual("\"Amount\" * 2", column.GeneratedExpression);
        Assert.IsFalse(HasErrors(model), Dump(model));
    }

    [TestMethod]
    public void AGeneratedColumnAddedByAlterTableJoinsTheTable()
    {
        var model = Build("CREATE TABLE public.t (a integer); ALTER TABLE public.t ADD COLUMN g integer GENERATED ALWAYS AS (a * 2) STORED;");

        Assert.IsTrue(model.Tables["public.t"].Columns.Single(static column => column.Name == "g").IsGenerated);
        Assert.IsFalse(HasErrors(model), Dump(model));
    }

    private static ColumnDefinition G(string expression)
        => Build(string.Format(Table, expression)).Tables["public.t"].Columns.Single(static column => column.Name == "g");

    private static DatabaseModel Build(string sql, IReadOnlyList<string>? schemas = null)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)], schemas);

    private static bool HasErrors(DatabaseModel model) => model.Diagnostics.Any(static diagnostic => diagnostic.Severity == SemanticDiagnosticSeverity.Error);

    private static string Dump(DatabaseModel model) => string.Join(Environment.NewLine, model.Diagnostics.Select(static diagnostic => $"{diagnostic.Severity}: {diagnostic.Message}"));
}
