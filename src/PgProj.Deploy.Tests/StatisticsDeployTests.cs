using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>G-150: statistics are created after their table and dropped before it; a change is a drop and a create; a removal is not a destructive change.</summary>
[TestClass]
[DoNotParallelize]
public sealed class StatisticsDeployTests
{
    private const string Table = "CREATE SCHEMA app; CREATE TABLE app.t (a int, b int, c text); ";

    [TestMethod]
    public void StatisticsAreCreatedAfterTheirTableWhateverTheOrderOfTheFile()
    {
        var statements = Statements(Build("CREATE SCHEMA app;"), Build("CREATE SCHEMA app; CREATE STATISTICS app.s ON a, b FROM app.t; CREATE TABLE app.t (a int, b int);"));

        var tableAt = statements.FindIndex(static s => s.StartsWith("CREATE TABLE", StringComparison.Ordinal));
        var statisticsAt = statements.FindIndex(static s => s.StartsWith("CREATE STATISTICS", StringComparison.Ordinal));
        Assert.IsTrue(tableAt >= 0 && tableAt < statisticsAt, string.Join("\n", statements));
    }

    [TestMethod]
    public void StatisticsAreWrittenWithTheirColumnsAndTheKindsOnlyWhenNotAllThree()
    {
        var all = Statements(Build(Table), Build(Table + "CREATE STATISTICS app.s ON a, b FROM app.t;")).Single(static s => s.StartsWith("CREATE STATISTICS", StringComparison.Ordinal));
        var some = Statements(Build(Table), Build(Table + "CREATE STATISTICS app.s (ndistinct, mcv) ON a, (lower(c)) FROM app.t;")).Single(static s => s.StartsWith("CREATE STATISTICS", StringComparison.Ordinal));

        Assert.AreEqual("CREATE STATISTICS \"app\".\"s\" ON \"a\", \"b\" FROM \"app\".\"t\";", all);
        StringAssert.StartsWith(some, "CREATE STATISTICS \"app\".\"s\" (mcv, ndistinct) ON \"a\", (");
        StringAssert.EndsWith(some, ") FROM \"app\".\"t\";");
    }

    [TestMethod]
    public void AChangedStatisticsObjectIsDroppedAndCreatedAgain()
    {
        var statements = Statements(
            Build(Table + "CREATE STATISTICS app.s ON a, b FROM app.t;"),
            Build(Table + "CREATE STATISTICS app.s (mcv) ON a, b FROM app.t;"));

        Assert.AreEqual(2, statements.Count, string.Join("\n", statements));
        Assert.AreEqual("DROP STATISTICS \"app\".\"s\";", statements[0]);
        StringAssert.StartsWith(statements[1], "CREATE STATISTICS");
    }

    [TestMethod]
    public void ARemovedStatisticsObjectIsDroppedBeforeItsTableAndIsNotDestructive()
    {
        var batch = Generate(Build(Table + "CREATE STATISTICS app.s ON a, b FROM app.t;"), Build("CREATE SCHEMA app;"));
        var statements = Sql(batch);

        var statisticsAt = statements.FindIndex(static s => s.StartsWith("DROP STATISTICS", StringComparison.Ordinal));
        var tableAt = statements.FindIndex(static s => s.StartsWith("DROP TABLE", StringComparison.Ordinal));
        Assert.IsTrue(statisticsAt >= 0 && statisticsAt < tableAt, string.Join("\n", statements));
        Assert.IsFalse(batch.GetDestructiveOperations().Any(static d => d.Reasons.Any(static r => r.Contains("statistics", StringComparison.OrdinalIgnoreCase))));
    }

    private static OperationBatch Generate(DatabaseModel oldModel, DatabaseModel newModel)
        => new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));

    private static List<string> Sql(OperationBatch batch)
        => batch.Operations
            .Where(static operation => operation is not SchemaCreateOperation)
            .Select(static operation => operation.GenerateSql().Trim().Replace("\r\n", "\n", StringComparison.Ordinal))
            .ToList();

    private static List<string> Statements(DatabaseModel oldModel, DatabaseModel newModel) => Sql(Generate(oldModel, newModel));

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
