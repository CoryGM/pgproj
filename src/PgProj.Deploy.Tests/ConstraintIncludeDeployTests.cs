using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>G-200: a key or exclusion constraint is created with its <c>INCLUDE (...)</c>, before its <c>WITH (...)</c>, as PostgreSQL writes it.</summary>
[TestClass]
[DoNotParallelize]
public sealed class ConstraintIncludeDeployTests
{
    [TestMethod]
    public void TheIncludeIsDeployed()
    {
        var sql = Script("CREATE TABLE t (id int, a int, \"B\" int, r int4range, CONSTRAINT t_pk PRIMARY KEY (id) INCLUDE (a) WITH (fillfactor = 70), CONSTRAINT t_u UNIQUE (a) INCLUDE (\"B\"), CONSTRAINT t_x EXCLUDE USING gist (r WITH &&) INCLUDE (a));");

        StringAssert.Contains(sql, "PRIMARY KEY (\"id\") INCLUDE (\"a\") WITH (fillfactor = 70)");
        StringAssert.Contains(sql, "UNIQUE (\"a\") INCLUDE (\"B\")");
        StringAssert.Contains(sql, "INCLUDE (\"a\")");
    }

    private static string Script(string sql)
    {
        var model = new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(DatabaseModel.Empty, model), new DependencyAnalyzer().Analyze(DatabaseModel.Empty, model));
        return string.Join("\n", batch.Operations.Select(static operation => operation.GenerateSql()));
    }
}
