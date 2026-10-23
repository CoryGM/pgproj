using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core.Tests;

/// <summary>
/// G-97: the parts of <see cref="ServerGeneratedExpressionRenderer"/> that need no server: the guard that keeps anything but a
/// generation expression out of <c>GENERATED ALWAYS AS (...)</c>, and attaching a live column's own rendering. The server round
/// trip is covered by <c>PgProj.Integration.Tests/GeneratedColumnConvergenceEndToEndTests</c>.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ServerGeneratedExpressionRendererTests
{
    [TestMethod]
    [DataRow("qty * price")]
    [DataRow("name || '!'")]
    [DataRow("CASE WHEN qty IN (1, 2) THEN 1 ELSE 0 END")]
    [DataRow("qty > 0 AND price BETWEEN 1 AND 9")]
    public void AGenerationExpressionIsAccepted(string expression)
        => Assert.IsTrue(new ServerGeneratedExpressionRenderer(new PgQueryParser()).IsGeneratedExpression(expression));

    [TestMethod]
    [DataRow("1) STORED); DROP TABLE s.t; --")]
    [DataRow("1) STORED, d integer GENERATED ALWAYS AS (2")]
    [DataRow("qty *")]
    [DataRow("")]
    public void AnythingButAGenerationExpressionIsRejectedBeforeReachingTheServer(string expression)
        => Assert.IsFalse(new ServerGeneratedExpressionRenderer(new PgQueryParser()).IsGeneratedExpression(expression));

    [TestMethod]
    public void EachLiveGeneratedColumnGetsItsOwnExpression()
    {
        var model = new SemanticBuilder().Build([new PgQueryParser().Parse("""
            CREATE SCHEMA s;
            CREATE TABLE s.t (qty integer, price numeric, total numeric GENERATED ALWAYS AS (((qty)::numeric * price)) STORED, d integer DEFAULT 1);
            """)]);

        ServerGeneratedExpressionRenderer.AttachLiveRenderings(model,
        [
            new LiveTableSnapshot("s", "t",
            [
                new LiveColumnSnapshot("qty", "integer", true, null, false, null, null),
                new LiveColumnSnapshot("price", "numeric", true, null, false, null, null),
                new LiveColumnSnapshot("total", "numeric", true, null, true, "((qty)::numeric * price)", null),
                new LiveColumnSnapshot("d", "integer", true, "1", false, null, null),
            ]),
        ]);

        var columns = model.Tables["s.t"].Columns.ToDictionary(static column => column.Name);
        Assert.AreEqual("((qty)::numeric * price)", columns["total"].ServerRenderedGeneratedExpression);
        Assert.IsNull(columns["d"].ServerRenderedGeneratedExpression, "A default is not a generation expression.");
        Assert.IsNull(columns["qty"].ServerRenderedGeneratedExpression);
    }
}
