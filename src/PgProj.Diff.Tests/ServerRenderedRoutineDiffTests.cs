using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-95: PostgreSQL stores a SQL-standard routine body (<c>BEGIN ATOMIC ... END</c>, <c>RETURN expression</c>) parsed, and prints it back
/// qualified and with casts and aliases added. When it has rendered both sides (<see cref="FunctionDefinition.ServerRenderedBody"/>),
/// the diff compares those renderings, as it does for views (G-96); without them, the body is compared as before.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ServerRenderedRoutineDiffTests
{
    private const string Table = "CREATE SCHEMA s; CREATE TABLE s.t (id int, a int);";
    private const string Rendered = "BEGIN ATOMIC\n SELECT\n         CASE\n             WHEN (total.p > 0) THEN sum(t.a)\n             ELSE (0)::bigint\n         END AS \"case\"\n    FROM s.t;\nEND";

    [TestMethod]
    public void SameRenderingIsNoChangeEvenWhenTheWrittenBodiesDiffer()
    {
        var live = WithRendering(BuildModel(Table + "CREATE FUNCTION s.total(p int) RETURNS bigint LANGUAGE sql BEGIN ATOMIC SELECT CASE WHEN (total.p > 0) THEN sum(t.a) ELSE (0)::bigint END AS \"case\" FROM s.t; END;"), Rendered);
        var project = WithRendering(BuildModel(Table + "CREATE FUNCTION s.total(p int) RETURNS bigint LANGUAGE sql BEGIN ATOMIC SELECT CASE WHEN p > 0 THEN sum(a) ELSE 0 END FROM s.t; END;"), Rendered);

        Assert.IsEmpty(new DiffEngine().Execute(live, project).ModifiedFunctions);
    }

    [TestMethod]
    public void DifferentRenderingIsAChange()
    {
        const string Sql = Table + "CREATE FUNCTION s.twice(x int) RETURNS int LANGUAGE sql RETURN x * 2;";
        var live = WithRendering(BuildModel(Sql), "RETURN (x * 2)");
        var project = WithRendering(BuildModel(Sql), "RETURN (x * 3)");

        var modification = new DiffEngine().Execute(live, project).ModifiedFunctions.Single();

        var detail = modification.Details.Single();
        Assert.AreEqual(nameof(FunctionDefinition.ServerRenderedBody), detail.PropertyName);
        Assert.AreEqual("RETURN (x * 2)", detail.OldValue);
        Assert.AreEqual("RETURN (x * 3)", detail.NewValue);
    }

    [TestMethod]
    public void AProcedureIsComparedByItsRenderingToo()
    {
        var live = WithRendering(BuildModel(Table + "CREATE PROCEDURE s.clear() LANGUAGE sql BEGIN ATOMIC DELETE FROM s.t; END;"), "BEGIN ATOMIC\n DELETE FROM s.t;\nEND");
        var same = WithRendering(BuildModel(Table + "CREATE PROCEDURE s.clear() LANGUAGE sql BEGIN ATOMIC delete from s.t; END;"), "BEGIN ATOMIC\n DELETE FROM s.t;\nEND");
        var changed = WithRendering(BuildModel(Table + "CREATE PROCEDURE s.clear() LANGUAGE sql BEGIN ATOMIC DELETE FROM s.t WHERE id > 0; END;"), "BEGIN ATOMIC\n DELETE FROM s.t\n   WHERE (t.id > 0);\nEND");

        Assert.IsEmpty(new DiffEngine().Execute(live, same).ModifiedProcedures);
        Assert.HasCount(1, new DiffEngine().Execute(live, changed).ModifiedProcedures);
    }

    [TestMethod]
    public void ASignatureOrCommentChangeIsStillReportedAlongsideTheRendering()
    {
        var live = WithRendering(BuildModel(Table + "CREATE FUNCTION s.twice(x int) RETURNS int LANGUAGE sql RETURN x * 2;"), "RETURN (x * 2)");
        var project = WithRendering(BuildModel(Table + "CREATE FUNCTION s.twice(x int) RETURNS int LANGUAGE sql IMMUTABLE RETURN x * 2; COMMENT ON FUNCTION s.twice(int) IS 'doubled';"), "RETURN (x * 2)");

        var properties = new DiffEngine().Execute(live, project).ModifiedFunctions.Single().Details.Select(static detail => detail.PropertyName).ToArray();

        CollectionAssert.AreEquivalent(new[] { nameof(FunctionDefinition.Comment), "Signature.Attributes" }, properties);
    }

    [TestMethod]
    public void WithoutARenderingOnBothSidesTheBodyIsComparedAsWritten()
    {
        var live = BuildModel(Table + "CREATE FUNCTION s.twice(x int) RETURNS int LANGUAGE sql RETURN x * 2;");
        var same = WithRendering(BuildModel(Table + "CREATE FUNCTION s.twice(x int) RETURNS int LANGUAGE sql RETURN x * 2;"), "RETURN (x * 2)");
        var changed = BuildModel(Table + "CREATE FUNCTION s.twice(x int) RETURNS int LANGUAGE sql RETURN x * 3;");

        Assert.IsEmpty(new DiffEngine().Execute(live, same).ModifiedFunctions);
        Assert.HasCount(1, new DiffEngine().Execute(live, changed).ModifiedFunctions);
    }

    private static DatabaseModel WithRendering(DatabaseModel model, string rendering)
    {
        model.Functions = model.Functions.ToDictionary(static entry => entry.Key, entry => entry.Value with { ServerRenderedBody = rendering }, StringComparer.OrdinalIgnoreCase);
        model.Procedures = model.Procedures.ToDictionary(static entry => entry.Key, entry => entry.Value with { ServerRenderedBody = rendering }, StringComparer.OrdinalIgnoreCase);
        return model;
    }

    private static DatabaseModel BuildModel(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
