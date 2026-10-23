using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-171: a materialized view is the one relation besides a table that owns indexes. Its columns are the view's output columns, which
/// are not modeled, so an index on it is accepted whatever it names, and an index on a relation that does not exist is still reported.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class MaterializedViewIndexTests
{
    private const string Prelude = "CREATE SCHEMA s; CREATE TABLE s.t (grp int, amount numeric); CREATE MATERIALIZED VIEW s.mv AS SELECT grp, sum(amount) AS total, count(*) AS n FROM s.t GROUP BY grp;";

    [TestMethod]
    [DataRow("CREATE UNIQUE INDEX mv_grp ON s.mv (grp);")]
    [DataRow("CREATE INDEX mv_total ON s.mv (total DESC) WHERE n > 1;")]
    [DataRow("CREATE INDEX mv_expr ON s.mv ((total * 2));")]
    public void AnIndexOnAMaterializedViewIsAccepted(string index)
    {
        var model = Build(Prelude + index);

        Assert.IsEmpty(model.Diagnostics, string.Join(" | ", model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.HasCount(1, model.Indexes);
        Assert.AreEqual("mv", model.Indexes.Values.Single().TableName);
    }

    [TestMethod]
    public void AnIndexOnAnUnqualifiedMaterializedViewNameIsFoundInTheSameSchema()
    {
        var model = Build("CREATE SCHEMA s; CREATE TABLE s.t (grp int); CREATE MATERIALIZED VIEW s.mv AS SELECT grp FROM s.t; CREATE INDEX mv_grp ON s.mv (grp);");

        Assert.IsEmpty(model.Diagnostics, string.Join(" | ", model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.IsNotNull(model.FindMaterializedView("s", "mv"));
        Assert.IsNotNull(model.FindMaterializedView(null, "s.mv"));
    }

    [TestMethod]
    public void AnOrdinaryViewIsNotAMaterializedView()
    {
        var model = Build("CREATE SCHEMA s; CREATE TABLE s.t (grp int); CREATE VIEW s.v AS SELECT grp FROM s.t;");

        Assert.IsNull(model.FindMaterializedView("s", "v"));
        Assert.IsNull(model.FindMaterializedView("s", "missing"));
        Assert.IsNull(model.FindMaterializedView("s", null));
    }

    [TestMethod]
    public void AnIndexOnARelationThatDoesNotExistIsStillReported()
    {
        var model = Build(Prelude + "CREATE INDEX nowhere_idx ON s.nowhere (grp);");

        Assert.IsTrue(model.Diagnostics.Any(static diagnostic => diagnostic.Message.Contains("nowhere", StringComparison.Ordinal)), "An index on a missing relation must still be reported.");
    }

    [TestMethod]
    public void AnIndexOnAnOrdinaryViewIsStillReported()
    {
        var model = Build("CREATE SCHEMA s; CREATE TABLE s.t (grp int); CREATE VIEW s.v AS SELECT grp FROM s.t; CREATE INDEX v_grp ON s.v (grp);");

        Assert.IsTrue(model.Diagnostics.Any(static diagnostic => diagnostic.Message.Contains("v_grp", StringComparison.Ordinal)), "PostgreSQL cannot index an ordinary view.");
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
