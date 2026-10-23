using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-101: when PostgreSQL has rendered both sides of a partition's bound (<see cref="TableDefinition.ServerRenderedPartitionBound"/>),
/// the diff compares those renderings, so a literal PostgreSQL rewrites in the key's type is no change, and a real change to
/// the bound still is. Without a rendering on both sides, the bounds are compared as written.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ServerRenderedPartitionBoundDiffTests
{
    private const string Parent = "CREATE SCHEMA s; CREATE TABLE s.log (at timestamptz NOT NULL) PARTITION BY RANGE (at);";

    [TestMethod]
    public void SameRenderingIsNoChangeEvenWhenTheWrittenBoundsDiffer()
    {
        var live = WithRendering(Build("FOR VALUES FROM ('2026-01-01 00:00:00+00') TO ('2027-01-01 00:00:00+00')"), "FOR VALUES FROM ('2026-01-01 00:00:00+00') TO ('2027-01-01 00:00:00+00')");
        var project = WithRendering(Build("FOR VALUES FROM ('2026-01-01') TO ('2027-01-01')"), "FOR VALUES FROM ('2026-01-01 00:00:00+00') TO ('2027-01-01 00:00:00+00')");

        var result = new DiffEngine().Execute(live, project);

        Assert.IsEmpty(result.ModifiedTables);
    }

    [TestMethod]
    public void DifferentRenderingIsAChange()
    {
        var live = WithRendering(Build("FOR VALUES FROM ('2026-01-01') TO ('2027-01-01')"), "FOR VALUES FROM ('2026-01-01 00:00:00+00') TO ('2027-01-01 00:00:00+00')");
        var project = WithRendering(Build("FOR VALUES FROM ('2026-01-01') TO ('2028-01-01')"), "FOR VALUES FROM ('2026-01-01 00:00:00+00') TO ('2028-01-01 00:00:00+00')");

        var result = new DiffEngine().Execute(live, project);

        Assert.HasCount(1, result.ModifiedTables);
        var detail = result.ModifiedTables[0].Details.Single(static detail => detail.PropertyName == nameof(TableDefinition.PartitionBoundRaw));
        Assert.AreEqual("FOR VALUES FROM ('2026-01-01 00:00:00+00') TO ('2028-01-01 00:00:00+00')", detail.NewValue);
    }

    [TestMethod]
    public void WithoutARenderingOnBothSidesTheBoundsAreComparedAsWritten()
    {
        var live = Build("FOR VALUES FROM ('2026-01-01 00:00:00+00') TO ('2027-01-01 00:00:00+00')");
        var project = WithRendering(Build("FOR VALUES FROM ('2026-01-01') TO ('2027-01-01')"), "FOR VALUES FROM ('2026-01-01 00:00:00+00') TO ('2027-01-01 00:00:00+00')");

        Assert.HasCount(1, new DiffEngine().Execute(live, project).ModifiedTables);
        Assert.IsEmpty(new DiffEngine().Execute(live, Build("FOR VALUES FROM ('2026-01-01 00:00:00+00') TO ('2027-01-01 00:00:00+00')")).ModifiedTables);
    }

    private static DatabaseModel Build(string bound)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(Parent + $"CREATE TABLE s.log_2026 PARTITION OF s.log {bound};")]);

    private static DatabaseModel WithRendering(DatabaseModel model, string rendering)
    {
        model.Tables = model.Tables.ToDictionary(
            static entry => entry.Key,
            entry => entry.Value.PartitionOfTableName is null ? entry.Value : entry.Value with { ServerRenderedPartitionBound = rendering },
            StringComparer.OrdinalIgnoreCase);
        return model;
    }
}
