using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-284: a schema the project declares is created whether or not anything is in it. A <c>CREATE SCHEMA</c> with nothing else was dropped from the script, so Supabase's
/// empty <c>graphql</c>, <c>storage</c>, and <c>vault</c> schemas were missing after <c>publish</c>.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SchemaDeclarationCreateTests
{
    [TestMethod]
    public void AnEmptyDeclaredSchemaIsCreatedAndNothingElse()
    {
        var statements = Statements(DatabaseModel.Empty, Build("CREATE SCHEMA a;"));

        CollectionAssert.AreEqual(new[] { "CREATE SCHEMA IF NOT EXISTS \"a\";" }, statements, string.Join("\n", statements));
    }

    [TestMethod]
    public void EachDeclaredSchemaIsCreatedOnceInNameOrder()
    {
        var statements = Statements(DatabaseModel.Empty, Build("CREATE SCHEMA b; CREATE SCHEMA a; CREATE TABLE b.t (id int);"));

        Assert.AreEqual("CREATE SCHEMA IF NOT EXISTS \"a\";", statements[0], string.Join("\n", statements));
        Assert.AreEqual("CREATE SCHEMA IF NOT EXISTS \"b\";", statements[1], string.Join("\n", statements));
        Assert.AreEqual(2, statements.Count(static s => s.StartsWith("CREATE SCHEMA", StringComparison.Ordinal)), string.Join("\n", statements));
    }

    [TestMethod]
    public void ADeclaredSchemasCommentIsSetAfterItIsCreated()
    {
        var statements = Statements(DatabaseModel.Empty, Build("CREATE SCHEMA a; COMMENT ON SCHEMA a IS 'Filled by another tool.';"));

        CollectionAssert.AreEqual(new[] { "CREATE SCHEMA IF NOT EXISTS \"a\";", "COMMENT ON SCHEMA \"a\" IS 'Filled by another tool.';" }, statements, string.Join("\n", statements));
    }

    [TestMethod]
    public void ASchemaTheDatabaseHasIsNotScripted()
    {
        Assert.AreEqual(0, Statements(Build("CREATE SCHEMA a;"), Build("CREATE SCHEMA a;")).Count);
    }

    [TestMethod]
    public void DeclaringPublicScriptsNothing()
    {
        Assert.AreEqual(0, Statements(DatabaseModel.Empty, Build("CREATE SCHEMA IF NOT EXISTS public;")).Count);
    }

    private static List<string> Statements(DatabaseModel oldModel, DatabaseModel newModel)
    {
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));
        return batch.Operations.Select(static operation => operation.GenerateSql().Trim().Replace("\r\n", "\n", StringComparison.Ordinal)).ToList();
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
