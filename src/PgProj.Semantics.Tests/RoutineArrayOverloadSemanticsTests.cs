using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-49: an array parameter is part of a routine's identity, and an array of a user-defined type must still
/// resolve and count as a dependency on that type.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RoutineArrayOverloadSemanticsTests
{
    [TestMethod]
    public void ScalarAndArrayOverloadsAreDistinctFunctions()
    {
        var model = BuildModel("""
            create function public.f(a integer) returns integer language sql as $$ select a $$;
            create function public.f(a integer[]) returns integer language sql as $$ select a[1] $$;
            """);

        Assert.IsFalse(model.HasErrors, string.Join(Environment.NewLine, model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        CollectionAssert.AreEquivalent(new[] { "public.f(int4)", "public.f(int4[])" }, model.Functions.Keys.ToArray());
    }

    [TestMethod]
    public void ScalarAndArrayProcedureOverloadsAreDistinct()
    {
        var model = BuildModel("""
            create procedure public.p(a text) language sql as $$ select 1 $$;
            create procedure public.p(a text[]) language sql as $$ select 1 $$;
            """);

        Assert.IsFalse(model.HasErrors, string.Join(Environment.NewLine, model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        CollectionAssert.AreEquivalent(new[] { "public.p(text)", "public.p(text[])" }, model.Procedures.Keys.ToArray());
    }

    [TestMethod]
    public void EquivalentArraySpellingsAreTheSameOverload()
    {
        var model = BuildModel("""
            create function public.f(a integer[]) returns integer language sql as $$ select 1 $$;
            create function public.f(a int[3]) returns integer language sql as $$ select 2 $$;
            """);

        Assert.IsTrue(model.Diagnostics.Any(static diagnostic => diagnostic.Message.Contains("Duplicate Function definition", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ArrayOfAUserDefinedTypeRaisesNoErrorAndKeepsItsBrackets()
    {
        // (That a routine depends on its parameter types for ordering is a separate gap, G-50, for scalars and arrays alike.)
        var model = BuildModel("""
            create type public.mood as enum ('happy', 'sad');
            create function public.f(m public.mood[]) returns public.mood[] language sql as $$ select m $$;
            """);

        Assert.IsFalse(model.HasErrors, string.Join(Environment.NewLine, model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.AreEqual("public.f(mood[])", model.Functions.Keys.Single());
    }

    [TestMethod]
    public void ArrayParametersOfBuiltInTypesRaiseNoMissingTypeErrors()
    {
        var model = BuildModel("create function public.f(a float8[], b bpchar[], c numeric[], d timestamptz[]) returns integer language sql as $$ select 1 $$;");

        Assert.IsFalse(model.HasErrors, string.Join(Environment.NewLine, model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    private static DatabaseModel BuildModel(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
