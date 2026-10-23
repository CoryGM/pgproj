using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core.Tests;

/// <summary>
/// G-100: the parts of <see cref="ServerIndexPredicateRenderer"/> that need no server: the guard that keeps anything but an index
/// predicate out of <c>CREATE INDEX ... WHERE</c>, and attaching a live index's own rendering. The server round trip is covered by
/// <c>PgProj.Integration.Tests/IndexPredicateConvergenceEndToEndTests</c>.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ServerIndexPredicateRendererTests
{
    [TestMethod]
    [DataRow("status <> 'cancelled'")]
    [DataRow("d > '2026-01-01' AND note IS NOT NULL")]
    [DataRow("status IN ('new', 'cancelled')")]
    [DataRow("(cid > 0)")]
    public void AnIndexPredicateIsAccepted(string predicate)
        => Assert.IsTrue(new ServerIndexPredicateRenderer(new PgQueryParser()).IsIndexPredicate(predicate));

    [TestMethod]
    [DataRow("true; DROP TABLE s.o")]
    [DataRow("true; COMMIT")]
    [DataRow("cid >")]
    [DataRow("")]
    public void AnythingButAnIndexPredicateIsRejectedBeforeReachingTheServer(string predicate)
        => Assert.IsFalse(new ServerIndexPredicateRenderer(new PgQueryParser()).IsIndexPredicate(predicate));

    [TestMethod]
    public void EachLivePartialIndexGetsItsOwnPredicateBySchemaAndName()
    {
        var model = new SemanticBuilder().Build([new PgQueryParser().Parse("""
            CREATE SCHEMA s;
            CREATE TABLE s.o (id int, status text);
            CREATE INDEX i1 ON s.o (id) WHERE status <> 'x'::text;
            CREATE INDEX i2 ON s.o (id);
            """)]);

        ServerIndexPredicateRenderer.AttachLiveRenderings(model,
        [
            new LiveIndexSnapshot("s", "o", "i1", "CREATE INDEX i1 ON s.o USING btree (id) WHERE (status <> 'x'::text)", Predicate: "(status <> 'x'::text)"),
            new LiveIndexSnapshot("s", "o", "i2", "CREATE INDEX i2 ON s.o USING btree (id)"),
        ]);

        Assert.AreEqual("(status <> 'x'::text)", model.Indexes["s.i1"].ServerRenderedPredicate);
        Assert.IsNull(model.Indexes["s.i2"].ServerRenderedPredicate, "An index without a predicate gets no rendering.");
    }
}
