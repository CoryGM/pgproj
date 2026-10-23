using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-24c (Phase 4): a partitioned parent's <c>PARTITION BY</c> and a partition's <c>PARTITION OF parent ... FOR VALUES</c>/
/// <c>DEFAULT</c> are deployed. Two risks flagged while scoping this gap turned out to need no new code, each confirmed with a
/// test rather than assumed: creating a parent before its partitions, and dropping every partition before its parent (so a
/// removed partition is never dropped a second time after its parent's drop already removed it, <c>42P01</c>), both already
/// fall out of the existing dependency-ordered create/drop phases in <see cref="ScriptGenerator"/>.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class TablePartitioningDeployTests
{
    [TestMethod]
    public void APartitionedParentIsCreatedWithItsKey()
    {
        var statements = Statements(DatabaseModel.Empty, Build("CREATE TABLE public.p (id integer, d date) PARTITION BY RANGE (d);"));

        var create = statements.Single(static s => s.StartsWith("CREATE TABLE", StringComparison.Ordinal));
        StringAssert.Contains(create, "PARTITION BY RANGE (d)");
    }

    [TestMethod]
    public void APartitionIsCreatedWithNoColumnListAndItsBound()
    {
        var statements = Statements(
            DatabaseModel.Empty,
            Build(
                "CREATE TABLE public.p (id integer, d date) PARTITION BY RANGE (d);"
                + " CREATE TABLE public.p_2024 PARTITION OF public.p FOR VALUES FROM ('2024-01-01') TO ('2025-01-01');"));

        var childCreate = statements.Single(static s => s.Contains("p_2024", StringComparison.Ordinal) && s.StartsWith("CREATE TABLE", StringComparison.Ordinal));
        Assert.AreEqual(
            "CREATE TABLE \"public\".\"p_2024\" PARTITION OF \"public\".\"p\" FOR VALUES FROM ('2024-01-01') TO ('2025-01-01');",
            childCreate.Split('\n')[0]);
        Assert.IsFalse(childCreate.Contains("(\n", StringComparison.Ordinal), "A partition's own DDL must not list columns.");
    }

    [TestMethod]
    public void ADefaultPartitionIsCreatedWithDefault()
    {
        var statements = Statements(
            DatabaseModel.Empty,
            Build(
                "CREATE TABLE public.p (id integer, cat text) PARTITION BY LIST (cat);"
                + " CREATE TABLE public.p_default PARTITION OF public.p DEFAULT;"));

        StringAssert.Contains(
            statements.Single(static s => s.Contains("p_default", StringComparison.Ordinal) && s.StartsWith("CREATE TABLE", StringComparison.Ordinal)),
            "PARTITION OF \"public\".\"p\" DEFAULT;");
    }

    [TestMethod]
    public void AMultiColumnKeyIsDeployedInOrder()
    {
        var statements = Statements(DatabaseModel.Empty, Build("CREATE TABLE public.p (a int, b int) PARTITION BY RANGE (a, b);"));

        StringAssert.Contains(statements.Single(static s => s.StartsWith("CREATE TABLE", StringComparison.Ordinal)), "PARTITION BY RANGE (a, b)");
    }

    [TestMethod]
    public void ASubPartitionedChildIsCreatedWithItsOwnKeyToo()
    {
        var statements = Statements(
            DatabaseModel.Empty,
            Build(
                "CREATE TABLE public.p (id integer, d date) PARTITION BY RANGE (d);"
                + " CREATE TABLE public.p1 PARTITION OF public.p FOR VALUES FROM ('2024-01-01') TO ('2025-01-01') PARTITION BY RANGE (d);"));

        var childCreate = statements.Single(static s => s.Contains("p1", StringComparison.Ordinal) && s.StartsWith("CREATE TABLE", StringComparison.Ordinal));
        StringAssert.Contains(childCreate, "PARTITION OF \"public\".\"p\" FOR VALUES FROM ('2024-01-01') TO ('2025-01-01')");
        StringAssert.Contains(childCreate, "PARTITION BY RANGE (d)");
    }

    [TestMethod]
    public void UnloggedStorageOptionsAndTablespaceStillWorkAlongsidePartitionBy()
    {
        var statements = Statements(DatabaseModel.Empty, Build("CREATE UNLOGGED TABLE public.p (id integer, d date) PARTITION BY RANGE (d) WITH (fillfactor=70) TABLESPACE fastspace;"));

        var create = statements.Single(static s => s.StartsWith("CREATE UNLOGGED TABLE", StringComparison.Ordinal));
        StringAssert.Contains(create, "PARTITION BY RANGE (d)");
        StringAssert.Contains(create, "WITH (fillfactor=70)");
        StringAssert.Contains(create, "TABLESPACE \"fastspace\"");
    }

    [TestMethod]
    public void AParentIsCreatedBeforeItsPartitions()
    {
        var statements = Statements(
            DatabaseModel.Empty,
            Build(
                "CREATE TABLE public.p (id integer, d date) PARTITION BY RANGE (d);"
                + " CREATE TABLE public.p_2024 PARTITION OF public.p FOR VALUES FROM ('2024-01-01') TO ('2025-01-01');"
                + " CREATE TABLE public.p_default PARTITION OF public.p DEFAULT;"));

        var parentIndex = statements.FindIndex(static s => s.StartsWith("CREATE TABLE \"public\".\"p\"", StringComparison.Ordinal) && s.Contains("PARTITION BY", StringComparison.Ordinal));
        var childIndex = statements.FindIndex(static s => s.Contains("p_2024", StringComparison.Ordinal));
        var defaultIndex = statements.FindIndex(static s => s.Contains("p_default", StringComparison.Ordinal));
        Assert.IsLessThan(childIndex, parentIndex, string.Join("\n", statements));
        Assert.IsLessThan(defaultIndex, parentIndex, string.Join("\n", statements));
    }

    [TestMethod]
    public void RemovingAWholeHierarchyDropsEachPartitionOnceBeforeItsParentWithNoRedundantDrop()
    {
        var statements = Statements(
            Build(
                "CREATE TABLE public.p (id integer, d date) PARTITION BY RANGE (d);"
                + " CREATE TABLE public.p_2024 PARTITION OF public.p FOR VALUES FROM ('2024-01-01') TO ('2025-01-01');"
                + " CREATE TABLE public.p_default PARTITION OF public.p DEFAULT;"),
            DatabaseModel.Empty);

        var dropStatements = statements.Where(static s => s.StartsWith("DROP TABLE", StringComparison.Ordinal)).ToArray();
        Assert.HasCount(3, dropStatements, string.Join("\n", dropStatements));
        CollectionAssert.AllItemsAreUnique(dropStatements);
        var parentIndex = statements.FindIndex(static s => s == "DROP TABLE \"public\".\"p\";");
        var childIndex = statements.FindIndex(static s => s == "DROP TABLE \"public\".\"p_2024\";");
        var defaultIndex = statements.FindIndex(static s => s == "DROP TABLE \"public\".\"p_default\";");
        Assert.IsLessThan(parentIndex, childIndex, string.Join("\n", statements));
        Assert.IsLessThan(parentIndex, defaultIndex, string.Join("\n", statements));
    }

    [TestMethod]
    public void ARemovedSinglePartitionIsJustItsOwnDrop()
    {
        var statements = Statements(
            Build(
                "CREATE TABLE public.p (id integer, d date) PARTITION BY RANGE (d);"
                + " CREATE TABLE public.p_2024 PARTITION OF public.p FOR VALUES FROM ('2024-01-01') TO ('2025-01-01');"
                + " CREATE TABLE public.p_default PARTITION OF public.p DEFAULT;"),
            Build(
                "CREATE TABLE public.p (id integer, d date) PARTITION BY RANGE (d);"
                + " CREATE TABLE public.p_default PARTITION OF public.p DEFAULT;"));

        Assert.AreEqual("DROP TABLE \"public\".\"p_2024\";", string.Join("\n", statements));
    }

    [TestMethod]
    public void AChangedPartitionStrategyBlocksExecutionInsteadOfDropAndRecreate()
    {
        var oldModel = Build("CREATE TABLE public.p (id integer, d date) PARTITION BY RANGE (d);");
        var newModel = Build("CREATE TABLE public.p (id integer, d date) PARTITION BY LIST (d);");
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));

        var script = string.Join("\n", batch.Operations.Select(static operation => operation.GenerateSql()));
        StringAssert.Contains(script, "-- TODO: Table \"public\".\"p\"'s partition key changed.");
        Assert.IsFalse(script.Contains("DROP TABLE", StringComparison.Ordinal));
        Assert.IsNotEmpty(batch.GetUnsupportedOperations().ToArray());
    }

    [TestMethod]
    public void AChangedPartitionKeyBlocksExecution()
    {
        var oldModel = Build("CREATE TABLE public.p (a int, b int) PARTITION BY RANGE (a, b);");
        var newModel = Build("CREATE TABLE public.p (a int, b int) PARTITION BY RANGE (b, a);");
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));

        Assert.IsNotEmpty(batch.GetUnsupportedOperations().ToArray());
    }

    [TestMethod]
    public void AChangedBoundBlocksExecutionInsteadOfSilentlyIgnoringIt()
    {
        var oldModel = Build(
            "CREATE TABLE public.p (id integer, d date) PARTITION BY RANGE (d);"
            + " CREATE TABLE public.p1 PARTITION OF public.p FOR VALUES FROM ('2024-01-01') TO ('2025-01-01');");
        var newModel = Build(
            "CREATE TABLE public.p (id integer, d date) PARTITION BY RANGE (d);"
            + " CREATE TABLE public.p1 PARTITION OF public.p FOR VALUES FROM ('2024-01-01') TO ('2026-01-01');");
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));

        var script = string.Join("\n", batch.Operations.Select(static operation => operation.GenerateSql()));
        StringAssert.Contains(script, "-- TODO: Table \"public\".\"p1\"'s partition parent or bound changed.");
        Assert.IsNotEmpty(batch.GetUnsupportedOperations().ToArray());
    }

    [TestMethod]
    public void ABoundPostgresRewroteIsNotAChangedBoundWhenThePartitionChangesForAnotherReason()
    {
        // G-101: the partition is modified (its comment changed); its bound is the same once both sides are PostgreSQL's
        // rendering, so the alter must not carry the blocking "bound changed" placeholder.
        var oldModel = WithBoundRendering(Build(
            "CREATE TABLE public.p (id integer, d timestamptz) PARTITION BY RANGE (d);"
            + " CREATE TABLE public.p1 PARTITION OF public.p FOR VALUES FROM ('2024-01-01 00:00:00+00') TO ('2025-01-01 00:00:00+00');"));
        var newModel = WithBoundRendering(Build(
            "CREATE TABLE public.p (id integer, d timestamptz) PARTITION BY RANGE (d);"
            + " CREATE TABLE public.p1 PARTITION OF public.p FOR VALUES FROM ('2024-01-01') TO ('2025-01-01');"
            + " COMMENT ON TABLE public.p1 IS 'first year';"));
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));

        var script = string.Join("\n", batch.Operations.Select(static operation => operation.GenerateSql()));
        Assert.IsFalse(script.Contains("partition parent or bound changed", StringComparison.Ordinal), script);
        StringAssert.Contains(script, "COMMENT ON TABLE");
        Assert.IsEmpty(batch.GetUnsupportedOperations().ToArray());
    }

    [TestMethod]
    public void ARepartitionedTableBlocksExecution()
    {
        var oldModel = Build(
            "CREATE TABLE public.p (id integer, d date) PARTITION BY RANGE (d);"
            + " CREATE TABLE public.p1 PARTITION OF public.p FOR VALUES FROM ('2024-01-01') TO ('2025-01-01');");
        var newModel = Build(
            "CREATE TABLE public.p (id integer, d date) PARTITION BY RANGE (d);"
            + " CREATE TABLE public.q (id integer, d date) PARTITION BY RANGE (d);"
            + " CREATE TABLE public.p1 PARTITION OF public.q FOR VALUES FROM ('2024-01-01') TO ('2025-01-01');");
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));

        Assert.IsNotEmpty(batch.GetUnsupportedOperations().ToArray());
    }

    private static List<string> Statements(DatabaseModel oldModel, DatabaseModel newModel)
    {
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));
        return batch.Operations
            .Where(static operation => operation is not SchemaCreateOperation)
            .Select(static operation => operation.GenerateSql().Trim().Replace("\r\n", "\n", StringComparison.Ordinal))
            .ToList();
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);

    /// <summary>Gives every partition the rendering the server would give its bound on a <c>timestamptz</c> key (G-101).</summary>
    private static DatabaseModel WithBoundRendering(DatabaseModel model)
    {
        model.Tables = model.Tables.ToDictionary(
            static entry => entry.Key,
            static entry => entry.Value.PartitionOfTableName is null
                ? entry.Value
                : entry.Value with { ServerRenderedPartitionBound = "FOR VALUES FROM ('2024-01-01 00:00:00+00') TO ('2025-01-01 00:00:00+00')" },
            StringComparer.OrdinalIgnoreCase);
        return model;
    }
}
