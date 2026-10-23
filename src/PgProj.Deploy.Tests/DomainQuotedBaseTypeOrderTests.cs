using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-286: a domain over a quoted type (<c>"Zname"</c>) is created after the type and deployed as written. The issue expected the lower-cased base type to break
/// this order; it did not (the order held before the fix too), so this is coverage for the case, not a regression test.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class DomainQuotedBaseTypeOrderTests
{
    [TestMethod]
    public void ADomainIsCreatedAfterTheQuotedTypeItIsOver()
    {
        var statements = Statements(Build("""
            CREATE DOMAIN public.a_short AS "Zname" CHECK (length(VALUE) < 10);
            CREATE DOMAIN public."Zname" AS character varying(50);
            """));

        var zname = statements.FindIndex(static s => s.StartsWith("CREATE DOMAIN \"public\".\"Zname\"", StringComparison.Ordinal));
        var shortName = statements.FindIndex(static s => s.StartsWith("CREATE DOMAIN \"public\".\"a_short\"", StringComparison.Ordinal));
        Assert.IsTrue(zname >= 0 && shortName > zname, string.Join("\n", statements));
        StringAssert.Contains(statements[shortName], "AS \"Zname\"");
    }

    private static List<string> Statements(DatabaseModel newModel)
    {
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(DatabaseModel.Empty, newModel), new DependencyAnalyzer().Analyze(DatabaseModel.Empty, newModel));
        return batch.Operations.Select(static operation => operation.GenerateSql().Trim().Replace("\r\n", "\n", StringComparison.Ordinal)).ToList();
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
