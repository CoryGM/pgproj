using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-56: a column DEFAULT is now captured, so the semantic passes see real default expressions for the first time.
/// They must accept every valid default (no invented "missing column" errors), never recurse or crash on the text
/// PostgreSQL reports back, and give two spellings of one default the same comparison form.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ColumnDefaultSemanticsTests
{
    [TestMethod]
    [DataRow("timestamptz default CURRENT_TIMESTAMP")]
    [DataRow("date default CURRENT_DATE")]
    [DataRow("time default LOCALTIME")]
    [DataRow("text default CURRENT_USER")]
    [DataRow("integer[] default ARRAY[1, 2, 3]")]
    [DataRow("text[] default ARRAY['a', 'b']")]
    [DataRow("text default current_setting('app.setting', true)")]
    [DataRow("text default 'example.com'")]
    [DataRow("text default 'schema.table.column'")]
    [DataRow("text default 'f(x), g(y)'")]
    [DataRow("text default $$a.b, c(d)$$")]
    [DataRow("text default $tag$a.b, it's$tag$")]
    [DataRow("text default E'a\\'b.c, d(e)'")]
    [DataRow("text default 'a''b.c'")]
    [DataRow("interval default '1 day'::interval")]
    [DataRow("text default 'it''s, (fine)'::text")]
    [DataRow("text default case when true then 'a.b' else 'c' end")]
    [DataRow("integer default (1 + 2) * 3")]
    [DataRow("timestamptz default (now() at time zone 'utc')")]
    [DataRow("jsonb default '{\"a\": [1, 2]}'::jsonb")]
    public void ValidDefaultsProduceNoErrorDiagnostics(string columnDefinition)
    {
        var model = Build($"create table public.t (c {columnDefinition});");

        var errors = model.Diagnostics.Where(static diagnostic => diagnostic.Severity == SemanticDiagnosticSeverity.Error).ToArray();
        Assert.IsEmpty(errors, columnDefinition + ": " + string.Join("; ", errors.Select(static error => error.Message)));
    }

    [TestMethod]
    public void TextInAStringLiteralIsNotADependency()
    {
        var model = Build("create table public.t (a integer, b text default 'a.b, a(x)::integer');");

        var column = model.Tables["public.t"].Columns.Single(static column => column.Name == "b");
        CollectionAssert.DoesNotContain(column.ReferencedColumns.ToList(), "public.t.a");
        Assert.IsFalse(column.ReferencedFunctions.Any(), string.Join(",", column.ReferencedFunctions));
    }

    /// <summary>
    /// The text PostgreSQL reports for a default reaches the expression mapper when the live model is built. A space or
    /// comma inside a literal, with no top-level comma, used to recurse until the process died.
    /// </summary>
    [TestMethod]
    [DataRow("'1 day'::interval")]
    [DataRow("'it''s, (fine)'::text")]
    [DataRow("'a b'::text")]
    [DataRow("'{a,b}'::text[]")]
    [DataRow("'x y'::character varying")]
    [DataRow("(now() AT TIME ZONE 'utc'::text)")]
    public void TextPostgreSqlReportsForADefaultDoesNotRecurseForever(string reportedDefault)
    {
        var model = Build($"CREATE TABLE \"public\".\"t\" (\n\"c\" text DEFAULT {reportedDefault}\n);");

        Assert.IsTrue(model.Tables.ContainsKey("public.t"));
    }

    [TestMethod]
    public void RawDefaultIsKeptAsWrittenAndNormalizedFormIsAlongside()
    {
        var model = Build("create table public.t (c text default   'x' ::  text  );");

        var column = model.Tables["public.t"].Columns.Single();
        Assert.AreEqual("'x' ::  text", column.RawDefaultExpression);
        Assert.AreEqual("'x'", column.NormalizedDefaultExpression);
        Assert.AreEqual("'x'", column.GetComparableDefaultExpression());
    }

    [TestMethod]
    public void ColumnWithoutADefaultHasNoNormalizedDefault()
    {
        var column = Build("create table public.t (c text);").Tables["public.t"].Columns.Single();

        Assert.IsNull(column.RawDefaultExpression);
        Assert.IsNull(column.NormalizedDefaultExpression);
        Assert.IsNull(column.GetComparableDefaultExpression());
    }

    [TestMethod]
    [DataRow("integer default 0", "integer default (0)")]
    [DataRow("text default 'x'", "text default 'x'::text")]
    [DataRow("integer default -1", "integer default '-1'::integer")]
    [DataRow("varchar(5) default 'x'", "varchar(5) default 'x'::character varying")]
    [DataRow("timestamptz default now()", "timestamptz default NOW()")]
    [DataRow("text default null", "text")]
    public void EquivalentDefaultsShareAComparisonForm(string left, string right)
    {
        Assert.AreEqual(Comparable(left), Comparable(right), $"{left} vs {right}");
    }

    [TestMethod]
    [DataRow("integer default 0", "integer default 1")]
    [DataRow("text default 'a  b'", "text default 'a b'")]
    [DataRow("text default 'A'", "text default 'a'")]
    [DataRow("integer default 1", "integer default '1'::bigint")]
    [DataRow("text default now()::text", "text default clock_timestamp()::text")]
    public void DifferentDefaultsDoNotShareAComparisonForm(string left, string right)
    {
        Assert.AreNotEqual(Comparable(left), Comparable(right), $"{left} vs {right}");
    }

    [TestMethod]
    public void StringLiteralsAreNeverAlteredByTheComparisonForm()
    {
        Assert.AreEqual("'a  B\n c'", Comparable("text default 'a  B\n c'"));
    }

    /// <summary>
    /// G-102: the text parser has no cast, so every default written with one was "unparsable" (a warning), and a cast to a
    /// schema-qualified type was read as a missing column (an error once <c>schemas</c> is set). These are the forms
    /// <c>pgproj init</c> writes, so no scaffolded project was warning-free.
    /// </summary>
    [TestMethod]
    [DataRow("numeric default 0::numeric")]
    [DataRow("numeric(10,2) default 1.5::numeric")]
    [DataRow("text[] default '{}'::text[]")]
    [DataRow("integer default 0::int")]
    [DataRow("jsonb default '{}'::jsonb")]
    [DataRow("integer[] default ARRAY[]::integer[]")]
    [DataRow("varchar(5) default 'x'::character varying")]
    [DataRow("text default ''::text")]
    [DataRow("boolean default 'true'::boolean")]
    [DataRow("timestamptz default '2026-01-01'::timestamptz")]
    [DataRow("s.st default 'new'::s.st")]
    [DataRow("s.st default 'new'::\"s\".\"st\"")]
    public void ACastDefaultProducesNoDiagnosticAtAll(string columnDefinition)
    {
        var model = Build($"create schema s; create type s.st as enum ('new', 'old'); create table s.t (c {columnDefinition});", ["s"]);

        Assert.IsEmpty(model.Diagnostics, columnDefinition + ": " + string.Join("; ", model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    [TestMethod]
    public void ACastToAnEnumIsATypeReferenceNotAColumn()
    {
        var model = Build("create schema s; create type s.st as enum ('new', 'old'); create table s.t (c s.st default 'new'::s.st);", ["s"]);

        var column = model.Tables["s.t"].Columns.Single();
        CollectionAssert.Contains(column.ReferencedTypes.ToList(), "s.st");
        // A defaulted column lists itself (as `integer default 0` does); the cast's type must not appear as another column.
        Assert.AreEqual("s.t.c", string.Join(",", column.ReferencedColumns));
    }

    [TestMethod]
    public void ACastToATypeThatDoesNotExistIsReportedAsAMissingType()
    {
        var model = Build("create schema s; create table s.t (c text default 'x'::s.nosuch);", ["s"]);

        var messages = model.Diagnostics.Select(static diagnostic => diagnostic.Message).ToArray();
        Assert.IsTrue(messages.Any(static message => message.Contains("missing type 's.nosuch'", StringComparison.Ordinal)), string.Join("; ", messages));
        Assert.IsFalse(messages.Any(static message => message.Contains("missing column", StringComparison.Ordinal)), string.Join("; ", messages));
    }

    private static DatabaseModel Build(string sql, IReadOnlyList<string> schemas)
        => new SemanticBuilder().Build([new PgQueryParser().Parse(sql)], schemas);

    private static string? Comparable(string columnDefinition)
        => Build($"create table public.t (c {columnDefinition});").Tables["public.t"].Columns.Single().GetComparableDefaultExpression();

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
