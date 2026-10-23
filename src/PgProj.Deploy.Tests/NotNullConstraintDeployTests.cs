using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-126: a named <c>NOT NULL</c> deploys as the plain <c>NOT NULL</c> on its column, with no <c>ADD CONSTRAINT</c> and no TODO placeholder, and
/// changes nothing against the same column written unnamed.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class NotNullConstraintDeployTests
{
    [TestMethod]
    [DataRow("CREATE TABLE public.t (a integer CONSTRAINT nn NOT NULL, b integer);")]
    [DataRow("CREATE TABLE public.t (a integer, b integer, CONSTRAINT nn NOT NULL a);")]
    [DataRow("CREATE TABLE public.t (a integer, b integer); ALTER TABLE public.t ADD CONSTRAINT nn NOT NULL a;")]
    public void ANamedNotNullIsDeployedAsTheColumnsNotNull(string sql)
    {
        var (statements, batch) = Generate(DatabaseModel.Empty, Build(sql));

        var script = string.Join("\n", statements);
        // The name is deployed on the column (G-235; before it was dropped, G-126), still as the column's own NOT NULL and not as a constraint of its own.
        StringAssert.Contains(script, "\"a\" integer CONSTRAINT \"nn\" NOT NULL");
        Assert.IsFalse(script.Contains("ADD CONSTRAINT", StringComparison.Ordinal) || script.Contains("TODO", StringComparison.Ordinal), script);
        Assert.IsEmpty(batch.GetUnsupportedOperations());
    }

    [TestMethod]
    public void ANamedNotNullOnAnExistingColumnIsASetNotNull()
    {
        var (statements, _) = Generate(
            Build("CREATE TABLE public.t (a integer, b integer);"),
            Build("CREATE TABLE public.t (a integer CONSTRAINT nn NOT NULL, b integer);"));

        Assert.IsTrue(statements.Any(static s => s.Contains("ALTER COLUMN \"a\" SET NOT NULL", StringComparison.Ordinal)), string.Join("\n", statements));
    }

    [TestMethod]
    public void ANamedNotNullAgainstTheSameColumnUnnamedDeploysNothing()
    {
        var (statements, _) = Generate(
            Build("CREATE TABLE public.t (a integer NOT NULL, b integer);"),
            Build("CREATE TABLE public.t (a integer CONSTRAINT nn NOT NULL, b integer);"));

        Assert.IsEmpty(statements);
    }

    private static (List<string> Statements, OperationBatch Batch) Generate(DatabaseModel oldModel, DatabaseModel newModel)
    {
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));
        var statements = batch.Operations
            .Where(static operation => operation is not SchemaCreateOperation)
            .Select(static operation => operation.GenerateSql().Trim().Replace("\r\n", "\n", StringComparison.Ordinal))
            .ToList();
        return (statements, batch);
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
