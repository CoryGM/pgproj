using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-262: <c>NULLS NOT DISTINCT</c> on a unique index or a unique constraint (PostgreSQL 15) was dropped without a word, so the published key treated NULLs as distinct.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class NullsNotDistinctTests
{
    private const string Table = "CREATE TABLE public.t (a integer, b integer);";

    [TestMethod]
    public void AUniqueIndexKeepsNullsNotDistinct()
    {
        var script = Script(DatabaseModel.Empty, Build(Table + "CREATE UNIQUE INDEX t_a ON public.t (a) NULLS NOT DISTINCT;"));

        StringAssert.Contains(script, "CREATE UNIQUE INDEX \"t_a\" ON \"public\".\"t\" USING btree (\"a\") NULLS NOT DISTINCT");
    }

    [TestMethod]
    public void AUniqueConstraintKeepsNullsNotDistinct()
    {
        var script = Script(DatabaseModel.Empty, Build(Table + "ALTER TABLE public.t ADD CONSTRAINT t_b_uq UNIQUE NULLS NOT DISTINCT (b);"));

        StringAssert.Contains(script, "UNIQUE NULLS NOT DISTINCT (\"b\")");
    }

    [TestMethod]
    public void AnInlineUniqueConstraintKeepsNullsNotDistinct()
    {
        var script = Script(DatabaseModel.Empty, Build("CREATE TABLE public.t (a integer UNIQUE NULLS NOT DISTINCT);"));

        StringAssert.Contains(script, "UNIQUE NULLS NOT DISTINCT (\"a\")");
    }

    [TestMethod]
    public void NullsNotDistinctGoesBeforeTheStorageOptionsAndThePredicate()
    {
        var script = Script(DatabaseModel.Empty, Build(Table + "CREATE UNIQUE INDEX t_a ON public.t (a) INCLUDE (b) NULLS NOT DISTINCT WITH (fillfactor = 70) WHERE b > 0;"));

        var include = script.IndexOf("INCLUDE", StringComparison.Ordinal);
        var nulls = script.IndexOf("NULLS NOT DISTINCT", StringComparison.Ordinal);
        var with = script.IndexOf("WITH (", StringComparison.Ordinal);
        var where = script.IndexOf("WHERE", StringComparison.Ordinal);
        Assert.IsTrue(include < nulls && nulls < with && with < where, script);
    }

    [TestMethod]
    public void AnOrdinaryUniqueIndexAndConstraintStateNothingExtra()
    {
        var script = Script(DatabaseModel.Empty, Build(Table + "CREATE UNIQUE INDEX t_a ON public.t (a); ALTER TABLE public.t ADD CONSTRAINT t_b_uq UNIQUE (b);"));

        Assert.IsFalse(script.Contains("NULLS", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void ChangingNullsNotDistinctOnAnIndexDropsAndCreatesIt()
    {
        var script = Script(
            Build(Table + "CREATE UNIQUE INDEX t_a ON public.t (a);"),
            Build(Table + "CREATE UNIQUE INDEX t_a ON public.t (a) NULLS NOT DISTINCT;"));

        StringAssert.Contains(script, "DROP INDEX");
        StringAssert.Contains(script, "NULLS NOT DISTINCT");
    }

    [TestMethod]
    public void ChangingNullsNotDistinctOnAConstraintDropsAndAddsIt()
    {
        var script = Script(
            Build(Table + "ALTER TABLE public.t ADD CONSTRAINT t_b_uq UNIQUE NULLS NOT DISTINCT (b);"),
            Build(Table + "ALTER TABLE public.t ADD CONSTRAINT t_b_uq UNIQUE (b);"));

        StringAssert.Contains(script, "DROP CONSTRAINT");
        StringAssert.Contains(script, "UNIQUE (\"b\")");
        Assert.IsFalse(script.Contains("NULLS", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void TheSameSettingOnBothSidesIsNoChange()
    {
        var model = Build(Table + "CREATE UNIQUE INDEX t_a ON public.t (a) NULLS NOT DISTINCT; ALTER TABLE public.t ADD CONSTRAINT t_b_uq UNIQUE NULLS NOT DISTINCT (b);");

        Assert.AreEqual(string.Empty, Script(model, model));
    }

    [TestMethod]
    public void NullsNotDistinctIsNoLongerReportedAsAnUnreadClause()
    {
        var model = Build(Table + "CREATE UNIQUE INDEX t_a ON public.t (a) NULLS NOT DISTINCT;");

        Assert.IsFalse(model.Diagnostics.Any(static d => d.Message.Contains("NULLS NOT DISTINCT", StringComparison.Ordinal)), string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
    }

    private static string Script(DatabaseModel oldModel, DatabaseModel newModel)
    {
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));
        return string.Join("\n", batch.Operations.Where(static operation => operation is not SchemaCreateOperation).Select(static operation => operation.GenerateSql().Replace("\r\n", "\n", StringComparison.Ordinal))).Trim();
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
