using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-338: a routine's revoked default is deployed as its <c>REVOKE ... FROM PUBLIC</c>, after the routine, and undone by <c>GRANT ... TO PUBLIC</c>; a rule as
/// <c>ALTER DEFAULT PRIVILEGES ... REVOKE ... FROM PUBLIC</c>, before the objects it covers. Taking the default from a routine that exists is destructive (also one
/// replaced in place); on a new routine, or put back on one recreated, it is not; a rule changes nothing that exists. A routine dropped and created again gets its
/// revoke back, whether the project states it or a rule of the project allows it: before G-338 it came back with PUBLIC's EXECUTE, silently.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RoutineRevokeSqlTests
{
    private const string Schema = "CREATE SCHEMA app; ";
    private const string F = "CREATE FUNCTION app.f() RETURNS integer LANGUAGE sql SECURITY DEFINER AS $$ SELECT 1 $$; ";
    private const string RevokeF = "REVOKE ALL ON FUNCTION app.f() FROM PUBLIC;";
    private const string RevokeFSql = "REVOKE ALL PRIVILEGES ON FUNCTION \"app\".\"f\"() FROM PUBLIC;";

    [TestMethod]
    public void ARevokeOnANewRoutineIsWrittenAfterItAndTakesNothingAway()
    {
        var statements = Statements(Build(Schema), Build(Schema + F + RevokeF), out var batch);

        AssertBefore(statements, "CREATE OR REPLACE FUNCTION \"app\".\"f\"", RevokeFSql);
        Assert.IsEmpty(batch.GetDestructiveOperations());
    }

    [TestMethod]
    public void ARevokeOnAProcedureIsWrittenOnProcedure()
    {
        const string p = "CREATE PROCEDURE app.p(a integer) LANGUAGE sql AS $$ SELECT 1 $$; ";
        var statements = Statements(Build(Schema), Build(Schema + p + "REVOKE ALL ON PROCEDURE app.p(integer) FROM PUBLIC;"), out _);

        CollectionAssert.Contains(statements.ToArray(), "REVOKE ALL PRIVILEGES ON PROCEDURE \"app\".\"p\"(pg_catalog.int4) FROM PUBLIC;", string.Join("\n", statements));
    }

    [TestMethod]
    [DataRow(F, "the routine exists")]
    [DataRow("CREATE FUNCTION app.f() RETURNS integer LANGUAGE sql SECURITY DEFINER AS $$ SELECT 0 $$; ", "the routine is replaced in place")]
    public void TakingTheDefaultFromARoutineThatExistsIsDestructive(string live, string why)
    {
        var statements = Statements(Build(Schema + live), Build(Schema + F + RevokeF), out var batch);

        CollectionAssert.Contains(statements.ToArray(), RevokeFSql, why);
        var destructive = batch.GetDestructiveOperations().Single();
        StringAssert.Contains(destructive.Reasons.Single(), "grant revoked", why);
    }

    [TestMethod]
    public void GivingTheDefaultBackIsAGrantToPublicAndTakesNothingAway()
    {
        var statements = Statements(Build(Schema + F + RevokeF), Build(Schema + F), out var batch);

        CollectionAssert.AreEqual(new[] { "GRANT ALL PRIVILEGES ON FUNCTION \"app\".\"f\"() TO PUBLIC;" }, statements.ToArray());
        Assert.IsEmpty(batch.GetDestructiveOperations());
    }

    [TestMethod]
    [DataRow("", "the project states it")]
    [DataRow("ALTER DEFAULT PRIVILEGES REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;", "a rule of the project allows it")]
    [DataRow("ALTER DEFAULT PRIVILEGES FOR ROLE other REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;", "another role's rule allows it")]
    public void ARoutineDroppedAndCreatedAgainGetsItsRevokeBack(string rule, string why)
    {
        // A changed return type is a drop and a create: PostgreSQL drops the routine's privileges with it.
        var stated = why == "the project states it" ? RevokeF : string.Empty;
        var statements = Statements(
            Build(Schema + rule + F + RevokeF),
            Build(Schema + rule + "CREATE FUNCTION app.f() RETURNS bigint LANGUAGE sql SECURITY DEFINER AS $$ SELECT 1 $$; " + stated),
            out var batch);

        AssertBefore(statements, "DROP FUNCTION \"app\".\"f\"", "CREATE OR REPLACE FUNCTION \"app\".\"f\"");
        AssertBefore(statements, "CREATE OR REPLACE FUNCTION \"app\".\"f\"", RevokeFSql);
        Assert.IsFalse(statements.Any(static statement => statement.Contains("TO PUBLIC", StringComparison.Ordinal)), why);
        Assert.IsFalse(batch.GetDestructiveOperations().Any(static destructive => destructive.Operation.NodeKind == ChangeNodeKind.Grant), why);
    }

    [TestMethod]
    public void ARoutineReplacedInPlaceKeepsItsRevokeWithoutAStatement()
    {
        var statements = Statements(Build(Schema + F + RevokeF), Build(Schema + "CREATE FUNCTION app.f() RETURNS integer LANGUAGE sql SECURITY DEFINER AS $$ SELECT 2 $$; " + RevokeF), out _);

        Assert.IsFalse(statements.Any(static statement => statement.Contains("PUBLIC", StringComparison.Ordinal)), string.Join("\n", statements));
    }

    [TestMethod]
    [DataRow("ALTER DEFAULT PRIVILEGES REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;", "ALTER DEFAULT PRIVILEGES REVOKE ALL PRIVILEGES ON FUNCTIONS FROM PUBLIC;")]
    [DataRow("ALTER DEFAULT PRIVILEGES FOR ROLE o REVOKE USAGE ON TYPES FROM PUBLIC;", "ALTER DEFAULT PRIVILEGES FOR ROLE \"o\" REVOKE ALL PRIVILEGES ON TYPES FROM PUBLIC;")]
    public void ARuleIsWrittenBeforeTheObjectsItCoversAndNeitherWayTakesAnythingAway(string rule, string expected)
    {
        var statements = Statements(Build(Schema + F), Build(Schema + rule + F + "CREATE FUNCTION app.g() RETURNS integer LANGUAGE sql AS $$ SELECT 1 $$; CREATE TYPE app.mood AS ENUM ('a');"), out var batch);

        CollectionAssert.Contains(statements.ToArray(), expected, string.Join("\n", statements));
        AssertBefore(statements, expected, rule.Contains("TYPES", StringComparison.Ordinal) ? "CREATE TYPE \"app\".\"mood\"" : "CREATE OR REPLACE FUNCTION \"app\".\"g\"");
        Assert.IsEmpty(batch.GetDestructiveOperations(), "A rule changes nothing that exists.");

        var removed = Statements(Build(Schema + rule + F), Build(Schema + F), out var removedBatch);
        CollectionAssert.Contains(removed.ToArray(), expected.Replace("REVOKE", "GRANT", StringComparison.Ordinal).Replace("FROM PUBLIC", "TO PUBLIC", StringComparison.Ordinal));
        Assert.IsEmpty(removedBatch.GetDestructiveOperations());
    }

    private static void AssertBefore(IReadOnlyList<string> statements, string firstPrefix, string secondPrefix)
    {
        var first = statements.Select((statement, index) => (statement, index)).FirstOrDefault(entry => entry.statement.StartsWith(firstPrefix, StringComparison.Ordinal));
        var second = statements.Select((statement, index) => (statement, index)).FirstOrDefault(entry => entry.statement.StartsWith(secondPrefix, StringComparison.Ordinal));

        Assert.IsNotNull(first.statement, $"No statement starts with '{firstPrefix}'.\n{string.Join("\n", statements)}");
        Assert.IsNotNull(second.statement, $"No statement starts with '{secondPrefix}'.\n{string.Join("\n", statements)}");
        Assert.IsLessThan(second.index, first.index, $"'{firstPrefix}' must come before '{secondPrefix}'.\n{string.Join("\n", statements)}");
    }

    private static IReadOnlyList<string> Statements(DatabaseModel oldModel, DatabaseModel newModel, out OperationBatch batch)
    {
        batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));
        return batch.Operations
            .Where(static operation => operation is not SchemaCreateOperation)
            .Select(static operation => operation.GenerateSql().Trim())
            .ToArray();
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
