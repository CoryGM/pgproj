using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core.Tests;

/// <summary>
/// G-95: the parts of <see cref="ServerRoutineBodyRenderer"/> that need no server: the guard that keeps anything but one routine with a
/// SQL-standard body away from the server, and giving a live routine its own body as its rendering. The server round trip itself is covered
/// by <c>PgProj.Integration.Tests/SqlStandardRoutineBodyEndToEndTests</c>.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ServerRoutineBodyRendererTests
{
    [TestMethod]
    [DataRow("CREATE FUNCTION pg_temp.f(p int) RETURNS int LANGUAGE sql BEGIN ATOMIC SELECT p; SELECT 2; END")]
    [DataRow("CREATE FUNCTION pg_temp.f(x int) RETURNS int LANGUAGE sql RETURN x * 2")]
    [DataRow("CREATE PROCEDURE pg_temp.p() LANGUAGE sql BEGIN ATOMIC DELETE FROM s.t; END")]
    public void OneRoutineWithASqlStandardBodyIsAccepted(string sql)
        => Assert.IsTrue(new ServerRoutineBodyRenderer(new PgQueryParser()).IsSingleRoutineStatement(sql));

    [TestMethod]
    [DataRow("CREATE FUNCTION pg_temp.f() RETURNS int LANGUAGE sql RETURN 1; DROP TABLE s.t")]
    [DataRow("CREATE FUNCTION pg_temp.f() RETURNS int LANGUAGE sql BEGIN ATOMIC SELECT 1; END; COMMIT")]
    [DataRow("CREATE FUNCTION pg_temp.f() RETURNS int LANGUAGE sql AS 'SELECT 1'")]
    [DataRow("CREATE FUNCTION pg_temp.f() RETURNS int LANGUAGE sql BEGIN ATOMIC SELECT 1;")]
    [DataRow("")]
    public void AnythingElseIsRejectedBeforeReachingTheServer(string sql)
        => Assert.IsFalse(new ServerRoutineBodyRenderer(new PgQueryParser()).IsSingleRoutineStatement(sql));

    [TestMethod]
    public void EachLiveSqlStandardRoutineGetsItsBodyAsItsRendering()
    {
        var model = new SemanticBuilder().Build([new PgQueryParser().Parse("""
            CREATE SCHEMA s;
            CREATE TABLE s.t (id int);
            CREATE OR REPLACE FUNCTION s.twice(x integer)
             RETURNS integer
             LANGUAGE sql
             IMMUTABLE
            RETURN (x * 2);
            CREATE FUNCTION s.one() RETURNS int LANGUAGE sql AS $$ SELECT 1 $$;
            CREATE OR REPLACE PROCEDURE s.clear()
             LANGUAGE sql
            BEGIN ATOMIC
             DELETE FROM s.t;
            END;
            """)]);

        ServerRoutineBodyRenderer.AttachLiveRenderings(model);

        Assert.AreEqual("RETURN (x * 2)", model.Functions.Values.Single(static function => function.Name == "twice").ServerRenderedBody);
        Assert.IsNull(model.Functions.Values.Single(static function => function.Name == "one").ServerRenderedBody);
        Assert.AreEqual("BEGIN ATOMIC\n DELETE FROM s.t;\nEND", model.Procedures.Values.Single().ServerRenderedBody?.Replace("\r\n", "\n", StringComparison.Ordinal));
    }
}
