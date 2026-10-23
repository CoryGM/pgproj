using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-198: a <c>RETURNS TABLE</c> function is created with its columns, as written (it was created as <c>RETURNS SETOF record</c>), and a changed
/// column drops and creates it, since PostgreSQL refuses <c>CREATE OR REPLACE</c> with a different row type.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ReturnsTableDeployTests
{
    private const string F = "CREATE FUNCTION public.f(p int) RETURNS TABLE ({0}) LANGUAGE sql STABLE AS $$ SELECT 1, 'a' $$;";

    [TestMethod]
    public void TheFunctionIsCreatedWithItsColumnsAsWritten()
    {
        var sql = Statements(DatabaseModel.Empty, Build(string.Format(F, "id INT, \"Name\" text"))).Single();

        StringAssert.StartsWith(sql, "CREATE OR REPLACE FUNCTION \"public\".\"f\"(p int)\nRETURNS TABLE (id INT, \"Name\" text)\nLANGUAGE sql");
    }

    [TestMethod]
    public void AChangedColumnDropsAndCreatesTheFunction()
    {
        var statements = Statements(Build(string.Format(F, "id int, name text")), Build(string.Format(F, "id int, name varchar")));

        Assert.HasCount(2, statements, string.Join("\n", statements));
        Assert.AreEqual("DROP FUNCTION \"public\".\"f\"(int);", statements[0]);
        StringAssert.Contains(statements[1], "RETURNS TABLE (id int, name varchar)");
    }

    private static List<string> Statements(DatabaseModel oldModel, DatabaseModel newModel)
        => new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel)).Operations
            .Where(static operation => operation is not SchemaCreateOperation)
            .Select(static operation => operation.GenerateSql().Trim())
            .ToList();

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
