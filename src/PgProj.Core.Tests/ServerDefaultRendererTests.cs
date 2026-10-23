using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core.Tests;

/// <summary>
/// G-102: the parts of <see cref="ServerDefaultRenderer"/> that need no server: the guard that keeps anything but a column
/// default out of <c>CREATE TEMPORARY TABLE ... DEFAULT</c>, and attaching a live column's own rendering. The server round trip
/// is covered by <c>PgProj.Integration.Tests/DefaultConvergenceEndToEndTests</c>.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ServerDefaultRendererTests
{
    [TestMethod]
    [DataRow("0")]
    [DataRow("0::numeric")]
    [DataRow("'{}'::text[]")]
    [DataRow("'new'::s.st")]
    [DataRow("nextval('s.seq'::regclass)")]
    [DataRow("now()")]
    [DataRow("(now() AT TIME ZONE 'utc')")]
    [DataRow("CASE WHEN true THEN 1 ELSE 2 END")]
    public void AColumnDefaultIsAccepted(string expression)
        => Assert.IsTrue(new ServerDefaultRenderer(new PgQueryParser()).IsDefaultExpression(expression));

    [TestMethod]
    [DataRow("0); DROP TABLE s.t; --")]
    [DataRow("0, d integer")]
    [DataRow("0; COMMIT")]
    [DataRow("0 +")]
    [DataRow("")]
    public void AnythingButAColumnDefaultIsRejectedBeforeReachingTheServer(string expression)
        => Assert.IsFalse(new ServerDefaultRenderer(new PgQueryParser()).IsDefaultExpression(expression));

    [TestMethod]
    public void EachLiveColumnGetsItsOwnDefaultByTableAndName()
    {
        var model = new SemanticBuilder().Build([new PgQueryParser().Parse("""
            CREATE SCHEMA s;
            CREATE TABLE s.t (a numeric DEFAULT (0)::numeric, b integer, g integer GENERATED ALWAYS AS (1) STORED);
            CREATE TABLE s.u (a numeric DEFAULT 1);
            """)]);

        ServerDefaultRenderer.AttachLiveRenderings(model,
        [
            new LiveTableSnapshot("s", "t",
            [
                new LiveColumnSnapshot("a", "numeric", true, "(0)::numeric", false, null, null),
                new LiveColumnSnapshot("b", "integer", true, null, false, null, null),
                new LiveColumnSnapshot("g", "integer", true, null, true, "1", null),
            ]),
            new LiveTableSnapshot("s", "u", [new LiveColumnSnapshot("a", "numeric", true, "1", false, null, null)]),
        ]);

        var t = model.Tables["s.t"].Columns.ToDictionary(static column => column.Name);
        Assert.AreEqual("(0)::numeric", t["a"].ServerRenderedDefault);
        Assert.IsNull(t["b"].ServerRenderedDefault, "A column without a default gets no rendering.");
        Assert.IsNull(t["g"].ServerRenderedDefault, "A generated column's expression is not a default.");
        Assert.AreEqual("1", model.Tables["s.u"].Columns.Single().ServerRenderedDefault, "Same column name, other table.");
    }
}
