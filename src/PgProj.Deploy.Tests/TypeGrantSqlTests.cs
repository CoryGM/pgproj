using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-337: the SQL for privileges on types and domains, where it sits in a script, and which of it takes access away. A grant is written <c>ON TYPE</c> (a domain too,
/// as <c>pg_dump</c> writes it); a revoked default is deployed as its <c>REVOKE ... FROM PUBLIC</c> and undone by <c>GRANT ... TO PUBLIC</c>. Taking PUBLIC's default
/// away from a type that exists is destructive; on a new type, or put back on one recreated, it is not; giving it back never is.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class TypeGrantSqlTests
{
    private const string Types = "CREATE SCHEMA app; CREATE TYPE app.mood AS ENUM ('a'); CREATE DOMAIN app.pos AS integer; ";

    [TestMethod]
    [DataRow("GRANT USAGE ON TYPE app.mood TO r;", "GRANT ALL PRIVILEGES ON TYPE \"app\".\"mood\" TO \"r\";")]
    [DataRow("GRANT USAGE ON DOMAIN app.pos TO r WITH GRANT OPTION;", "GRANT ALL PRIVILEGES ON TYPE \"app\".\"pos\" TO \"r\" WITH GRANT OPTION;")]
    [DataRow("REVOKE USAGE ON TYPE app.mood FROM PUBLIC;", "REVOKE ALL PRIVILEGES ON TYPE \"app\".\"mood\" FROM PUBLIC;")]
    [DataRow("REVOKE USAGE ON DOMAIN app.pos FROM PUBLIC;", "REVOKE ALL PRIVILEGES ON TYPE \"app\".\"pos\" FROM PUBLIC;")]
    public void EachFormIsWrittenAfterItsType(string sql, string expected)
    {
        var statements = Statements(DatabaseModel.Empty, Build(Types + sql), out var batch);

        CollectionAssert.Contains(statements.ToArray(), expected, string.Join("\n", statements));
        var typeName = sql.Contains("pos", StringComparison.Ordinal) ? "DOMAIN \"app\".\"pos\"" : "TYPE \"app\".\"mood\"";
        AssertBefore(statements, $"CREATE {typeName}", expected);
        Assert.IsEmpty(batch.GetDestructiveOperations(), "On a type the script creates, nothing is taken away.");
    }

    [TestMethod]
    public void TakingTheDefaultFromATypeThatExistsIsDestructive()
    {
        var statements = Statements(Build(Types), Build(Types + "REVOKE ALL ON TYPE app.mood FROM PUBLIC;"), out var batch);

        CollectionAssert.AreEqual(new[] { "REVOKE ALL PRIVILEGES ON TYPE \"app\".\"mood\" FROM PUBLIC;" }, statements.ToArray());
        var destructive = batch.GetDestructiveOperations().Single();
        Assert.AreEqual("grant revoked: REVOKE ALL PRIVILEGES ON TYPE \"app\".\"mood\" FROM PUBLIC", destructive.Reasons.Single());
    }

    [TestMethod]
    public void GivingTheDefaultBackIsAGrantToPublicAndTakesNothingAway()
    {
        var statements = Statements(Build(Types + "REVOKE ALL ON TYPE app.mood FROM PUBLIC;"), Build(Types), out var batch);

        CollectionAssert.AreEqual(new[] { "GRANT ALL PRIVILEGES ON TYPE \"app\".\"mood\" TO PUBLIC;" }, statements.ToArray());
        Assert.IsEmpty(batch.GetDestructiveOperations());
    }

    [TestMethod]
    public void RevokingARolesGrantOnATypeIsDestructive()
    {
        var statements = Statements(Build(Types + "GRANT USAGE ON TYPE app.mood TO r;"), Build(Types), out var batch);

        CollectionAssert.AreEqual(new[] { "REVOKE ALL PRIVILEGES ON TYPE \"app\".\"mood\" FROM \"r\";" }, statements.ToArray());
        StringAssert.StartsWith(batch.GetDestructiveOperations().Single().Reasons.Single(), "grant revoked: ");
    }

    [TestMethod]
    public void ARevokedDefaultPutBackOnARecreatedTypeTakesNothingAway()
    {
        var revoked = Build(Types + "REVOKE ALL ON TYPE app.mood FROM PUBLIC;").Grants.Values.Single();
        var batch = new OperationBatch
        {
            Operations =
            [
                new CreateOperation { NodeId = revoked.Key, NodeKind = ChangeNodeKind.Grant, Node = revoked, IsRestored = true },
            ],
        };

        Assert.IsEmpty(batch.GetDestructiveOperations());
    }

    [TestMethod]
    public void ATypeDroppedAndCreatedAgainGetsItsPrivilegesBack()
    {
        // A domain whose collation changes is dropped and created again when nothing uses it (G-196); its privileges go with it and must be issued again.
        const string live = "CREATE SCHEMA app; CREATE DOMAIN app.pos AS text COLLATE \"C\"; ";
        const string project = "CREATE SCHEMA app; CREATE DOMAIN app.pos AS text COLLATE \"POSIX\"; ";
        const string privileges = "REVOKE ALL ON TYPE app.pos FROM PUBLIC; GRANT USAGE ON DOMAIN app.pos TO r;";

        var statements = Statements(Build(live + privileges), Build(project + privileges), out var batch);

        AssertBefore(statements, "DROP DOMAIN \"app\".\"pos\"", "CREATE DOMAIN \"app\".\"pos\"");
        AssertBefore(statements, "CREATE DOMAIN \"app\".\"pos\"", "REVOKE ALL PRIVILEGES ON TYPE \"app\".\"pos\" FROM PUBLIC;");
        AssertBefore(statements, "CREATE DOMAIN \"app\".\"pos\"", "GRANT ALL PRIVILEGES ON TYPE \"app\".\"pos\" TO \"r\";");
        Assert.IsFalse(batch.GetDestructiveOperations().Any(static destructive => destructive.Operation.NodeKind == ChangeNodeKind.Grant), "Putting back what the type had takes nothing away.");
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
