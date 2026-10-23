using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-252: on PostgreSQL 16 and 17 a foreign key to a partitioned table also makes one constraint per partition, named with the first free number, so the explicit foreign keys of a scaffolded
/// project (<c>fk_a_fkey</c>, <c>fk_a_fkey4</c>, <c>fk_a_fkey12</c>) have to be created in the order of their numbers.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ForeignKeyNumberOrderTests
{
    private const string Tables = """
        CREATE TABLE public.pk (a integer PRIMARY KEY) PARTITION BY LIST (a);
        CREATE TABLE public.pk1 PARTITION OF public.pk FOR VALUES IN (1);
        CREATE TABLE public.fk (a integer);
        """;

    [TestMethod]
    public void ForeignKeysToAPartitionedTableAreCreatedInTheOrderOfTheirNumbers()
    {
        var script = Script(Build(Tables + """
            ALTER TABLE public.fk ADD CONSTRAINT fk_a_fkey12 FOREIGN KEY (a) REFERENCES public.pk (a);
            ALTER TABLE public.fk ADD CONSTRAINT fk_a_fkey4 FOREIGN KEY (a) REFERENCES public.pk (a);
            ALTER TABLE public.fk ADD CONSTRAINT fk_a_fkey FOREIGN KEY (a) REFERENCES public.pk (a);
            """));

        var plain = script.IndexOf("\"fk_a_fkey\"", StringComparison.Ordinal);
        var four = script.IndexOf("\"fk_a_fkey4\"", StringComparison.Ordinal);
        var twelve = script.IndexOf("\"fk_a_fkey12\"", StringComparison.Ordinal);
        Assert.IsTrue(plain >= 0 && plain < four && four < twelve, script);
    }

    [TestMethod]
    public void ForeignKeysToAnOrdinaryTableAreNotOrderedByNumber()
    {
        var script = Script(Build("""
            CREATE TABLE public.pk (a integer PRIMARY KEY);
            CREATE TABLE public.fk (a integer);
            ALTER TABLE public.fk ADD CONSTRAINT fk_a_fkey12 FOREIGN KEY (a) REFERENCES public.pk (a);
            ALTER TABLE public.fk ADD CONSTRAINT fk_a_fkey4 FOREIGN KEY (a) REFERENCES public.pk (a);
            """));

        // Nothing makes a clone, so the usual order (by name) stands.
        Assert.IsTrue(script.IndexOf("\"fk_a_fkey12\"", StringComparison.Ordinal) < script.IndexOf("\"fk_a_fkey4\"", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void ForeignKeysOfDifferentStemsOrDifferentTablesAreNotChained()
    {
        var model = Build(Tables + """
            CREATE TABLE public.fk2 (b integer);
            ALTER TABLE public.fk ADD CONSTRAINT fk_a_fkey3 FOREIGN KEY (a) REFERENCES public.pk (a);
            ALTER TABLE public.fk ADD CONSTRAINT other_fkey2 FOREIGN KEY (a) REFERENCES public.pk (a);
            ALTER TABLE public.fk2 ADD CONSTRAINT fk_a_fkey1 FOREIGN KEY (b) REFERENCES public.pk (a);
            """);

        var graph = new DependencyAnalyzer().Analyze(DatabaseModel.Empty, model);
        var three = graph.Nodes.Values.Single(static node => node.NodeId.EndsWith("fk_a_fkey3", StringComparison.Ordinal) && node.NodeId.Contains(".fk.", StringComparison.Ordinal));
        Assert.IsFalse(three.ReferencedNodeIds.Any(static id => id.EndsWith("other_fkey2", StringComparison.Ordinal) || id.EndsWith("fk_a_fkey1", StringComparison.Ordinal)), string.Join(",", three.ReferencedNodeIds));
    }

    private static string Script(DatabaseModel newModel)
    {
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(DatabaseModel.Empty, newModel), new DependencyAnalyzer().Analyze(DatabaseModel.Empty, newModel));
        return string.Join("\n", batch.Operations.Where(static operation => operation is not SchemaCreateOperation).Select(static operation => operation.GenerateSql().Replace("\r\n", "\n", StringComparison.Ordinal))).Trim();
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
