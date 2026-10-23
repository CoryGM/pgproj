using PgProj.Dependencies;
using PgProj.Deploy;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Engine.Tests;

/// <summary>
/// G-280 (option (b), the user's decision): <c>publish --execute</c> runs the script in one transaction, which keeps every relation it changes locked until it
/// commits, so a very large schema failed partway with "out of shared memory". The locks are estimated before anything runs; over the server's lock table the
/// script is refused unless <c>--allow-lock-limit</c>, and from 80% of it <c>publish</c> warns.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class LockCapacityTests
{
    private static readonly LockTableSize Defaults = new(64, 100, 0);

    [TestMethod]
    public void ANewTableIsItselfItsToastTableAndItsToastIndex()
    {
        Assert.AreEqual(3, Estimate(string.Empty, "CREATE TABLE public.t (id int, note text);"));
    }

    [TestMethod]
    public void AKeyAddsItsIndexAndAnIndexOnAPartitionedTableAddsOneOnEachPartition()
    {
        var plain = Estimate("CREATE TABLE public.t (id int);", "CREATE TABLE public.t (id int); CREATE INDEX t_id ON public.t (id);");
        var partitioned = Estimate(
            """
            CREATE TABLE public.p (id int) PARTITION BY RANGE (id);
            CREATE TABLE public.p1 PARTITION OF public.p FOR VALUES FROM (0) TO (10);
            CREATE TABLE public.p2 PARTITION OF public.p FOR VALUES FROM (10) TO (20);
            """,
            """
            CREATE TABLE public.p (id int) PARTITION BY RANGE (id);
            CREATE TABLE public.p1 PARTITION OF public.p FOR VALUES FROM (0) TO (10);
            CREATE TABLE public.p2 PARTITION OF public.p FOR VALUES FROM (10) TO (20);
            CREATE INDEX p_id ON public.p (id);
            """);

        Assert.AreEqual(2, plain, "the index and its table");
        Assert.AreEqual(6, partitioned, "the index and the parent, and each partition with its copy of the index");
    }

    [TestMethod]
    public void ANewKeyOnATableIsTheTableAndTheKeysIndex()
    {
        Assert.AreEqual(2, Estimate("CREATE TABLE public.t (id int);", "CREATE TABLE public.t (id int, CONSTRAINT t_pk PRIMARY KEY (id));"));
        Assert.AreEqual(2, Estimate("CREATE TABLE public.t (id int);", "CREATE TABLE public.t (id int, CONSTRAINT t_id_key UNIQUE (id));"));
    }

    [TestMethod]
    public void ANewForeignKeyLocksTheTableItReferences()
    {
        Assert.AreEqual(2, Estimate(
            "CREATE TABLE public.a (id int PRIMARY KEY); CREATE TABLE public.b (a_id int);",
            "CREATE TABLE public.a (id int PRIMARY KEY); CREATE TABLE public.b (a_id int REFERENCES public.a (id));"));
    }

    [TestMethod]
    public void ADroppedTableTakesItsIndexesWithIt()
    {
        Assert.AreEqual(4, Estimate("CREATE TABLE public.t (id int); CREATE INDEX t_id ON public.t (id);", string.Empty));
    }

    [TestMethod]
    public void NothingToDoIsNoLock()
    {
        const string sql = "CREATE TABLE public.t (id int); CREATE FUNCTION public.f() RETURNS int LANGUAGE sql AS $$ SELECT 1 $$;";

        Assert.AreEqual(0, Estimate(sql, sql));
    }

    [TestMethod]
    public void TheLimitIsTheServersFormula()
    {
        Assert.AreEqual(6400, Defaults.Locks);
        Assert.AreEqual(6464, new LockTableSize(64, 100, 1).Locks);
    }

    [TestMethod]
    public void OverTheLimitIsRefusedAndNearItIsWarned()
    {
        Assert.IsTrue(LockCapacity.IsOver(6401, Defaults));
        Assert.IsFalse(LockCapacity.IsOver(6400, Defaults));
        Assert.IsTrue(LockCapacity.IsNear(5120, Defaults), "80% of the table");
        Assert.IsFalse(LockCapacity.IsNear(5119, Defaults));
    }

    [TestMethod]
    public void TheMessageNamesTheEstimateTheSettingsAndAValueThatFits()
    {
        var over = LockCapacity.Describe(9000, Defaults);
        var near = LockCapacity.Describe(5500, Defaults);

        StringAssert.Contains(over, "about 9,000 locks");
        StringAssert.Contains(over, "max_locks_per_transaction 64 * (max_connections 100 + max_prepared_transactions 0) = 6,400");
        StringAssert.Contains(over, "out of shared memory");
        StringAssert.Contains(over, "at least 113");
        StringAssert.Contains(near, "86% of the server's lock table");
        Assert.IsTrue((over + near).All(static character => character < 128), "the console gets ASCII (G-285)");
    }

    private static int Estimate(string liveSql, string projectSql)
    {
        var live = Build(liveSql);
        var project = Build(projectSql);
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(live, project), new DependencyAnalyzer().Analyze(live, project));
        return LockCapacity.Estimate(batch, live, project);
    }

    private static DatabaseModel Build(string sql)
        => string.IsNullOrEmpty(sql) ? DatabaseModel.Empty : new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
