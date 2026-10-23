using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-247: a schema that holds nothing but its owner is created before the owner is set. It was created only for an object in it, so <c>ALTER SCHEMA s OWNER TO r</c> failed
/// with "schema does not exist".
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SchemaOwnerCreateTests
{
    [TestMethod]
    public void ASchemaWithOnlyAnOwnerIsCreatedFirst()
    {
        var statements = Statements(Build("CREATE SCHEMA IF NOT EXISTS s; ALTER SCHEMA s OWNER TO app_owner;"));

        Assert.AreEqual("CREATE SCHEMA IF NOT EXISTS \"s\";", statements[0], string.Join("\n", statements));
        Assert.IsTrue(statements.Any(static s => s.StartsWith("ALTER SCHEMA", StringComparison.Ordinal)), string.Join("\n", statements));
    }

    [TestMethod]
    public void TheDefaultSchemaAndASchemaWithNoStatementAreNotCreated()
    {
        var statements = Statements(Build("ALTER SCHEMA public OWNER TO pg_database_owner;"));

        Assert.IsFalse(statements.Any(static s => s.StartsWith("CREATE SCHEMA", StringComparison.Ordinal)), string.Join("\n", statements));
    }

    private static List<string> Statements(DatabaseModel newModel)
    {
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(DatabaseModel.Empty, newModel), new DependencyAnalyzer().Analyze(DatabaseModel.Empty, newModel));
        return batch.Operations.Select(static operation => operation.GenerateSql().Trim().Replace("\r\n", "\n", StringComparison.Ordinal)).ToList();
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
