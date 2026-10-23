using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-197: <c>CREATE FUNCTION</c>/<c>CREATE PROCEDURE</c> was written without the routine's <c>SET</c>, <c>COST</c>, <c>ROWS</c>, <c>PARALLEL</c>,
/// <c>LEAKPROOF</c>, and <c>WINDOW</c>. They are written now, and a change to any but <c>WINDOW</c> is a <c>CREATE OR REPLACE</c>, which sets every one
/// of them (a clause left out is reset). <c>CREATE OR REPLACE</c> cannot turn a function into a window function or back, so that is a drop and a create.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RoutineOptionsDeployTests
{
    [TestMethod]
    public void EveryOptionIsWrittenIntoTheCreate()
    {
        var sql = Statements(DatabaseModel.Empty, Build("""
            create function public.f(a int, b int default 5) returns setof int language sql immutable leakproof parallel safe cost 5 rows 42
              security definer set search_path = pg_catalog, "My S", "$user" set work_mem = '64MB' set statement_timeout = 5000 set myapp.tenant from current
              as $$ select a + b $$;
            """)).Single();

        StringAssert.Contains(sql, "\nIMMUTABLE\nSECURITY DEFINER\nLEAKPROOF\nPARALLEL SAFE\nCOST 5\nROWS 42\n"
            + "set search_path = pg_catalog, \"My S\", \"$user\"\nset work_mem = '64MB'\nset statement_timeout = 5000\nset myapp.tenant from current\nAS $$");
    }

    [TestMethod]
    public void AProcedureIsWrittenWithItsSettings()
    {
        var sql = Statements(DatabaseModel.Empty, Build("create procedure public.p() language sql security definer set search_path = pg_catalog as $$ select 1 $$;")).Single();

        StringAssert.Contains(sql, "SECURITY DEFINER\nset search_path = pg_catalog\nAS $$");
    }

    [TestMethod]
    public void AWindowFunctionIsWrittenAsOne()
    {
        var sql = Statements(DatabaseModel.Empty, Build("create function public.f(integer) returns integer language c window as 'mod', 'fn';")).Single();

        StringAssert.Contains(sql, "LANGUAGE c\nWINDOW\n");
    }

    [TestMethod]
    [DataRow("set search_path = pg_catalog", "", "LANGUAGE sql\nAS $$")]
    [DataRow("", "set search_path = pg_catalog", "LANGUAGE sql\nset search_path = pg_catalog\nAS $$")]
    [DataRow("parallel safe cost 5", "leakproof", "LANGUAGE sql\nLEAKPROOF\nAS $$")]
    public void AChangedOptionIsACreateOrReplace(string before, string after, string expectedClauses)
    {
        const string f = "create function public.f(x integer) returns integer language sql {0} as $$ select x $$;";

        var statements = Statements(Build(string.Format(f, before)), Build(string.Format(f, after)));

        Assert.HasCount(1, statements, string.Join("\n", statements));
        StringAssert.StartsWith(statements[0], "CREATE OR REPLACE FUNCTION");
        StringAssert.Contains(statements[0], expectedClauses);
    }

    [TestMethod]
    public void BecomingAWindowFunctionIsADropAndACreate()
    {
        const string f = "create function public.f(integer) returns integer language c {0} as 'mod', 'fn';";

        var statements = Statements(Build(string.Format(f, string.Empty)), Build(string.Format(f, "window")));

        Assert.HasCount(2, statements, string.Join("\n", statements));
        StringAssert.StartsWith(statements[0], "DROP FUNCTION");
        StringAssert.Contains(statements[1], "WINDOW");
    }

    private static List<string> Statements(DatabaseModel oldModel, DatabaseModel newModel)
        => new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel)).Operations
            .Where(static operation => operation is not SchemaCreateOperation)
            .Select(static operation => operation.GenerateSql().Trim())
            .ToList();

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
