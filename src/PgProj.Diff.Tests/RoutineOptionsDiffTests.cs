using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-197: a routine's <c>SET</c>, <c>COST</c>, <c>ROWS</c>, <c>PARALLEL</c>, <c>LEAKPROOF</c>, and <c>WINDOW</c> are compared. A real change in either
/// direction is reported; the same routine as a project writes it and as <c>pg_get_functiondef</c> prints it is not, and neither is a clause that
/// states PostgreSQL's default.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RoutineOptionsDiffTests
{
    private const string F = "create function public.f(x integer) returns integer language sql {0} as $$ select x $$;";
    private const string S = "create function public.g() returns setof integer language sql {0} as $$ select 1 $$;";

    [TestMethod]
    [DataRow("", "set search_path = pg_catalog", DisplayName = "SET added")]
    [DataRow("set search_path = pg_catalog, public", "set search_path = public", DisplayName = "SET value changed")]
    [DataRow("set search_path = a, b", "set search_path = b, a", DisplayName = "search_path order changed")]
    [DataRow("set work_mem = '64MB'", "set work_mem from current", DisplayName = "SET value to FROM CURRENT is not a change")]
    [DataRow("", "leakproof", DisplayName = "LEAKPROOF added")]
    [DataRow("", "parallel safe", DisplayName = "PARALLEL added")]
    [DataRow("parallel safe", "parallel restricted", DisplayName = "PARALLEL changed")]
    [DataRow("", "cost 5", DisplayName = "COST added")]
    [DataRow("cost 5", "cost 6", DisplayName = "COST changed")]
    public void AChangedOptionIsReportedBothWays(string before, string after)
    {
        var expected = after.Contains("from current", StringComparison.Ordinal) ? 0 : 1;
        Assert.AreEqual(expected, ModifiedCount(string.Format(F, before), string.Format(F, after)), $"{before} -> {after}");
        Assert.AreEqual(expected, ModifiedCount(string.Format(F, after), string.Format(F, before)), $"{after} -> {before}");
    }

    [TestMethod]
    [DataRow("", "rows 42")]
    [DataRow("rows 42", "rows 10")]
    public void AChangedRowsIsReportedBothWays(string before, string after)
    {
        Assert.AreEqual(1, ModifiedCount(string.Format(S, before), string.Format(S, after)));
        Assert.AreEqual(1, ModifiedCount(string.Format(S, after), string.Format(S, before)));
    }

    [TestMethod]
    public void ASettingIsReportedAsTheConfiguration()
    {
        var modified = new DiffEngine().Diff(
            Build(string.Format(F, "set search_path = pg_catalog, public")),
            Build(string.Format(F, string.Empty))).Modified.Single();

        var detail = modified.Details.Single(static detail => detail.PropertyName == "Signature.Configuration");
        Assert.AreEqual("search_path = pg_catalog, public", detail.OldValue);
        Assert.AreEqual(string.Empty, detail.NewValue);
    }

    [TestMethod]
    public void AProcedureSettingIsReported()
    {
        Assert.AreEqual(1, ModifiedCount(
            "create procedure public.p() language sql security definer set search_path = pg_catalog as $$ select 1 $$;",
            "create procedure public.p() language sql security definer as $$ select 1 $$;"));
    }

    [TestMethod]
    public void TheProjectsFormAndTheServersFormAreTheSameRoutine()
    {
        const string project = """
            create function public.f(a int, b int default 5) returns int language sql immutable leakproof parallel safe cost 5
              security definer set search_path = pg_catalog, "My S", "$user" set work_mem = '64MB' set statement_timeout = 5000
              as $$ select a + b $$;
            """;
        const string server = """
            CREATE OR REPLACE FUNCTION public.f(a integer, b integer DEFAULT 5)
             RETURNS integer
             LANGUAGE sql
             IMMUTABLE PARALLEL SAFE LEAKPROOF SECURITY DEFINER COST 5
             SET search_path TO 'pg_catalog', 'My S', '$user'
             SET work_mem TO '64MB'
             SET statement_timeout TO '5000'
            AS $function$ select a + b $function$;
            """;

        Assert.AreEqual(0, ChangeCount(project, server));
        Assert.AreEqual(0, ChangeCount(server, project));
    }

    [TestMethod]
    public void SettingsInADifferentOrderAreNotAChange()
    {
        Assert.AreEqual(0, ModifiedCount(
            string.Format(F, "set work_mem = '64MB' set search_path = public"),
            string.Format(F, "set search_path = public set work_mem = '64MB'")));
    }

    [TestMethod]
    [DataRow("parallel unsafe", DisplayName = "PARALLEL UNSAFE")]
    [DataRow("not leakproof", DisplayName = "NOT LEAKPROOF")]
    [DataRow("cost 100", DisplayName = "COST 100")]
    [DataRow("cost 100.0", DisplayName = "COST 100.0")]
    [DataRow("set search_path to default", DisplayName = "SET ... TO DEFAULT")]
    public void AClauseStatingTheDefaultIsNotAChange(string clause)
    {
        Assert.AreEqual(0, ModifiedCount(string.Format(F, string.Empty), string.Format(F, clause)));
    }

    [TestMethod]
    public void TheDefaultRowsOfASetReturningFunctionIsNotAChange()
    {
        Assert.AreEqual(0, ModifiedCount(string.Format(S, string.Empty), string.Format(S, "rows 1000")));
    }

    [TestMethod]
    public void TheDefaultCostOfACFunctionIsOne()
    {
        const string c = "create function public.f(integer) returns integer language c {0} as 'mod', 'fn';";

        Assert.AreEqual(0, ModifiedCount(string.Format(c, string.Empty), string.Format(c, "cost 1")));
        Assert.AreEqual(1, ModifiedCount(string.Format(c, string.Empty), string.Format(c, "cost 100")));
    }

    [TestMethod]
    public void ACostIsComparedAsTheRealPostgreSqlStores()
    {
        // procost is a real, printed with six significant digits: 1234567.89 comes back as 1.23457e+06.
        Assert.AreEqual(0, ModifiedCount(string.Format(F, "cost 1234567.89"), string.Format(F, "cost 1.23457e+06")));
    }

    [TestMethod]
    public void WindowIsReported()
    {
        const string c = "create function public.f(integer) returns integer language c {0} as 'mod', 'fn';";

        Assert.AreEqual(1, ModifiedCount(string.Format(c, string.Empty), string.Format(c, "window")));
    }

    private static int ModifiedCount(string oldSql, string newSql)
        => new DiffEngine().Diff(Build(oldSql), Build(newSql)).Modified.Count;

    private static int ChangeCount(string oldSql, string newSql)
    {
        var changes = new DiffEngine().Diff(Build(oldSql), Build(newSql));
        return changes.Added.Count + changes.Removed.Count + changes.Modified.Count;
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
