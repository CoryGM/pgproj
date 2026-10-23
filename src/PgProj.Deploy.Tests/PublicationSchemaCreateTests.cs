using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-231: a <c>CREATE PUBLICATION ... FOR TABLES IN SCHEMA s</c> needs <c>s</c> to exist, though nothing else of the project may be in it. The schema is created first,
/// as for any object created in one.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class PublicationSchemaCreateTests
{
    [TestMethod]
    public void ThePublicationsSchemaIsCreatedBeforeThePublication()
    {
        var statements = Statements(DatabaseModel.Empty, Build("CREATE PUBLICATION a_pub FOR TABLES IN SCHEMA z_pubschema; CREATE SCHEMA z_pubschema;"));

        Assert.AreEqual(2, statements.Count, string.Join("\n", statements));
        Assert.AreEqual("CREATE SCHEMA IF NOT EXISTS \"z_pubschema\";", statements[0]);
        StringAssert.StartsWith(statements[1], "CREATE PUBLICATION \"a_pub\"");
    }

    [TestMethod]
    public void APublicationOfTheDefaultSchemaOrOfTablesNeedsNoSchema()
    {
        var statements = Statements(DatabaseModel.Empty, Build("CREATE TABLE public.t (id int); CREATE PUBLICATION a_pub FOR TABLE public.t; CREATE PUBLICATION b_pub FOR TABLES IN SCHEMA public;"));

        Assert.IsFalse(statements.Any(static s => s.StartsWith("CREATE SCHEMA", StringComparison.Ordinal)), string.Join("\n", statements));
    }

    private static List<string> Statements(DatabaseModel oldModel, DatabaseModel newModel)
    {
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));
        return batch.Operations.Select(static operation => operation.GenerateSql().Trim().Replace("\r\n", "\n", StringComparison.Ordinal)).ToList();
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
