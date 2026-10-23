using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-297: a partition the project writes as a standalone <c>CREATE TABLE</c> and attaches later (how <c>pg_dump</c> writes one) keeps the column order it was written in, which
/// <c>PARTITION OF</c> cannot give it (it takes its parent's). It is created with its columns and attached, as G-264 does for NOT NULL names.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class AttachedPartitionColumnOrderTests
{
    private const string Parent = "CREATE TABLE public.parted (a integer NOT NULL, b text) PARTITION BY RANGE (a);\n";

    [TestMethod]
    public void APartitionWithItsOwnColumnOrderIsCreatedWithItsColumnsAndAttached()
    {
        var script = Script(Parent + """
            CREATE TABLE public.parted_1 (b text, a integer NOT NULL);
            ALTER TABLE ONLY public.parted ATTACH PARTITION public.parted_1 FOR VALUES FROM (1) TO (10);
            """);

        var child = script[script.IndexOf("CREATE TABLE \"public\".\"parted_1\"", StringComparison.Ordinal)..];
        Assert.DoesNotContain("PARTITION OF", child);
        Assert.IsLessThan(child.IndexOf("\"a\"", StringComparison.Ordinal), child.IndexOf("\"b\"", StringComparison.Ordinal), "b is listed before a, as written");
        StringAssert.Contains(child, "ALTER TABLE \"public\".\"parted\" ATTACH PARTITION \"public\".\"parted_1\" FOR VALUES FROM (1) TO (10);");
    }

    [TestMethod]
    public void APartitionWithItsParentsColumnOrderIsStillPartitionOf()
    {
        var script = Script(Parent + """
            CREATE TABLE public.parted_1 (a integer NOT NULL, b text);
            ALTER TABLE ONLY public.parted ATTACH PARTITION public.parted_1 FOR VALUES FROM (1) TO (10);
            """);

        StringAssert.Contains(script, "CREATE TABLE \"public\".\"parted_1\" PARTITION OF \"public\".\"parted\" FOR VALUES FROM (1) TO (10);");
    }

    [TestMethod]
    public void ColumnOrderIsNotAChangeToTheDiff()
    {
        var reordered = Build(Parent + "CREATE TABLE public.parted_1 (b text, a integer NOT NULL);\nALTER TABLE ONLY public.parted ATTACH PARTITION public.parted_1 FOR VALUES FROM (1) TO (10);");
        var inherited = Build(Parent + "CREATE TABLE public.parted_1 PARTITION OF public.parted FOR VALUES FROM (1) TO (10);");

        var diff = new DiffEngine().Diff(inherited, reordered);
        Assert.AreEqual(0, diff.Added.Count + diff.Removed.Count + diff.Modified.Count, "column order stays out of the comparison (2026-09-24)");
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);

    private static string Script(string sql)
    {
        var project = Build(sql);
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(DatabaseModel.Empty, project), new DependencyAnalyzer().Analyze(DatabaseModel.Empty, project));
        return string.Join("\n", batch.Operations.Where(static operation => operation is not SchemaCreateOperation).Select(static operation => operation.GenerateSql().Replace("\r\n", "\n", StringComparison.Ordinal))).Trim();
    }
}
