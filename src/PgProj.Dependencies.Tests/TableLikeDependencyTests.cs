using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Dependencies.Tests;

/// <summary>
/// G-221: a table written with <c>LIKE</c> is created after its source, and after what of the source the options copy, since each of those is its own
/// statement: the <c>CHECK</c>s for <c>INCLUDING CONSTRAINTS</c>, the keys, exclusion constraints, and indexes for <c>INCLUDING INDEXES</c>, the statistics for
/// <c>INCLUDING STATISTICS</c>.
/// </summary>
[TestClass]
public sealed class TableLikeDependencyTests
{
    private const string Source = "CREATE TABLE a (id int PRIMARY KEY, n text, CONSTRAINT a_ck CHECK (id > 0)); CREATE INDEX a_n ON a (n); CREATE STATISTICS a_st ON id, n FROM a; ";

    [TestMethod]
    public void IncludingAllDependsOnTheSourceAndEverythingItCopies()
    {
        var dependencies = DependenciesOf(Source + "CREATE TABLE b (LIKE a INCLUDING ALL);", "public.b");

        CollectionAssert.IsSubsetOf(new[] { "public.a", "public.a.a_ck", "public.a.a_pkey", "public.a_n", "statistics:public.a_st" }, dependencies);
    }

    [TestMethod]
    public void APlainLikeDependsOnlyOnTheSource()
    {
        var dependencies = DependenciesOf(Source + "CREATE TABLE b (LIKE a);", "public.b");

        CollectionAssert.Contains(dependencies, "public.a");
        CollectionAssert.DoesNotContain(dependencies, "public.a.a_ck");
        CollectionAssert.DoesNotContain(dependencies, "public.a_n");
        CollectionAssert.DoesNotContain(dependencies, "statistics:public.a_st");
    }

    [TestMethod]
    public void EachOptionAddsOnlyWhatItCopies()
    {
        var constraints = DependenciesOf(Source + "CREATE TABLE b (LIKE a INCLUDING CONSTRAINTS);", "public.b");
        var indexes = DependenciesOf(Source + "CREATE TABLE b (LIKE a INCLUDING INDEXES);", "public.b");

        CollectionAssert.Contains(constraints, "public.a.a_ck");
        CollectionAssert.DoesNotContain(constraints, "public.a.a_pkey");
        CollectionAssert.IsSubsetOf(new[] { "public.a.a_pkey", "public.a_n" }, indexes);
        CollectionAssert.DoesNotContain(indexes, "public.a.a_ck");
    }

    [TestMethod]
    public void TheTableIsSortedAfterTheSourcesConstraints()
    {
        var order = new DependencyAnalyzer().Analyze(Build(Source + "CREATE TABLE b (LIKE a INCLUDING ALL);")).TopologicalSort().Select(static node => node.NodeId).ToList();

        Assert.IsTrue(order.IndexOf("public.a.a_ck") < order.IndexOf("public.b"));
        Assert.IsTrue(order.IndexOf("public.a.a_pkey") < order.IndexOf("public.b"));
    }

    private static string[] DependenciesOf(string sql, string key)
        => new DependencyAnalyzer().Analyze(Build(sql)).GetDependencies(key).Select(static node => node.NodeId).ToArray();

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
