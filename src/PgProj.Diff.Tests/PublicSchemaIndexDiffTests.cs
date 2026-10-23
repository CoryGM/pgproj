using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-113: the live side reads an index on a <c>public</c> table as <c>pg_get_indexdef</c> prints it, without the schema (<c>ON t</c>).
/// Against the project's qualified index it must be no change, and a real change must still be one.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class PublicSchemaIndexDiffTests
{
    private const string Table = "CREATE TABLE public.t (z int, a int);";

    [TestMethod]
    public void AnIndexReadWithoutItsPublicSchemaIsNoChange()
    {
        var live = Build(Table + " CREATE INDEX t_z_idx ON t USING btree (z);");
        var project = Build(Table + " CREATE INDEX t_z_idx ON public.t (z);");

        var result = new DiffEngine().Execute(live, project);

        Assert.IsEmpty(result.AddedIndexes);
        Assert.IsEmpty(result.RemovedIndexes);
        Assert.IsEmpty(result.ModifiedIndexes);
    }

    [TestMethod]
    public void ARealChangeIsStillReported()
    {
        var live = Build(Table + " CREATE INDEX t_z_idx ON t USING btree (z);");
        var project = Build(Table + " CREATE INDEX t_z_idx ON public.t (a, z);");

        var result = new DiffEngine().Execute(live, project);

        Assert.IsEmpty(result.AddedIndexes);
        Assert.IsEmpty(result.RemovedIndexes);
        Assert.HasCount(1, result.ModifiedIndexes);
        Assert.AreEqual("public.t_z_idx", result.ModifiedIndexes[0].NodeId);
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
