using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-219: a materialized view's <c>USING</c> and <c>TABLESPACE</c> were dropped from the deployed statement. They are deployed as written, where the grammar puts
/// them; the tablespace is compared as a table's is (the database's default counts as none), the access method only when the project states one; both are
/// changed in place.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class MaterializedViewTablespaceTests
{
    private const string Table = "CREATE TABLE public.t (a int);";

    [TestMethod]
    public void TheyAreReadAndCreatedAsWritten()
    {
        var model = Build($"{Table} CREATE MATERIALIZED VIEW public.mv USING heap WITH (fillfactor = 70) TABLESPACE ts1 AS SELECT a FROM public.t;");
        var view = model.Views["public.mv"];
        Assert.AreEqual("heap", view.AccessMethod);
        Assert.AreEqual("ts1", view.TablespaceName);

        var script = ScriptFor(DatabaseModel.Empty, model);
        StringAssert.Contains(script, "CREATE MATERIALIZED VIEW \"public\".\"mv\" USING \"heap\" WITH (fillfactor = 70) TABLESPACE \"ts1\" AS\n", script);
    }

    [TestMethod]
    public void AChangedTablespaceAndAccessMethodAreSetInPlace()
    {
        var script = ScriptFor(
            Build($"{Table} CREATE MATERIALIZED VIEW public.mv USING heap TABLESPACE ts1 AS SELECT a FROM public.t;"),
            Build($"{Table} CREATE MATERIALIZED VIEW public.mv USING heap2 TABLESPACE ts2 AS SELECT a FROM public.t;"));

        StringAssert.Contains(script, "ALTER MATERIALIZED VIEW \"public\".\"mv\" SET TABLESPACE \"ts2\";", script);
        StringAssert.Contains(script, "ALTER MATERIALIZED VIEW \"public\".\"mv\" SET ACCESS METHOD \"heap2\";", script);
        Assert.IsFalse(script.Contains("DROP MATERIALIZED VIEW", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void AnAccessMethodTheProjectDoesNotStateIsNotCompared()
    {
        var changes = new DiffEngine().Diff(
            Build($"{Table} CREATE MATERIALIZED VIEW public.mv USING heap2 AS SELECT a FROM public.t;"),
            Build($"{Table} CREATE MATERIALIZED VIEW public.mv AS SELECT a FROM public.t;"));

        Assert.IsEmpty(changes.Modified);
    }

    [TestMethod]
    public void TheDatabasesDefaultTablespaceCountsAsNone()
    {
        var project = Build($"{Table} CREATE MATERIALIZED VIEW public.mv TABLESPACE pg_default AS SELECT a FROM public.t;");
        DefaultTablespace.Apply(project, "pg_default");

        Assert.IsEmpty(new DiffEngine().Diff(Build($"{Table} CREATE MATERIALIZED VIEW public.mv AS SELECT a FROM public.t;"), project).Modified);
    }

    private static string ScriptFor(DatabaseModel oldModel, DatabaseModel newModel)
        => new ScriptGenerator().GenerateSqlScript(new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel)));

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
