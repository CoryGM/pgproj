using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-90 part 2: what a column's <c>DEFAULT</c> refers to is bound from pg_query's tree for the expression, not by finding the
/// <c>CREATE TABLE</c> text and scanning it. A default added by <c>ALTER TABLE ... ADD COLUMN</c> is read too, and keywords, literals, and
/// cast types can't be mistaken for columns or calls.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ColumnDefaultBindingTests
{
    private const string Routines = """
        CREATE TYPE public.mood AS ENUM ('ok', 'sad');
        CREATE FUNCTION public.f(p int) RETURNS int LANGUAGE sql AS $$ SELECT p $$;
        CREATE FUNCTION public.g(p int) RETURNS int LANGUAGE sql AS $$ SELECT p $$;
        """;

    [TestMethod]
    public void ADefaultAddedByAlterTableIsBound()
    {
        var model = Build(Routines + "CREATE TABLE public.t (id int); ALTER TABLE public.t ADD COLUMN m public.mood DEFAULT 'ok'::public.mood; ALTER TABLE public.t ADD COLUMN n int DEFAULT public.f(1);");

        var columns = model.Tables["public.t"].Columns.ToDictionary(static column => column.Name);
        CollectionAssert.Contains(columns["m"].ReferencedTypes.ToList(), "public.mood");
        Assert.IsTrue(columns["n"].ReferencedFunctions.Any(static name => name.StartsWith("public.f", StringComparison.Ordinal)), string.Join(", ", columns["n"].ReferencedFunctions));
        AssertNoDiagnostics(model);
    }

    [TestMethod]
    public void NestedCallsAndCastsAreBound()
    {
        var model = Build(Routines + "CREATE TABLE public.t (n numeric DEFAULT CAST(public.f(public.g(1)) AS numeric(10,2)), m public.mood DEFAULT 'sad'::public.mood);");

        var columns = model.Tables["public.t"].Columns.ToDictionary(static column => column.Name);
        var functions = string.Join(", ", columns["n"].ReferencedFunctions);
        Assert.IsTrue(columns["n"].ReferencedFunctions.Any(static name => name.StartsWith("public.f", StringComparison.Ordinal)), functions);
        Assert.IsTrue(columns["n"].ReferencedFunctions.Any(static name => name.StartsWith("public.g", StringComparison.Ordinal)), functions);
        CollectionAssert.Contains(columns["m"].ReferencedTypes.ToList(), "public.mood");
        AssertNoDiagnostics(model);
    }

    [TestMethod]
    public void KeywordsAndLiteralsAreNotNames()
    {
        var model = Build("""
            CREATE TABLE public.t (
                a timestamptz DEFAULT CURRENT_TIMESTAMP,
                b text DEFAULT 'public.t.a' || current_user,
                c boolean DEFAULT true,
                d text DEFAULT $x$f(public.t.b)$x$,
                e int[] DEFAULT ARRAY[1, 2]
            );
            """);

        foreach (var column in model.Tables["public.t"].Columns)
        {
            Assert.IsEmpty(column.ReferencedFunctions, column.Name + ": " + string.Join(", ", column.ReferencedFunctions));
            // A column always lists itself; no other column is named by any of these defaults.
            Assert.IsFalse(column.ReferencedColumns.Any(name => !string.Equals(name, column.Key, StringComparison.OrdinalIgnoreCase)),
                column.Name + ": " + string.Join(", ", column.ReferencedColumns));
        }

        AssertNoDiagnostics(model);
    }

    [TestMethod]
    public void AKeywordSpelledLikeAColumnIsNotThatColumn()
    {
        // The text scan read every bare word that matched a column's name as that column (G-90 part 2).
        var model = Build("""
            CREATE TABLE public.t (
                zone text, "time" text, epoch text, "placing" text, "both" text,
                ts timestamp DEFAULT (now() AT TIME ZONE 'utc'),
                n numeric DEFAULT extract(epoch from now()),
                o text DEFAULT overlay('abc' placing 'x' from 2),
                tr text DEFAULT trim(both ' ' from ' a ')
            );
            """);

        foreach (var column in model.Tables["public.t"].Columns)
        {
            Assert.IsFalse(column.ReferencedColumns.Any(name => !string.Equals(name, column.Key, StringComparison.OrdinalIgnoreCase)),
                column.Name + ": " + string.Join(", ", column.ReferencedColumns));
        }
    }

    [TestMethod]
    public void AGrammarFormIsNotACallToItsKeyword()
    {
        // COALESCE, NULLIF, GREATEST are expressions of their own, and TRIM/SUBSTRING/POSITION are calls to pg_catalog functions under other names.
        var model = Build("""
            CREATE TABLE public.t (
                a text DEFAULT coalesce(null, 'x'),
                b numeric DEFAULT nullif(1, 2),
                c int DEFAULT greatest(1, 2),
                d text DEFAULT trim(both ' ' from ' a '),
                e text DEFAULT substring('abc' from 1 for 2)
            );
            """);

        var calls = model.Tables["public.t"].Columns.SelectMany(static column => column.ReferencedFunctions).ToArray();
        foreach (var keyword in new[] { "coalesce", "nullif", "greatest", "trim", "substring" })
        {
            CollectionAssert.DoesNotContain(calls, keyword, string.Join(", ", calls));
        }
    }

    [TestMethod]
    public void ATypedLiteralIsACast()
        => CollectionAssert.Contains(
            Build("CREATE TABLE public.t (d date DEFAULT current_date + interval '1 day');").Tables["public.t"].Columns.Single().ReferencedTypes.ToList(),
            "interval");

    [TestMethod]
    public void AColumnOfAnotherTableIsReportedMissing()
    {
        var model = Build("CREATE TABLE public.t (a int DEFAULT other.x);");

        Assert.IsTrue(model.Diagnostics.Any(static diagnostic => diagnostic.Message.Contains("default expression references missing column 'other.x'", StringComparison.Ordinal)),
            string.Join(" | ", model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    private static void AssertNoDiagnostics(DatabaseModel model)
        => Assert.IsEmpty(model.Diagnostics, string.Join(" | ", model.Diagnostics.Select(static diagnostic => diagnostic.Message)));

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
