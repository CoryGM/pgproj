using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core.Tests;

/// <summary>
/// G-172: the parts of <see cref="ServerCheckConstraintRenderer"/> that need no server: the guard that keeps anything but one expression
/// out of <c>ADD CONSTRAINT ... CHECK (...)</c>, and attaching a live constraint's own rendering. The server round trip is covered by
/// <c>PgProj.Integration.Tests/CheckConstraintConvergenceEndToEndTests</c>.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ServerCheckConstraintRendererTests
{
    [TestMethod]
    [DataRow("status <> 'cancelled'")]
    [DataRow("status IN ('new', 'cancelled')")]
    [DataRow("(cid > 0)")]
    [DataRow("(status = 'new') = (cid IS NULL)")]
    public void ACheckExpressionIsAccepted(string expression)
        => Assert.IsTrue(new ServerCheckConstraintRenderer(new PgQueryParser()).IsCheckExpression(expression));

    [TestMethod]
    [DataRow("true); DROP TABLE s.o; --")]
    [DataRow("true); COMMIT; SELECT (1")]
    [DataRow("cid >")]
    [DataRow("")]
    public void AnythingButACheckExpressionIsRejectedBeforeReachingTheServer(string expression)
        => Assert.IsFalse(new ServerCheckConstraintRenderer(new PgQueryParser()).IsCheckExpression(expression));

    [TestMethod]
    public void EachLiveCheckGetsItsOwnExpressionBySchemaTableAndName()
    {
        var model = new SemanticBuilder().Build([new PgQueryParser().Parse("""
            CREATE SCHEMA s;
            CREATE TABLE s.o (id int, other int);
            CREATE TABLE s.p (id int);
            ALTER TABLE s.o ADD CONSTRAINT c1 CHECK (id > 0);
            ALTER TABLE s.o ADD CONSTRAINT c2 CHECK (other > 0);
            ALTER TABLE s.p ADD CONSTRAINT c1 CHECK (id < 0);
            ALTER TABLE s.o ADD CONSTRAINT u1 UNIQUE (id);
            """)]);

        ServerCheckConstraintRenderer.AttachLiveRenderings(model,
        [
            new LiveConstraintSnapshot("s", "o", "c1", "CHECK (id > 0)", CheckExpression: "(id > 0)"),
            new LiveConstraintSnapshot("s", "o", "c2", "CHECK (other > 0)", CheckExpression: "(other > 0)"),
            new LiveConstraintSnapshot("s", "p", "c1", "CHECK (id < 0)", CheckExpression: "(id < 0)"),
            new LiveConstraintSnapshot("s", "o", "u1", "UNIQUE (id)"),
        ]);

        Assert.AreEqual("(id > 0)", model.Constraints["s.o.c1"].ServerRenderedCheck);
        Assert.AreEqual("(other > 0)", model.Constraints["s.o.c2"].ServerRenderedCheck);
        Assert.AreEqual("(id < 0)", model.Constraints["s.p.c1"].ServerRenderedCheck, "Two tables may each have a constraint of the same name.");
        Assert.IsNull(model.Constraints["s.o.u1"].ServerRenderedCheck, "A constraint that is not a check gets no rendering.");
    }
}
