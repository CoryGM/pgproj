using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-121 part 2: a virtual generated column (PostgreSQL 18) is created as <c>VIRTUAL</c>, a stored one still as <c>STORED</c>, and a column that
/// changes between them is dropped and added, since neither can become the other in place and a generated column loses nothing when it is.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class VirtualGeneratedColumnDeployTests
{
    [TestMethod]
    [DataRow(" VIRTUAL", ") VIRTUAL")]
    [DataRow("", ") VIRTUAL")]
    [DataRow(" STORED", ") STORED")]
    public void ANewGeneratedColumnIsCreatedInItsKind(string written, string expectedEnding)
    {
        var statements = Statements(DatabaseModel.Empty, Build($"CREATE TABLE public.t (a integer, b integer GENERATED ALWAYS AS (a * 2){written});"));

        Assert.IsTrue(statements.Any(statement => statement.Contains($"GENERATED ALWAYS AS (a * 2{expectedEnding}", StringComparison.Ordinal)), string.Join("\n---\n", statements));
    }

    [TestMethod]
    public void AVirtualColumnAddedToAnExistingTableIsAddedVirtual()
    {
        var statements = Statements(
            Build("CREATE TABLE public.t (a integer);"),
            Build("CREATE TABLE public.t (a integer, b integer GENERATED ALWAYS AS (a * 2) VIRTUAL);"));

        StringAssert.Contains(string.Join("\n", statements), "ADD COLUMN \"b\" integer GENERATED ALWAYS AS (a * 2) VIRTUAL;");
    }

    [TestMethod]
    [DataRow(" STORED", " VIRTUAL", "VIRTUAL;")]
    [DataRow(" VIRTUAL", " STORED", "STORED;")]
    public void AChangeBetweenStoredAndVirtualDropsAndAddsTheColumn(string before, string after, string expectedEnding)
    {
        var statements = Statements(
            Build($"CREATE TABLE public.t (a integer, b integer GENERATED ALWAYS AS (a * 2){before});"),
            Build($"CREATE TABLE public.t (a integer, b integer GENERATED ALWAYS AS (a * 2){after});"));

        var script = string.Join("\n", statements);
        StringAssert.Contains(script, "DROP COLUMN \"b\"");
        StringAssert.Contains(script, "ADD COLUMN \"b\"");
        StringAssert.Contains(script, expectedEnding);
    }

    [TestMethod]
    public void AChangedVirtualExpressionDropsAndAddsTheColumnVirtual()
    {
        var statements = Statements(
            Build("CREATE TABLE public.t (a integer, b integer GENERATED ALWAYS AS (a * 2) VIRTUAL);"),
            Build("CREATE TABLE public.t (a integer, b integer GENERATED ALWAYS AS (a * 3) VIRTUAL);"));

        var script = string.Join("\n", statements);
        StringAssert.Contains(script, "DROP COLUMN \"b\"");
        StringAssert.Contains(script, "GENERATED ALWAYS AS (a * 3) VIRTUAL;");
    }

    [TestMethod]
    public void TheSameVirtualColumnWrittenTwoWaysDeploysNothing()
    {
        Assert.IsEmpty(Statements(
            Build("CREATE TABLE public.t (a integer, b integer GENERATED ALWAYS AS (a * 2) VIRTUAL);"),
            Build("CREATE TABLE public.t (a integer, b integer GENERATED ALWAYS AS (a * 2));")));
    }

    [TestMethod]
    public void ChangingBetweenStoredAndVirtualIsNotDestructive()
    {
        var oldModel = Build("CREATE TABLE public.t (a integer, b integer GENERATED ALWAYS AS (a * 2) STORED);");
        var newModel = Build("CREATE TABLE public.t (a integer, b integer GENERATED ALWAYS AS (a * 2) VIRTUAL);");
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));

        Assert.IsEmpty(batch.GetDestructiveOperations());
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
}
