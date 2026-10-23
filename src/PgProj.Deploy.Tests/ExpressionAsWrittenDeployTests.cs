using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-207 step 2: what is deployed is what the project wrote. An expression the parser used to rebuild from its tree, wrongly for
/// <c>CASE</c>, <c>ARRAY[...]</c>, an array cast, or <c>current_user</c>, is deployed exactly as written: a routine parameter's default
/// (G-202), an exclusion constraint's expression element (G-208), an extended statistics expression (G-209), and a publication's row
/// filter (G-210).
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ExpressionAsWrittenDeployTests
{
    [TestMethod]
    public void ARoutineParameterDefaultIsDeployedAsWritten()
    {
        var sql = Script("CREATE FUNCTION f(a text[] DEFAULT '{a,b}'::text[], b int[] DEFAULT ARRAY[1,2], c int DEFAULT CASE WHEN true THEN 1 ELSE 2 END, d text DEFAULT current_user) RETURNS int LANGUAGE sql AS $$ SELECT 1 $$;");

        StringAssert.Contains(sql, "DEFAULT '{a,b}'::text[], ");
        StringAssert.Contains(sql, "DEFAULT ARRAY[1,2], ");
        StringAssert.Contains(sql, "DEFAULT CASE WHEN true THEN 1 ELSE 2 END, ");
        StringAssert.Contains(sql, "DEFAULT current_user)");
    }

    [TestMethod]
    public void AnExclusionExpressionIsDeployedAsWritten()
    {
        var sql = Script("CREATE TABLE t (a int, r int4range, EXCLUDE USING gist ((CASE WHEN a > 0 THEN 1 ELSE 2 END) WITH =, r WITH &&));");

        StringAssert.Contains(sql, "(CASE WHEN a > 0 THEN 1 ELSE 2 END) WITH =");
    }

    [TestMethod]
    public void AStatisticsExpressionIsDeployedAsWritten()
    {
        var sql = Script("CREATE TABLE t (a int, b int); CREATE STATISTICS s ON (CASE WHEN a > 0 THEN 1 ELSE 2 END), b FROM t;");

        StringAssert.Contains(sql, "(CASE WHEN a > 0 THEN 1 ELSE 2 END)");
    }

    [TestMethod]
    public void APublicationRowFilterIsDeployedAsWritten()
    {
        var sql = Script("CREATE TABLE t (a int, b text); CREATE PUBLICATION p FOR TABLE t WHERE (CASE WHEN a > 0 THEN true ELSE false END AND b <> 'x');");

        StringAssert.Contains(sql, "WHERE (CASE WHEN a > 0 THEN true ELSE false END AND b <> 'x')");
    }

    private static string Script(string sql)
    {
        var model = new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(DatabaseModel.Empty, model), new DependencyAnalyzer().Analyze(DatabaseModel.Empty, model));
        return string.Join("\n", batch.Operations.Select(static operation => operation.GenerateSql()));
    }
}
