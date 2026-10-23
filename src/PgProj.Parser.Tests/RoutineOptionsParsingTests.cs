namespace PgProj.Parser.Tests;

/// <summary>
/// G-197: a routine's <c>SET</c>, <c>COST</c>, <c>ROWS</c>, <c>PARALLEL</c>, <c>LEAKPROOF</c>, and <c>WINDOW</c> were not read, so they were neither
/// deployed nor compared: a <c>SECURITY DEFINER</c> function lost its pinned <c>search_path</c>. They are now part of the signature, both as
/// a project writes them and as <c>pg_get_functiondef</c> prints them.
/// </summary>
[TestClass]
public sealed class RoutineOptionsParsingTests
{
    [TestMethod]
    public void TheIssuesFunctionKeepsEveryOption()
    {
        var signature = ParseFunction("""
            create function f(a int, b int default 5) returns int language sql immutable leakproof parallel safe cost 5
              security definer set search_path = pg_catalog, public as $$ select a + b $$;
            """);

        Assert.IsTrue(signature.IsSecurityDefiner);
        Assert.IsTrue(signature.IsLeakproof);
        Assert.AreEqual("safe", signature.Parallel);
        Assert.AreEqual("5", signature.Cost);
        Assert.IsNull(signature.Rows);
        Assert.IsFalse(signature.IsWindow);
        Assert.AreEqual("search_path=pg_catalog|public", Settings(signature));
    }

    [TestMethod]
    public void TheFormPgGetFunctiondefPrintsIsReadTheSameWay()
    {
        var signature = ParseFunction("""
            CREATE OR REPLACE FUNCTION public.f(a integer, b integer DEFAULT 5)
             RETURNS integer
             LANGUAGE sql
             IMMUTABLE PARALLEL SAFE LEAKPROOF SECURITY DEFINER COST 5
             SET search_path TO 'pg_catalog', 'public'
            AS $function$ select a + b $function$
            """);

        Assert.IsTrue(signature.IsLeakproof);
        Assert.AreEqual("safe", signature.Parallel);
        Assert.AreEqual("5", signature.Cost);
        Assert.AreEqual("search_path=pg_catalog|public", Settings(signature));
    }

    [TestMethod]
    public void SettingValuesAreTextWhateverTheyWereWrittenAs()
    {
        var signature = ParseFunction("""
            create function f() returns int language sql
              set work_mem = '64MB' set statement_timeout = 5000 set extra_float_digits = -1 set enable_seqscan = off
              set search_path = "My S", "$user" set myapp.Tenant = 'x' set cursor_tuple_fraction = 0.5 set search_path to default
              as $$ select 1 $$;
            """);

        Assert.AreEqual(
            "work_mem=64MB statement_timeout=5000 extra_float_digits=-1 enable_seqscan=off search_path=My S|$user myapp.tenant=x cursor_tuple_fraction=0.5",
            Settings(signature));
    }

    [TestMethod]
    public void FromCurrentHasNoValue()
    {
        var setting = ParseFunction("create function f() returns int language sql set work_mem from current as $$ select 1 $$;").Settings.Single();

        Assert.AreEqual("work_mem", setting.Name);
        Assert.IsTrue(setting.IsFromCurrent);
        Assert.IsEmpty(setting.Values);
    }

    [TestMethod]
    [DataRow("cost 7.5 rows 42", "7.5", "42")]
    [DataRow("cost 0 rows 0", "0", "0")]
    [DataRow("", null, null)]
    public void CostAndRowsAreKeptAsWritten(string clauses, string? cost, string? rows)
    {
        var signature = ParseFunction($"create function f() returns setof int language sql {clauses} as $$ select 1 $$;");

        Assert.AreEqual(cost, signature.Cost);
        Assert.AreEqual(rows, signature.Rows);
    }

    [TestMethod]
    [DataRow("not leakproof parallel unsafe", false, "unsafe")]
    [DataRow("PARALLEL RESTRICTED", false, "restricted")]
    [DataRow("", false, null)]
    public void NotLeakproofAndParallelAreRead(string clauses, bool leakproof, string? parallel)
    {
        var signature = ParseFunction($"create function f() returns int language sql {clauses} as $$ select 1 $$;");

        Assert.AreEqual(leakproof, signature.IsLeakproof);
        Assert.AreEqual(parallel, signature.Parallel);
    }

    [TestMethod]
    public void WindowIsRead()
    {
        Assert.IsTrue(ParseFunction("create function f(int) returns int language c window as 'mod', 'fn';").IsWindow);
    }

    [TestMethod]
    public void AProcedureKeepsItsSettings()
    {
        var signature = new PgQueryParser().Parse("create procedure p() language sql security definer set search_path = pg_catalog as $$ select 1 $$;")
            .Procedures.Single().Signature!;

        Assert.AreEqual("search_path=pg_catalog", Settings(signature));
    }

    [TestMethod]
    public void ASettingIsRenderedAsStringLiteralsWithItsNameQuotedWhereNeeded()
    {
        Assert.AreEqual("SET search_path TO 'My S', '$user', 'it''s'", new RoutineSetting("search_path", ["My S", "$user", "it's"], IsFromCurrent: false).Render());
        Assert.AreEqual("SET myapp.\"Tenant\" TO 'x'", new RoutineSetting("myapp.Tenant", ["x"], IsFromCurrent: false).Render());
        Assert.AreEqual("SET work_mem FROM CURRENT", new RoutineSetting("work_mem", [], IsFromCurrent: true).Render());
    }

    private static string Settings(RoutineSignature signature)
        => string.Join(" ", signature.Settings.Select(static setting => setting.Name + "=" + string.Join("|", setting.Values)));

    private static RoutineSignature ParseFunction(string sql)
        => new PgQueryParser().Parse(sql).Functions.Single().Signature!;
}
