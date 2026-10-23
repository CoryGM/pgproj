using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-121 part 2: a <c>NOT ENFORCED</c> CHECK or FOREIGN KEY constraint is deployed with <c>NOT ENFORCED</c>, after its deferrable clause, and a
/// constraint that changes between enforced and not is dropped and added back the way any changed constraint is.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ConstraintEnforcementDeployTests
{
    [TestMethod]
    public void ANotEnforcedCheckIsAddedNotEnforced()
    {
        var statements = Statements(DatabaseModel.Empty, Build("CREATE TABLE public.t (a integer, CONSTRAINT c CHECK (a > 0) NOT ENFORCED);"));

        Assert.IsTrue(statements.Any(static s => s.EndsWith("ADD CONSTRAINT \"c\"\nCHECK (a > 0) NOT ENFORCED;", StringComparison.Ordinal)), string.Join("\n---\n", statements));
    }

    [TestMethod]
    public void ANotEnforcedForeignKeyIsAddedNotEnforcedAfterItsDeferrableClause()
    {
        var statements = Statements(
            DatabaseModel.Empty,
            Build("CREATE TABLE public.p (id integer PRIMARY KEY); CREATE TABLE public.t (a integer, CONSTRAINT c FOREIGN KEY (a) REFERENCES public.p (id) DEFERRABLE INITIALLY DEFERRED NOT ENFORCED);"));

        var foreignKey = statements.Single(static s => s.Contains("FOREIGN KEY", StringComparison.Ordinal));
        StringAssert.EndsWith(foreignKey, " DEFERRABLE INITIALLY DEFERRED NOT ENFORCED;");
    }

    [TestMethod]
    public void AnEnforcedConstraintHasNoEnforcedClause()
    {
        var statements = Statements(
            DatabaseModel.Empty,
            Build("CREATE TABLE public.p (id integer PRIMARY KEY); CREATE TABLE public.t (a integer, CONSTRAINT c1 CHECK (a > 0) ENFORCED, CONSTRAINT c2 FOREIGN KEY (a) REFERENCES public.p (id));"));

        Assert.IsFalse(string.Join("\n", statements).Contains("ENFORCED", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("", " NOT ENFORCED", "NOT ENFORCED")]
    [DataRow(" NOT ENFORCED", "", "CHECK (a > 0);")]
    public void AChangeBetweenEnforcedAndNotEnforcedDropsAndAddsTheConstraint(string before, string after, string expectedEnding)
    {
        var statements = Statements(
            Build($"CREATE TABLE public.t (a integer, CONSTRAINT c CHECK (a > 0){before});"),
            Build($"CREATE TABLE public.t (a integer, CONSTRAINT c CHECK (a > 0){after});"));

        var script = string.Join("\n", statements);
        StringAssert.Contains(script, "DROP CONSTRAINT");
        StringAssert.Contains(script, "ADD CONSTRAINT");
        StringAssert.Contains(script, expectedEnding);
    }

    [TestMethod]
    public void TheSameConstraintWrittenEnforcedTwoWaysDeploysNothing()
    {
        Assert.IsEmpty(Statements(
            Build("CREATE TABLE public.t (a integer, CONSTRAINT c CHECK (a > 0));"),
            Build("CREATE TABLE public.t (a integer, CONSTRAINT c CHECK (a > 0) ENFORCED);")));
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
