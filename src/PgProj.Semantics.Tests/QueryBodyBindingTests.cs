using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-90: what a view's query and a SQL-language routine body read is bound from pg_query's tree, not regular expressions over the
/// text. Relations, qualified columns, calls, and casts are read by what they are in the tree, scoped as PostgreSQL scopes them
/// (aliases, CTEs, subqueries, set operations), and a view read by another view is a view reference (G-91), a qualified call is a
/// call (G-92), and a <c>LANGUAGE sql</c> body's calls are read (G-94).
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class QueryBodyBindingTests
{
    private const string Tables = """
        CREATE TABLE public.o (id int, total numeric);
        CREATE TABLE public.i (o_id int, qty int);
        """;

    [TestMethod]
    public void AViewOverAViewReferencesThatView()
    {
        var model = Build(Tables + """
            CREATE VIEW public.a_top AS SELECT * FROM public.z_base WHERE total > 1;
            CREATE VIEW public.z_base AS SELECT id, total FROM public.o;
            """);

        var view = model.Views["public.a_top"];
        CollectionAssert.AreEqual(new[] { "public.z_base" }, view.ReferencedViews.ToArray());
        Assert.IsFalse(view.ReferencedTables.Contains("public.z_base", StringComparer.OrdinalIgnoreCase));
        AssertNoDiagnostics(model);
    }

    [TestMethod]
    public void AColumnOfAJoinedViewIsNotReadAsATableColumn()
    {
        var model = Build(Tables + """
            CREATE VIEW public.z_base AS SELECT id, total FROM public.o;
            CREATE VIEW public.a_join AS SELECT b.id, o.total FROM public.o o JOIN public.z_base b ON b.id = o.id;
            """);

        var view = model.Views["public.a_join"];
        CollectionAssert.AreEquivalent(new[] { "public.o.id", "public.o.total" }, view.ReferencedColumns.ToArray());
        AssertNoDiagnostics(model);
    }

    [TestMethod]
    [DataRow("public.ot(o.id)")]
    [DataRow("ot(o.id)")]
    public void AFunctionCallInTheSelectListIsACall(string call)
    {
        var model = Build(Tables + $"""
            CREATE FUNCTION public.ot(p int) RETURNS numeric LANGUAGE sql AS $$ SELECT 1::numeric $$;
            CREATE VIEW public.v AS SELECT o.id, {call} AS t FROM public.o o;
            """);

        var view = model.Views["public.v"];
        Assert.AreEqual(1, view.ReferencedFunctions.Count(static name => name.EndsWith("ot", StringComparison.Ordinal)), string.Join(", ", view.ReferencedFunctions));
        CollectionAssert.AreEqual(new[] { "public.o.id" }, view.ReferencedColumns.ToArray());
        AssertNoDiagnostics(model);
    }

    [TestMethod]
    public void AMissingTableIsReportedOnce()
    {
        var model = Build(Tables + "CREATE VIEW public.v AS SELECT n.id FROM public.nope n JOIN public.o o ON o.id = n.id;");

        var missing = model.Diagnostics.Where(static diagnostic => diagnostic.Message.Contains("missing table", StringComparison.Ordinal)).ToArray();
        Assert.HasCount(1, missing, string.Join(" | ", model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        StringAssert.Contains(missing[0].Message, "'public.nope'");
        Assert.IsFalse(model.Diagnostics.Any(static diagnostic => diagnostic.Message.Contains("missing column", StringComparison.Ordinal)), "A missing table's columns are not reported on top of it.");
    }

    [TestMethod]
    public void AMissingColumnOfATableIsReported()
    {
        var model = Build(Tables + "CREATE VIEW public.v AS SELECT o.nope FROM public.o o;");

        Assert.IsTrue(model.Diagnostics.Any(static diagnostic => diagnostic.Message.Contains("references missing column 'o.nope'", StringComparison.Ordinal)),
            string.Join(" | ", model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    [TestMethod]
    public void ACteIsNotARelation()
    {
        var model = Build(Tables + """
            CREATE VIEW public.v AS
            WITH big AS (SELECT id FROM public.o WHERE total > 10)
            SELECT big.id, i.qty FROM big JOIN public.i i ON i.o_id = big.id;
            """);

        var view = model.Views["public.v"];
        CollectionAssert.AreEquivalent(new[] { "public.o", "public.i" }, view.ReferencedTables.ToArray());
        CollectionAssert.AreEquivalent(new[] { "public.i.qty", "public.i.o_id" }, view.ReferencedColumns.ToArray());
        AssertNoDiagnostics(model);
    }

    [TestMethod]
    public void ARecursiveCteReadsOnlyItsBaseRelation()
    {
        var model = Build(Tables + """
            CREATE VIEW public.v AS
            WITH RECURSIVE n(k) AS (SELECT id FROM public.o UNION ALL SELECT k + 1 FROM n WHERE k < 5)
            SELECT k FROM n;
            """);

        CollectionAssert.AreEqual(new[] { "public.o" }, model.Views["public.v"].ReferencedTables.ToArray());
        AssertNoDiagnostics(model);
    }

    [TestMethod]
    public void EveryBranchOfASetOperationIsRead()
    {
        var model = Build(Tables + "CREATE VIEW public.v AS SELECT o.id FROM public.o o UNION SELECT i.o_id FROM public.i i EXCEPT SELECT 0;");

        var view = model.Views["public.v"];
        CollectionAssert.AreEquivalent(new[] { "public.o", "public.i" }, view.ReferencedTables.ToArray());
        CollectionAssert.AreEquivalent(new[] { "public.o.id", "public.i.o_id" }, view.ReferencedColumns.ToArray());
    }

    [TestMethod]
    public void ACorrelatedSubquerySeesItsOuterQuerysNames()
    {
        var model = Build(Tables + "CREATE VIEW public.v AS SELECT o.id, (SELECT sum(i.qty) FROM public.i i WHERE i.o_id = o.id) AS qty FROM public.o o;");

        var view = model.Views["public.v"];
        CollectionAssert.AreEquivalent(new[] { "public.o.id", "public.i.qty", "public.i.o_id" }, view.ReferencedColumns.ToArray());
        AssertNoDiagnostics(model);
    }

    [TestMethod]
    public void ALateralSubqueryAndAFunctionInFromAreRead()
    {
        var model = Build(Tables + """
            CREATE FUNCTION public.lines(p int) RETURNS SETOF int LANGUAGE sql AS $$ SELECT qty FROM public.i WHERE o_id = p $$;
            CREATE VIEW public.v AS
            SELECT o.id, l.q, g.n FROM public.o o
            CROSS JOIN LATERAL (SELECT i.qty AS q FROM public.i i WHERE i.o_id = o.id) l
            CROSS JOIN LATERAL public.lines(o.id) AS g(n);
            """);

        var view = model.Views["public.v"];
        CollectionAssert.AreEquivalent(new[] { "public.o", "public.i" }, view.ReferencedTables.ToArray());
        Assert.IsTrue(view.ReferencedFunctions.Contains("public.lines"), string.Join(", ", view.ReferencedFunctions));
        AssertNoDiagnostics(model);
    }

    [TestMethod]
    public void NamesInsideStringsAndCommentsAreNotRead()
    {
        var model = Build(Tables + """
            CREATE VIEW public.v AS
            SELECT 'from public.fake join public.fake2 f' AS note, o.id -- join public.fake3 x
            FROM public.o o /* , public.fake4 */;
            """);

        CollectionAssert.AreEqual(new[] { "public.o" }, model.Views["public.v"].ReferencedTables.ToArray());
        AssertNoDiagnostics(model);
    }

    [TestMethod]
    public void CastsAreReadAsTypes()
    {
        var model = Build(Tables + """
            CREATE TYPE public.mood AS ENUM ('ok');
            CREATE VIEW public.v AS SELECT 'ok'::public.mood AS m, CAST(o.total AS integer) AS t FROM public.o o;
            """);

        Assert.IsTrue(model.Views["public.v"].ReferencedTypes.Contains("public.mood"), string.Join(", ", model.Views["public.v"].ReferencedTypes));
        AssertNoDiagnostics(model);
    }

    [TestMethod]
    public void AMaterializedViewOverAViewReferencesThatView()
    {
        var model = Build(Tables + """
            CREATE MATERIALIZED VIEW public.m AS SELECT id FROM public.z_base;
            CREATE VIEW public.z_base AS SELECT id FROM public.o;
            """);

        CollectionAssert.AreEqual(new[] { "public.z_base" }, model.Views["public.m"].ReferencedViews.ToArray());
        AssertNoDiagnostics(model);
    }

    [TestMethod]
    public void ASqlFunctionBodysCallsTablesAndViewsAreRead()
    {
        var model = Build(Tables + """
            CREATE SCHEMA s;
            CREATE VIEW public.z_base AS SELECT id FROM public.o;
            CREATE FUNCTION s.a_top() RETURNS int LANGUAGE sql AS $$ SELECT s.z_leaf() + (SELECT count(*)::int FROM public.z_base) $$;
            CREATE FUNCTION s.z_leaf() RETURNS int LANGUAGE sql AS $$ SELECT 1 $$;
            """);

        var function = model.Functions.Values.Single(static routine => routine.Name == "a_top");
        Assert.IsTrue(function.ReferencedFunctions.Contains("s.z_leaf"), string.Join(", ", function.ReferencedFunctions));
        CollectionAssert.AreEqual(new[] { "public.z_base" }, function.ReferencedViews.ToArray());
        AssertNoDiagnostics(model);
    }

    [TestMethod]
    public void ASqlBodysWriteTargetsAreRead()
    {
        var model = Build(Tables + """
            CREATE TABLE public.log (n int);
            CREATE FUNCTION public.f(p int) RETURNS void LANGUAGE sql AS $$
                INSERT INTO public.log (n) VALUES (p);
                UPDATE public.o SET total = 0 WHERE id = p;
                DELETE FROM public.i USING public.o o WHERE i.o_id = o.id AND o.id = p;
            $$;
            """);

        CollectionAssert.AreEquivalent(new[] { "public.log", "public.o", "public.i" }, model.Functions["public.f(int4)"].ReferencedTables.ToArray());
        AssertNoDiagnostics(model);
    }

    [TestMethod]
    public void ABeginAtomicBodyIsRead()
    {
        var model = Build(Tables + """
            CREATE FUNCTION public.n() RETURNS bigint LANGUAGE sql BEGIN ATOMIC SELECT count(*) FROM public.i; END;
            """);

        CollectionAssert.AreEqual(new[] { "public.i" }, model.Functions.Values.Single(static routine => routine.Name == "n").ReferencedTables.ToArray());
        AssertNoDiagnostics(model);
    }

    [TestMethod]
    public void AProceduresSqlBodyIsRead()
    {
        var model = Build(Tables + """
            CREATE FUNCTION public.leaf() RETURNS int LANGUAGE sql AS $$ SELECT 1 $$;
            CREATE PROCEDURE public.p() LANGUAGE sql AS $$ INSERT INTO public.i (qty) SELECT public.leaf() $$;
            """);

        var procedure = model.Procedures.Values.Single();
        CollectionAssert.AreEqual(new[] { "public.i" }, procedure.ReferencedTables.ToArray());
        Assert.IsTrue(procedure.ReferencedFunctions.Contains("public.leaf"), string.Join(", ", procedure.ReferencedFunctions));
    }

    [TestMethod]
    public void AParameterQualifiedByItsFunctionIsNotAColumn()
    {
        var model = Build(Tables + "CREATE FUNCTION public.f(p int) RETURNS int LANGUAGE sql AS $$ SELECT o.id FROM public.o o WHERE o.id = f.p $$;");

        CollectionAssert.AreEqual(new[] { "public.o.id" }, model.Functions["public.f(int4)"].ReferencedColumns.ToArray());
        AssertNoDiagnostics(model);
    }

    [TestMethod]
    public void APlPgSqlBodyAddsNoDependencyButWhatItNamesAndTheProjectLacksIsAWarning()
    {
        // PostgreSQL doesn't resolve a PL/pgSQL body's names until it runs, so the model orders nothing by them (G-90); since G-303a the body is read to warn about a
        // name the project does not define (replaces the test that said a PL/pgSQL body is not read at all).
        var model = Build("CREATE FUNCTION public.f() RETURNS int LANGUAGE plpgsql AS $$ BEGIN RETURN (SELECT count(*) FROM public.nope); END $$;");

        Assert.IsEmpty(model.Functions.Values.Single().ReferencedTables);
        var diagnostic = model.Diagnostics.Single();
        Assert.AreEqual(SemanticDiagnosticSeverity.Warning, diagnostic.Severity);
        StringAssert.Contains(diagnostic.Message, "'public.nope'");
    }

    [TestMethod]
    public void TwoSqlFunctionsCallingEachOtherAreACycle()
    {
        // PostgreSQL can create neither first (each body is checked at CREATE); two PL/pgSQL functions can, since neither body is.
        var sql = Build("""
            CREATE FUNCTION public.a(n int) RETURNS int LANGUAGE sql AS $$ SELECT CASE WHEN n > 0 THEN public.b(n - 1) ELSE 0 END $$;
            CREATE FUNCTION public.b(n int) RETURNS int LANGUAGE sql AS $$ SELECT CASE WHEN n > 0 THEN public.a(n - 1) ELSE 1 END $$;
            """);
        var plpgsql = Build("""
            CREATE FUNCTION public.a(n int) RETURNS int LANGUAGE plpgsql AS $$ BEGIN RETURN public.b(n); END $$;
            CREATE FUNCTION public.b(n int) RETURNS int LANGUAGE plpgsql AS $$ BEGIN RETURN public.a(n); END $$;
            """);

        Assert.IsTrue(sql.Diagnostics.Any(static diagnostic => diagnostic.Message.Contains("cycle", StringComparison.OrdinalIgnoreCase)),
            string.Join(" | ", sql.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        AssertNoDiagnostics(plpgsql);
    }

    private static void AssertNoDiagnostics(DatabaseModel model)
        => Assert.IsEmpty(model.Diagnostics, string.Join(" | ", model.Diagnostics.Select(static diagnostic => diagnostic.Message)));

    [TestMethod]
    [DataRow("public.target()")]
    [DataRow("target()")]
    public void ACallStatementInAProcedureBodyNamesTheCalledProcedure(string call)
    {
        var model = Build($"""
            CREATE PROCEDURE public.caller() LANGUAGE sql AS $$ CALL {call}; $$;
            CREATE PROCEDURE public.target() LANGUAGE sql AS $$ SELECT 1; $$;
            """);

        var caller = model.Procedures.Values.Single(static procedure => procedure.Name == "caller");
        Assert.AreEqual(1, caller.ReferencedFunctions.Count(static name => name.EndsWith("target", StringComparison.Ordinal)), string.Join(", ", caller.ReferencedFunctions));
        AssertNoDiagnostics(model);
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
