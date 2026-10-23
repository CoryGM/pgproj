using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-307: <c>FOR UPDATE OF x</c> (and <c>FOR NO KEY UPDATE</c>, <c>FOR SHARE</c>, <c>FOR KEY SHARE</c>) names a FROM item of its query, by alias or by table name;
/// it is not a reference to a table called <c>x</c>, in a <c>LANGUAGE sql</c> body (an error once <c>schemas</c> is set) or in a PL/pgSQL body (a warning).
/// </summary>
[TestClass]
public sealed class RowLockingClauseTests
{
    private const string Table = "CREATE TABLE public.t (id int PRIMARY KEY, n int);";

    [TestMethod]
    [DataRow(null)]
    [DataRow("public")]
    public void AnAliasALockingClauseNamesIsNotAMissingTable(string? schema)
    {
        var model = Build(
            Table + """
            CREATE FUNCTION public.f() RETURNS int LANGUAGE plpgsql AS $$
            DECLARE r record;
            BEGIN
              FOR r IN SELECT x.id FROM public.t AS x FOR UPDATE OF x LOOP NULL; END LOOP;
              RETURN 1;
            END $$;
            CREATE FUNCTION public.g() RETURNS SETOF int LANGUAGE sql AS $$ SELECT y.id FROM public.t AS y FOR UPDATE OF y $$;
            """,
            schema is null ? null : [schema]);

        AssertNoDiagnostics(model);
        CollectionAssert.AreEqual(new[] { "public.t" }, model.Functions["public.g()"].ReferencedTables.ToArray());
    }

    [TestMethod]
    [DataRow("FOR UPDATE OF y")]
    [DataRow("FOR NO KEY UPDATE OF y")]
    [DataRow("FOR SHARE OF y")]
    [DataRow("FOR KEY SHARE OF y NOWAIT")]
    [DataRow("FOR UPDATE OF y SKIP LOCKED")]
    [DataRow("FOR SHARE OF y FOR UPDATE OF z")]
    [DataRow("FOR UPDATE")]
    public void EveryStrengthAndWaitPolicyNamesOnlyItsFromItems(string clause)
    {
        var model = Build(Table + $"CREATE FUNCTION public.g() RETURNS SETOF int LANGUAGE sql AS $$ SELECT y.id FROM public.t AS y JOIN public.t AS z ON z.id = y.n {clause} $$;", ["public"]);

        AssertNoDiagnostics(model);
        CollectionAssert.AreEqual(new[] { "public.t" }, model.Functions["public.g()"].ReferencedTables.ToArray());
    }

    [TestMethod]
    public void ATableNamedWithoutAnAliasIsStillTheOneTheQueryReads()
    {
        var model = Build(Table + "CREATE FUNCTION public.g() RETURNS SETOF int LANGUAGE sql AS $$ SELECT id FROM public.t FOR UPDATE OF t $$;", ["public"]);

        AssertNoDiagnostics(model);
        CollectionAssert.AreEqual(new[] { "public.t" }, model.Functions["public.g()"].ReferencedTables.ToArray());
    }

    [TestMethod]
    public void ALockingClauseInASubqueryIsReadInItsOwnQuery()
    {
        var model = Build(Table + "CREATE FUNCTION public.g() RETURNS SETOF int LANGUAGE sql AS $$ SELECT s.id FROM (SELECT w.id FROM public.t AS w FOR UPDATE OF w) AS s $$;", ["public"]);

        AssertNoDiagnostics(model);
    }

    [TestMethod]
    public void ATableTheQueryReadsAndTheProjectLacksIsStillReported()
    {
        var model = Build(
            Table + """
            CREATE FUNCTION public.f() RETURNS int LANGUAGE plpgsql AS $$ BEGIN PERFORM y.id FROM public.gone AS y FOR UPDATE OF y; RETURN 1; END $$;
            CREATE FUNCTION public.g() RETURNS SETOF int LANGUAGE sql AS $$ SELECT y.id FROM public.gone AS y FOR UPDATE OF y $$;
            """,
            ["public"]);

        var messages = model.Diagnostics.Select(static diagnostic => diagnostic.Message).ToArray();
        Assert.IsTrue(messages.Any(static message => message.Contains("'public.g()'", StringComparison.Ordinal) && message.Contains("public.gone", StringComparison.Ordinal)), string.Join(" | ", messages));
        Assert.IsTrue(messages.Any(static message => message.StartsWith("The PL/pgSQL body", StringComparison.Ordinal) && message.Contains("public.gone", StringComparison.Ordinal)), string.Join(" | ", messages));
        Assert.IsFalse(messages.Any(static message => message.Contains("'y'", StringComparison.Ordinal)), string.Join(" | ", messages));
    }

    private static void AssertNoDiagnostics(DatabaseModel model)
        => Assert.IsEmpty(model.Diagnostics, string.Join(" | ", model.Diagnostics.Select(static diagnostic => diagnostic.Message)));

    private static DatabaseModel Build(string sql, IReadOnlyList<string>? schemas = null)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)], schemas);
}
