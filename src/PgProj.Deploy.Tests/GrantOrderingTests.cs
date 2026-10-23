using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-61: <c>GRANT ... ON ALL TABLES|SEQUENCES|FUNCTIONS IN SCHEMA s</c> applies to the objects that exist when it runs, but a grant
/// had no dependency on the objects it covers, so its position depended on the order the model happened to enumerate them:
/// a sequence created in the same publish came after the grant and was never granted anything (a real-server run showed
/// <c>GRANT ... ON ALL SEQUENCES</c> emitted before <c>CREATE SEQUENCE</c>). A grant must come after the objects it covers, and be
/// revoked before they are dropped.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class GrantOrderingTests
{
    [TestMethod]
    [DataRow("create sequence s.q;", "GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA s TO r;", "CREATE SEQUENCE \"s\".\"q\"", "GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA")]
    [DataRow("create table s.z_t (id integer);", "GRANT SELECT ON ALL TABLES IN SCHEMA s TO r;", "CREATE TABLE \"s\".\"z_t\"", "GRANT SELECT ON ALL TABLES IN SCHEMA")]
    [DataRow("create table s.t (id integer); create view s.z_v as select id from s.t;", "GRANT SELECT ON ALL TABLES IN SCHEMA s TO r;", "CREATE OR REPLACE VIEW \"s\".\"z_v\"", "GRANT SELECT ON ALL TABLES IN SCHEMA")]
    [DataRow("create function s.z_f() returns integer language sql as $$ select 1 $$;", "GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA s TO r;", "CREATE OR REPLACE FUNCTION \"s\".\"z_f\"", "GRANT ALL PRIVILEGES ON ALL FUNCTIONS IN SCHEMA")]
    public void GrantOverAllObjectsIsEmittedAfterTheObjectsAreCreated(string objects, string grant, string createPrefix, string grantPrefix)
    {
        var statements = Statements(DatabaseModel.Empty, Build("create schema s; " + objects + " " + grant));

        AssertBefore(statements, createPrefix, grantPrefix);
    }

    [TestMethod]
    public void GrantIsEmittedAfterEveryKindOfObjectItCoversEvenWhenTheyAreAllNewTogether()
    {
        var statements = Statements(DatabaseModel.Empty, Build(
            "create schema s; create table s.t (id integer); create sequence s.q; create function s.f() returns integer language sql as $$ select 1 $$; "
            + "GRANT SELECT ON ALL TABLES IN SCHEMA s TO r; GRANT ALL ON ALL SEQUENCES IN SCHEMA s TO r; GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA s TO r;"));

        AssertBefore(statements, "CREATE TABLE \"s\".\"t\"", "GRANT SELECT ON ALL TABLES");
        AssertBefore(statements, "CREATE SEQUENCE \"s\".\"q\"", "GRANT ALL PRIVILEGES ON ALL SEQUENCES");
        AssertBefore(statements, "CREATE OR REPLACE FUNCTION \"s\".\"f\"", "GRANT ALL PRIVILEGES ON ALL FUNCTIONS");
    }

    /// <summary>A grant on the schema itself needs no object, and one in another schema is not held back by these objects.</summary>
    [TestMethod]
    public void GrantOnAnotherSchemasObjectsDoesNotWaitForThisSchemasObjects()
    {
        var statements = Statements(DatabaseModel.Empty, Build(
            "create schema a; create schema z; create table z.t (id integer); GRANT USAGE ON SCHEMA a TO r; GRANT SELECT ON ALL TABLES IN SCHEMA z TO r;"));

        AssertBefore(statements, "CREATE TABLE \"z\".\"t\"", "GRANT SELECT ON ALL TABLES IN SCHEMA \"z\"");
        Assert.IsTrue(statements.Any(static statement => statement.StartsWith("GRANT USAGE ON SCHEMA \"a\"", StringComparison.Ordinal)), string.Join("\n", statements));
    }

    /// <summary>Removing the objects and the grant together revokes first, then drops.</summary>
    [TestMethod]
    [DataRow("create sequence s.q;", "GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA s TO r;", "DROP SEQUENCE", "REVOKE ALL PRIVILEGES ON ALL SEQUENCES")]
    [DataRow("create table s.t (id integer);", "GRANT SELECT ON ALL TABLES IN SCHEMA s TO r;", "DROP TABLE", "REVOKE SELECT ON ALL TABLES")]
    public void GrantIsRevokedBeforeTheObjectsItCoversAreDropped(string objects, string grant, string dropPrefix, string revokePrefix)
    {
        var statements = Statements(Build("create schema s; " + objects + " " + grant), Build("create schema s;"));

        AssertBefore(statements, revokePrefix, dropPrefix);
    }

    [TestMethod]
    public void GrantOverNothingIsStillNotEmitted()
    {
        var statements = Statements(DatabaseModel.Empty, Build("create schema s; GRANT SELECT ON ALL TABLES IN SCHEMA s TO r;"));

        Assert.IsFalse(statements.Any(static statement => statement.StartsWith("GRANT", StringComparison.Ordinal)), string.Join("\n", statements));
    }

    private static void AssertBefore(IReadOnlyList<string> statements, string firstPrefix, string secondPrefix)
    {
        var first = statements.Select((statement, index) => (statement, index)).FirstOrDefault(entry => entry.statement.StartsWith(firstPrefix, StringComparison.Ordinal));
        var second = statements.Select((statement, index) => (statement, index)).FirstOrDefault(entry => entry.statement.StartsWith(secondPrefix, StringComparison.Ordinal));

        Assert.IsNotNull(first.statement, $"No statement starts with '{firstPrefix}'.\n{string.Join("\n", statements)}");
        Assert.IsNotNull(second.statement, $"No statement starts with '{secondPrefix}'.\n{string.Join("\n", statements)}");
        Assert.IsLessThan(second.index, first.index, $"'{firstPrefix}' must come before '{secondPrefix}'.\n{string.Join("\n", statements)}");
    }

    private static IReadOnlyList<string> Statements(DatabaseModel oldModel, DatabaseModel newModel)
    {
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));
        return batch.Operations
            .Where(static operation => operation is not SchemaCreateOperation)
            .Select(static operation => operation.GenerateSql().Trim())
            .ToArray();
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
