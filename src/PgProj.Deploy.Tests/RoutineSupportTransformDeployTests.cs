using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>G-205: <c>SUPPORT</c> and <c>TRANSFORM</c> are written into <c>CREATE FUNCTION</c> as the project wrote them, and removing one is a <c>CREATE OR REPLACE</c>.</summary>
[TestClass]
[DoNotParallelize]
public sealed class RoutineSupportTransformDeployTests
{
    [TestMethod]
    public void BothClausesAreDeployedAsWritten()
    {
        var sql = Statements(DatabaseModel.Empty, Build("create function public.g(int) returns int language plpgsql support pg_catalog.int4_support transform for type int, FOR TYPE text as $$ begin return 1; end $$;")).Single();

        StringAssert.Contains(sql, "LANGUAGE plpgsql\nsupport pg_catalog.int4_support\ntransform for type int, FOR TYPE text\nAS $$");
    }

    [TestMethod]
    public void RemovingSupportIsACreateOrReplaceWithoutIt()
    {
        const string f = "create function public.f(text, text) returns bool language internal immutable strict {0} as 'textlike';";

        var statements = Statements(Build(string.Format(f, "support textlike_support")), Build(string.Format(f, string.Empty)));

        Assert.HasCount(1, statements, string.Join("\n", statements));
        StringAssert.StartsWith(statements[0], "CREATE OR REPLACE FUNCTION");
        Assert.IsFalse(statements[0].Contains("support", StringComparison.OrdinalIgnoreCase), statements[0]);
    }

    private static List<string> Statements(DatabaseModel oldModel, DatabaseModel newModel)
        => new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel)).Operations
            .Where(static operation => operation is not SchemaCreateOperation)
            .Select(static operation => operation.GenerateSql().Trim())
            .ToList();

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
