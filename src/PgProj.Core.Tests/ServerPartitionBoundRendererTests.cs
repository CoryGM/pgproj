using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core.Tests;

/// <summary>
/// G-101: the parts of <see cref="ServerPartitionBoundRenderer"/> that need no server: the guard that keeps anything but a
/// partition bound out of <c>CREATE TEMPORARY TABLE ... PARTITION OF</c>, and attaching a live partition's own rendering.
/// The server round trip is covered by <c>PgProj.Integration.Tests/PartitionConvergenceEndToEndTests</c>.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ServerPartitionBoundRendererTests
{
    [TestMethod]
    [DataRow("FOR VALUES FROM ('2026-01-01') TO ('2027-01-01')")]
    [DataRow("FOR VALUES FROM (MINVALUE) TO ('2026-01-01')")]
    [DataRow("FOR VALUES FROM ('2026-01-01', 0) TO ('2026-12-31', 1.5)")]
    [DataRow("FOR VALUES IN ('a', 'b')")]
    [DataRow("FOR VALUES WITH (MODULUS 2, REMAINDER 0)")]
    [DataRow("DEFAULT")]
    public void APartitionBoundIsAccepted(string bound)
        => Assert.IsTrue(new ServerPartitionBoundRenderer(new PgQueryParser()).IsPartitionBound(bound));

    [TestMethod]
    [DataRow("DEFAULT; DROP TABLE s.log")]
    [DataRow("FOR VALUES IN (1); COMMIT")]
    [DataRow("FOR VALUES IN (")]
    [DataRow("")]
    public void AnythingButAPartitionBoundIsRejectedBeforeReachingTheServer(string bound)
        => Assert.IsFalse(new ServerPartitionBoundRenderer(new PgQueryParser()).IsPartitionBound(bound));

    [TestMethod]
    public void EachLivePartitionGetsItsOwnBoundBySchemaAndName()
    {
        var model = new SemanticBuilder().Build([new PgQueryParser().Parse("""
            CREATE SCHEMA s;
            CREATE TABLE s.log (at timestamptz NOT NULL) PARTITION BY RANGE (at);
            CREATE TABLE s.log_2026 PARTITION OF s.log FOR VALUES FROM ('2026-01-01 00:00:00+00') TO ('2027-01-01 00:00:00+00');
            CREATE TABLE public.plain (id int);
            """)]);

        ServerPartitionBoundRenderer.AttachLiveRenderings(model,
        [
            new LiveTableSnapshot("s", "log", [], PartitionKeyDef: "RANGE (at)"),
            new LiveTableSnapshot("s", "log_2026", [], PartitionOfSchemaName: "s", PartitionOfTableName: "log", PartitionBoundRaw: "FOR VALUES FROM ('2026-01-01 00:00:00+00') TO ('2027-01-01 00:00:00+00')"),
            new LiveTableSnapshot("public", "plain", []),
        ]);

        Assert.AreEqual("FOR VALUES FROM ('2026-01-01 00:00:00+00') TO ('2027-01-01 00:00:00+00')", model.Tables["s.log_2026"].ServerRenderedPartitionBound);
        Assert.IsNull(model.Tables["s.log"].ServerRenderedPartitionBound, "A partitioned parent has no bound.");
        Assert.IsNull(model.Tables["public.plain"].ServerRenderedPartitionBound);
    }
}
