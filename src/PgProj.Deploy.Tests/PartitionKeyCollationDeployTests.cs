using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-240: a partition key column keeps its <c>COLLATE</c> and operator class. They were dropped, so the key compared under the column's own collation, and partitions
/// that do not overlap under the key's could overlap under the column's.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class PartitionKeyCollationDeployTests
{
    [TestMethod]
    public void TheKeyIsDeployedWithItsCollationAndOperatorClass()
    {
        var script = Script(DatabaseModel.Empty, Build("CREATE TABLE public.p (a text, c text) PARTITION BY LIST (c COLLATE \"C\"); CREATE TABLE public.q (a text) PARTITION BY RANGE (a text_pattern_ops);"));

        StringAssert.Contains(script, "PARTITION BY LIST (c COLLATE \"C\")");
        StringAssert.Contains(script, "PARTITION BY RANGE (a \"text_pattern_ops\")");
    }

    [TestMethod]
    public void AKeyWithNoCollationIsDeployedAsBefore()
    {
        var script = Script(DatabaseModel.Empty, Build("CREATE TABLE public.p (a int, b int) PARTITION BY RANGE (a, (b + 1));"));

        StringAssert.Contains(script, "PARTITION BY RANGE (a, (b + 1))");
    }

    [TestMethod]
    public void TheDatabasesSpellingOfTheCollationIsTheSameKey()
    {
        Assert.AreEqual(string.Empty, Script(
            Build("CREATE TABLE public.p (c text) PARTITION BY LIST (c COLLATE pg_catalog.\"C\");"),
            Build("CREATE TABLE public.p (c text) PARTITION BY LIST (c COLLATE \"C\");")));
    }

    [TestMethod]
    public void ADifferentCollationIsAChange()
    {
        var script = Script(
            Build("CREATE TABLE public.p (c text) PARTITION BY LIST (c COLLATE \"C\");"),
            Build("CREATE TABLE public.p (c text) PARTITION BY LIST (c COLLATE \"POSIX\");"));

        StringAssert.Contains(script, "partition key changed");
    }

    [TestMethod]
    public void AddingACollationToTheKeyIsAChange()
    {
        var script = Script(
            Build("CREATE TABLE public.p (c text) PARTITION BY LIST (c);"),
            Build("CREATE TABLE public.p (c text) PARTITION BY LIST (c COLLATE \"C\");"));

        StringAssert.Contains(script, "partition key changed");
    }

    private static string Script(DatabaseModel oldModel, DatabaseModel newModel)
    {
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));
        return string.Join("\n", batch.Operations.Where(static operation => operation is not SchemaCreateOperation).Select(static operation => operation.GenerateSql().Replace("\r\n", "\n", StringComparison.Ordinal))).Trim();
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
