using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-232: from PostgreSQL 14 a procedure's <c>OUT</c> parameters are part of its signature, so two procedures that differ only by an <c>OUT</c> parameter both exist, and a
/// statement that names one by its types alone is refused as not unique. Procedures that share a name with an <c>OUT</c> overload are named with every parameter's mode.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ProcedureOutOverloadTests
{
    private const string Both = "CREATE PROCEDURE public.p(IN a int, IN b int, IN c int) LANGUAGE sql AS 'select 1'; CREATE PROCEDURE public.p(OUT a int, IN b int, IN c int) LANGUAGE sql AS 'select 2';";

    [TestMethod]
    public void EveryStatementThatNamesAnOverloadWithAnOutParameterSpellsOutTheModes()
    {
        var script = Script(DatabaseModel.Empty, Build(Both + "ALTER PROCEDURE public.p(IN a int, IN b int, IN c int) OWNER TO postgres; COMMENT ON PROCEDURE public.p(OUT a int, IN b int, IN c int) IS 'c';"));

        StringAssert.Contains(script, "ALTER PROCEDURE \"public\".\"p\"(IN int, IN int, IN int) OWNER TO");
        StringAssert.Contains(script, "COMMENT ON PROCEDURE \"public\".\"p\"(OUT int, IN int, IN int) IS 'c'");
    }

    [TestMethod]
    public void DroppingEitherOverloadNamesItUnambiguously()
    {
        var script = Script(Build(Both), DatabaseModel.Empty);

        StringAssert.Contains(script, "DROP PROCEDURE \"public\".\"p\"(IN int, IN int, IN int)");
        StringAssert.Contains(script, "DROP PROCEDURE \"public\".\"p\"(OUT int, IN int, IN int)");
    }

    [TestMethod]
    public void AProcedureWithNoOutOverloadIsNamedByItsTypesAsBefore()
    {
        var script = Script(Build("CREATE PROCEDURE public.p(IN a int, IN b int) LANGUAGE sql AS 'select 1'; CREATE PROCEDURE public.p(IN a text) LANGUAGE sql AS 'select 1'; COMMENT ON PROCEDURE public.p(int, int) IS 'c';"), DatabaseModel.Empty);

        StringAssert.Contains(script, "DROP PROCEDURE \"public\".\"p\"(int, int)");
        Assert.IsFalse(script.Contains("IN int", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void AProcedureWithAnOutParameterAndNoSameNamedOverloadIsNamedByItsInputsAsBefore()
    {
        var script = Script(Build("CREATE PROCEDURE public.p(OUT a int, IN b int) LANGUAGE sql AS 'select 1';"), DatabaseModel.Empty);

        StringAssert.Contains(script, "DROP PROCEDURE \"public\".\"p\"(int)");
    }

    private static string Script(DatabaseModel oldModel, DatabaseModel newModel)
    {
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));
        return string.Join("\n", batch.Operations.Where(static operation => operation is not SchemaCreateOperation).Select(static operation => operation.GenerateSql().Replace("\r\n", "\n", StringComparison.Ordinal))).Trim();
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
