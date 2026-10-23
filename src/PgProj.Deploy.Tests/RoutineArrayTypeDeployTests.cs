using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-49: regenerated routine headers and DROP statements must carry the array brackets, or an array-parameter
/// function deploys with the wrong signature and cannot be dropped by name and argument types.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RoutineArrayTypeDeployTests
{
    [TestMethod]
    public void CreateHeaderKeepsArrayParametersAndReturnType()
    {
        var script = ScriptFor(DatabaseModel.Empty, BuildModel("""
            create function public.f(a integer[], VARIADIC b text[]) returns numeric[] language sql as $$ select null $$;
            """));

        StringAssert.Contains(script, "\"public\".\"f\"(a integer[], VARIADIC b text[])");
        StringAssert.Contains(script, "RETURNS numeric[]");
    }

    [TestMethod]
    public void ScalarAndArrayOverloadsBothDeploy()
    {
        var script = ScriptFor(DatabaseModel.Empty, BuildModel("""
            create function public.f(a integer) returns integer language sql as $$ select a $$;
            create function public.f(a integer[]) returns integer language sql as $$ select a[1] $$;
            """));

        StringAssert.Contains(script, "\"public\".\"f\"(a integer)");
        StringAssert.Contains(script, "\"public\".\"f\"(a integer[])");
    }

    [TestMethod]
    public void DropNamesTheArrayOverload()
    {
        var oldModel = BuildModel("""
            create function public.f(a integer) returns integer language sql as $$ select a $$;
            create function public.f(a integer[]) returns integer language sql as $$ select a[1] $$;
            """);
        var newModel = BuildModel("create function public.f(a integer) returns integer language sql as $$ select a $$;");

        var script = ScriptFor(oldModel, newModel);

        StringAssert.Contains(script, "DROP FUNCTION \"public\".\"f\"(integer[]);");
        Assert.IsFalse(script.Contains("DROP FUNCTION \"public\".\"f\"(pg_catalog.int4);", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void DropProcedureNamesTheArrayOverload()
    {
        var oldModel = BuildModel("create procedure public.p(a text[]) language sql as $$ select 1 $$;");

        var script = ScriptFor(oldModel, DatabaseModel.Empty);

        StringAssert.Contains(script, "DROP PROCEDURE \"public\".\"p\"(text[]);");
    }

    private static string ScriptFor(DatabaseModel oldModel, DatabaseModel newModel)
    {
        var generator = new ScriptGenerator();
        return generator.GenerateSqlScript(generator.Generate(
            new DiffEngine().Diff(oldModel, newModel),
            new DependencyAnalyzer().Analyze(oldModel, newModel)));
    }

    private static DatabaseModel BuildModel(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
