using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core.Tests;

/// <summary>
/// G-96: the parts of <see cref="ServerViewRenderer"/> that need no server — the guard that keeps anything but one view
/// body out of <c>CREATE TEMPORARY VIEW</c>, and attaching a live view's own rendering. The server round trip itself is
/// covered by <c>PgProj.Integration.Tests/ViewConvergenceEndToEndTests</c>.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ServerViewRendererTests
{
    [TestMethod]
    [DataRow("SELECT id, name FROM public.c WHERE id > 5")]
    [DataRow("SELECT * FROM s.c")]
    [DataRow("SELECT c.id, (SELECT count(*) FROM public.o WHERE o.cid = c.id) AS n FROM public.c c")]
    [DataRow("WITH x AS (SELECT 1 AS a) SELECT a FROM x")]
    [DataRow("SELECT 1 AS a UNION ALL SELECT 2")]
    public void ASingleQueryIsAcceptedAsAViewBody(string body)
        => Assert.IsTrue(new ServerViewRenderer(new PgQueryParser()).IsSingleViewBody(body));

    [TestMethod]
    [DataRow("SELECT 1; DROP TABLE public.c")]
    [DataRow("SELECT 1; COMMIT")]
    [DataRow("SELECT 1 AS a; CREATE TABLE public.x (id int)")]
    [DataRow("SELECT FROM WHERE")]
    [DataRow("")]
    public void AnythingButASingleQueryIsRejectedBeforeReachingTheServer(string body)
        => Assert.IsFalse(new ServerViewRenderer(new PgQueryParser()).IsSingleViewBody(body));

    [TestMethod]
    public void NormalizeRenderingTrimsPostgresLeadingSpaceAndTrailingSemicolon()
        => Assert.AreEqual("SELECT id\n   FROM c", ServerViewRenderer.NormalizeRendering(" SELECT id\n   FROM c;"));

    [TestMethod]
    public void EachLiveViewGetsItsOwnRenderingBySchemaAndName()
    {
        var model = new SemanticBuilder().Build([new PgQueryParser().Parse("""
            CREATE SCHEMA s;
            CREATE TABLE public.c (id int);
            CREATE TABLE s.c (id int);
            CREATE VIEW public.v AS SELECT id FROM public.c;
            CREATE VIEW s.v AS SELECT id FROM s.c;
            CREATE MATERIALIZED VIEW s.mv AS SELECT id FROM s.c;
            """)]);

        ServerViewRenderer.AttachLiveRenderings(model,
        [
            new LiveViewSnapshot("public", "v", " SELECT id\n   FROM c;"),
            new LiveViewSnapshot("s", "v", " SELECT id\n   FROM s.c;"),
        ]);

        Assert.AreEqual("SELECT id\n   FROM c", model.Views["public.v"].ServerRenderedBody);
        Assert.AreEqual("SELECT id\n   FROM s.c", model.Views["s.v"].ServerRenderedBody);
        Assert.IsNull(model.Views["s.mv"].ServerRenderedBody, "A view with no snapshot gets no rendering.");
    }
}
