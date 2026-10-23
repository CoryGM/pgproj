using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-224: <c>CREATE UNLOGGED SEQUENCE</c> lost <c>UNLOGGED</c>. It is deployed, compared, and changed in place with <c>ALTER SEQUENCE ... SET LOGGED | UNLOGGED</c>,
/// a statement of its own; a serial's sequence on an unlogged table is unlogged, as PostgreSQL makes it.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SequenceUnloggedTests
{
    [TestMethod]
    public void AnUnloggedSequenceIsReadAndCreatedSo()
    {
        var model = Build("CREATE UNLOGGED SEQUENCE public.s INCREMENT BY 2;");
        Assert.IsTrue(model.Sequences["public.s"].IsUnlogged);

        StringAssert.Contains(ScriptFor(DatabaseModel.Empty, model), "CREATE UNLOGGED SEQUENCE \"public\".\"s\" INCREMENT BY 2");
    }

    [TestMethod]
    public void AChangeIsSetInPlaceInAStatementOfItsOwn()
    {
        var script = ScriptFor(Build("CREATE SEQUENCE public.s;"), Build("CREATE UNLOGGED SEQUENCE public.s INCREMENT BY 2;"));

        StringAssert.Contains(script, "ALTER SEQUENCE \"public\".\"s\" INCREMENT BY 2;", script);
        StringAssert.Contains(script, "ALTER SEQUENCE \"public\".\"s\" SET UNLOGGED;", script);
        Assert.IsFalse(script.Contains("DROP SEQUENCE", StringComparison.Ordinal), script);

        var back = ScriptFor(Build("CREATE UNLOGGED SEQUENCE public.s;"), Build("CREATE SEQUENCE public.s;"));
        StringAssert.Contains(back, "ALTER SEQUENCE \"public\".\"s\" SET LOGGED;", back);
    }

    [TestMethod]
    public void ASerialsSequenceFollowsAnUnloggedTable()
        => Assert.IsTrue(Build("CREATE UNLOGGED TABLE public.t (id serial);").Sequences["public.t_id_seq"].IsUnlogged);

    private static string ScriptFor(DatabaseModel oldModel, DatabaseModel newModel)
        => new ScriptGenerator().GenerateSqlScript(new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel)));

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
