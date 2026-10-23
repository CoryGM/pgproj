using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-204: <c>MULTIRANGE_TYPE_NAME</c> is deployed as written (it was left out, so the multirange got PostgreSQL's default name), and a table using
/// the multirange is created after the range that makes it.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class MultirangeNameDeployTests
{
    [TestMethod]
    public void TheNameIsDeployedAsWrittenAndItsUsersComeAfterTheRange()
    {
        var statements = Statements("""
            CREATE TABLE t (x s."Many Spans", m floatmulti);
            CREATE SCHEMA s;
            CREATE TYPE s."Spans" AS RANGE (subtype = int4, multirange_type_name = s."Many Spans");
            CREATE TYPE floatrange AS RANGE (subtype = float8, multirange_type_name = floatmulti);
            """);

        var spans = statements.FindIndex(static statement => statement.Contains("MULTIRANGE_TYPE_NAME = s.\"Many Spans\")", StringComparison.Ordinal));
        var floats = statements.FindIndex(static statement => statement.Contains("MULTIRANGE_TYPE_NAME = floatmulti)", StringComparison.Ordinal));
        var table = statements.FindIndex(static statement => statement.StartsWith("CREATE TABLE", StringComparison.Ordinal));
        Assert.IsTrue(spans >= 0 && floats >= 0, string.Join("\n", statements));
        Assert.IsTrue(table > spans && table > floats, string.Join("\n", statements));
    }

    private static List<string> Statements(string sql)
    {
        var model = new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
        return new ScriptGenerator().Generate(new DiffEngine().Diff(DatabaseModel.Empty, model), new DependencyAnalyzer().Analyze(DatabaseModel.Empty, model)).Operations
            .Select(static operation => operation.GenerateSql().Trim())
            .ToList();
    }
}
