using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-49: scalar and array overloads are different routines, and equivalent array spellings are the same one.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RoutineArrayOverloadDiffTests
{
    [TestMethod]
    public void SelfDiffIsEmpty()
    {
        var model = BuildModel("integer[]");

        AssertNoFunctionChanges(new DiffEngine().Execute(model, model));
    }

    [TestMethod]
    [DataRow("integer[]", "int[]")]
    [DataRow("integer[]", "integer[3]")]
    [DataRow("integer[]", "integer ARRAY")]
    public void EquivalentArraySpellingsProduceNoChange(string oldParameter, string newParameter)
    {
        AssertNoFunctionChanges(new DiffEngine().Execute(BuildModel(oldParameter), BuildModel(newParameter)));
    }

    [TestMethod]
    public void ChangingAScalarParameterToAnArrayReplacesTheOverload()
    {
        var result = new DiffEngine().Execute(BuildModel("integer"), BuildModel("integer[]"));

        Assert.HasCount(1, result.AddedFunctions);
        Assert.AreEqual("public.f(int4[])", result.AddedFunctions[0].Key);
        Assert.HasCount(1, result.RemovedFunctions);
        Assert.AreEqual("public.f(int4)", result.RemovedFunctions[0].Key);
        Assert.IsEmpty(result.ModifiedFunctions);
    }

    [TestMethod]
    public void AddingAnArrayOverloadKeepsTheScalarOne()
    {
        var oldModel = BuildModel("integer");
        var newModel = new SemanticPipeline().Execute([new PgQueryParser().Parse("""
            create function public.f(a integer) returns integer language sql as $$ select 1 $$;
            create function public.f(a integer[]) returns integer language sql as $$ select 1 $$;
            """)]);

        var result = new DiffEngine().Execute(oldModel, newModel);

        Assert.HasCount(1, result.AddedFunctions);
        Assert.AreEqual("public.f(int4[])", result.AddedFunctions[0].Key);
        Assert.IsEmpty(result.RemovedFunctions);
        Assert.IsEmpty(result.ModifiedFunctions);
    }

    private static void AssertNoFunctionChanges(DiffResult result)
    {
        Assert.IsEmpty(result.AddedFunctions, string.Join(", ", result.AddedFunctions.Select(static function => function.Key)));
        Assert.IsEmpty(result.RemovedFunctions, string.Join(", ", result.RemovedFunctions.Select(static function => function.Key)));
        Assert.IsEmpty(result.ModifiedFunctions);
    }

    private static DatabaseModel BuildModel(string parameterType)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse($"create function public.f(a {parameterType}) returns integer language sql as $$ select 1 $$;")]);
}
