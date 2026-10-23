using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-130: an aggregate is created after the functions and types it uses and before what calls it, and dropped in the opposite order; it is written with every option; a change is
/// a drop and a create around what the live database records as using it; and one that only disappears is a destructive change.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class AggregateDeployTests
{
    private const string Head = "CREATE FUNCTION public.step(s integer, x integer) RETURNS integer LANGUAGE sql AS $$ SELECT s $$; ";

    private const string Aggregate = "CREATE AGGREGATE public.mysum (integer) (SFUNC = public.step, STYPE = integer, INITCOND = '0');";

    [TestMethod]
    public void AnAggregateIsCreatedAfterItsFunctionAndBeforeTheViewThatCallsIt()
    {
        var statements = Statements(DatabaseModel.Empty, Build("CREATE VIEW public.v AS SELECT public.mysum(n) AS total FROM public.t; CREATE TABLE public.t (n integer); " + Aggregate + Head));

        var functionAt = statements.FindIndex(static s => s.StartsWith("CREATE OR REPLACE FUNCTION \"public\".\"step\"", StringComparison.Ordinal));
        var aggregateAt = statements.FindIndex(static s => s.StartsWith("CREATE AGGREGATE", StringComparison.Ordinal));
        var viewAt = statements.FindIndex(static s => s.StartsWith("CREATE OR REPLACE VIEW", StringComparison.Ordinal));
        Assert.IsTrue(functionAt >= 0 && aggregateAt > functionAt && viewAt > aggregateAt, string.Join("\n---\n", statements));
    }

    [TestMethod]
    public void EveryOptionIsWritten()
    {
        var statements = Statements(
            DatabaseModel.Empty,
            Build("CREATE AGGREGATE app.a (integer, text[]) (SFUNC = sf, STYPE = int8[], SSPACE = 64, FINALFUNC = ff, FINALFUNC_EXTRA, FINALFUNC_MODIFY = READ_WRITE, COMBINEFUNC = cf, SERIALFUNC = sr, DESERIALFUNC = ds, "
                + "INITCOND = '{0,0}', MSFUNC = msf, MINVFUNC = mif, MSTYPE = int8, MSSPACE = 10, MFINALFUNC = mff, MFINALFUNC_EXTRA, MINITCOND = '0', SORTOP = <, PARALLEL = SAFE);"));

        var sql = statements.Single(static s => s.StartsWith("CREATE AGGREGATE", StringComparison.Ordinal));
        StringAssert.StartsWith(sql, "CREATE AGGREGATE \"app\".\"a\" (integer, text[]) (");
        foreach (var expected in new[]
        {
            "SFUNC = \"sf\"", "STYPE = int8[]", "SSPACE = 64", "FINALFUNC = \"ff\"", "FINALFUNC_EXTRA", "FINALFUNC_MODIFY = READ_WRITE", "COMBINEFUNC = \"cf\"",
            "INITCOND = '{0,0}'", "MSTYPE = int8", "MSSPACE = 10", "MFINALFUNC_EXTRA", "MINITCOND = '0'", "SORTOP = OPERATOR(<)", "PARALLEL = SAFE",
        })
        {
            StringAssert.Contains(sql, expected);
        }
    }

    [TestMethod]
    public void AStarAnOrderedSetAndAVariadicAggregateAreWritten()
    {
        var statements = Statements(
            DatabaseModel.Empty,
            Build("CREATE AGGREGATE public.c (*) (SFUNC = int8inc, STYPE = bigint); "
                + "CREATE AGGREGATE public.o (float8 ORDER BY float8) (SFUNC = array_append, STYPE = float8[], FINALFUNC = g); "
                + "CREATE AGGREGATE public.h (VARIADIC \"any\" ORDER BY VARIADIC \"any\") (SFUNC = f, STYPE = internal, HYPOTHETICAL);"));

        var script = string.Join("\n", statements);
        StringAssert.Contains(script, "CREATE AGGREGATE \"public\".\"c\" (*)");
        StringAssert.Contains(script, "CREATE AGGREGATE \"public\".\"o\" (float8 ORDER BY float8)");
        StringAssert.Contains(script, "CREATE AGGREGATE \"public\".\"h\" (VARIADIC \"any\" ORDER BY VARIADIC \"any\")");
        StringAssert.Contains(script, "HYPOTHETICAL");
    }

    [TestMethod]
    public void AChangedAggregateIsDroppedAndCreatedAgainAndIsNotDestructive()
    {
        var oldModel = Build(Head + Aggregate);
        var newModel = Build(Head + Aggregate.Replace("INITCOND = '0'", "INITCOND = '1'", StringComparison.Ordinal));
        var batch = Generate(oldModel, newModel);

        var script = string.Join("\n", Sql(batch));
        StringAssert.Contains(script, "DROP AGGREGATE \"public\".\"mysum\" (integer);");
        StringAssert.Contains(script, "INITCOND = '1'");
        Assert.IsEmpty(batch.GetDestructiveOperations());
    }

    [TestMethod]
    public void ARemovedAggregateIsDroppedBeforeItsFunctionAndIsDestructive()
    {
        var oldModel = Build(Head + Aggregate);
        var newModel = Build("CREATE TABLE public.keep (a integer);");
        var batch = Generate(oldModel, newModel);

        var statements = Sql(batch);
        var aggregateAt = statements.FindIndex(static s => s.StartsWith("DROP AGGREGATE", StringComparison.Ordinal));
        var functionAt = statements.FindIndex(static s => s.StartsWith("DROP FUNCTION", StringComparison.Ordinal));
        Assert.IsTrue(aggregateAt >= 0 && functionAt > aggregateAt, string.Join("\n---\n", statements));
        Assert.IsTrue(batch.GetDestructiveOperations().Any(static d => d.Reasons.Any(static reason => reason.StartsWith("aggregate dropped", StringComparison.Ordinal))));
    }

    [TestMethod]
    public void AViewThatCallsAChangedAggregateIsTakenOffAndPutBack()
    {
        var users = "CREATE TABLE public.t (n integer); CREATE VIEW public.v AS SELECT public.mysum(n) AS total FROM public.t;";
        var live = Build(Head + Aggregate + users);
        live.LiveDependencies = [new LiveDependency("view", "public.v", DatabaseModel.CreateRoutineKey("public", "mysum", "integer"), null, "routine")];

        var statements = Statements(live, Build(Head + Aggregate.Replace("INITCOND = '0'", "INITCOND = '1'", StringComparison.Ordinal) + users));

        var dropView = statements.FindIndex(static s => s.StartsWith("DROP VIEW", StringComparison.Ordinal));
        var dropAggregate = statements.FindIndex(static s => s.StartsWith("DROP AGGREGATE", StringComparison.Ordinal));
        var createAggregate = statements.FindIndex(static s => s.StartsWith("CREATE AGGREGATE", StringComparison.Ordinal));
        var createView = statements.FindIndex(static s => s.StartsWith("CREATE OR REPLACE VIEW", StringComparison.Ordinal));
        Assert.IsTrue(dropView >= 0 && dropAggregate > dropView && createAggregate > dropAggregate && createView > createAggregate, string.Join("\n---\n", statements));
    }

    [TestMethod]
    public void TheSameAggregateWrittenTwoWaysDeploysNothing()
    {
        Assert.IsEmpty(Statements(
            Build(Head + Aggregate),
            Build(Head + "CREATE AGGREGATE mysum (pg_catalog.int4) (STYPE = int4, SFUNC = step, INITCOND = '0', PARALLEL = UNSAFE);")));
    }

    private static OperationBatch Generate(DatabaseModel oldModel, DatabaseModel newModel)
        => new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));

    private static List<string> Sql(OperationBatch batch)
        => batch.Operations
            .Where(static operation => operation is not SchemaCreateOperation)
            .Select(static operation => operation.GenerateSql().Trim().Replace("\r\n", "\n", StringComparison.Ordinal))
            .ToList();

    private static List<string> Statements(DatabaseModel oldModel, DatabaseModel newModel) => Sql(Generate(oldModel, newModel));

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
