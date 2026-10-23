using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// Regression coverage for G-29: function/procedure identity must include argument types so
/// PostgreSQL overloads (same schema-qualified name, different parameter types) do not collide
/// in the semantic model.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class OverloadedRoutineSemanticsTests
{
    [TestMethod]
    public void OverloadedFunctionsWithDifferentArgumentTypesGetDistinctKeys()
    {
        const string sql = """
            create function public.calc(a integer, b integer) returns integer language sql as $$select a + b;$$;
            create function public.calc(a text, b text) returns text language sql as $$select a || b;$$;
            """;

        var model = BuildModel(sql);

        Assert.IsFalse(model.HasErrors, string.Join(Environment.NewLine, model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.AreEqual(2, model.Functions.Count);
        Assert.IsTrue(model.Functions.ContainsKey("public.calc(int4,int4)"));
        Assert.IsTrue(model.Functions.ContainsKey("public.calc(text,text)"));
    }

    [TestMethod]
    public void OverloadedFunctionsDoNotProduceDuplicateDefinitionDiagnostic()
    {
        const string sql = """
            create function public.calc(a integer) returns integer language sql as $$select a;$$;
            create function public.calc(a integer, b integer) returns integer language sql as $$select a + b;$$;
            """;

        var model = BuildModel(sql);

        Assert.IsFalse(model.Diagnostics.Any(diagnostic => diagnostic.Message.Contains("Duplicate Function definition", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void OverloadedProceduresWithDifferentArgumentTypesGetDistinctKeys()
    {
        const string sql = """
            create procedure public.do_work(job_id integer) language plpgsql as $$begin null; end;$$;
            create procedure public.do_work(job_id text) language plpgsql as $$begin null; end;$$;
            """;

        var model = BuildModel(sql);

        Assert.IsFalse(model.HasErrors, string.Join(Environment.NewLine, model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.AreEqual(2, model.Procedures.Count);
        Assert.IsTrue(model.Procedures.ContainsKey("public.do_work(int4)"));
        Assert.IsTrue(model.Procedures.ContainsKey("public.do_work(text)"));
    }

    [TestMethod]
    public void AZeroArgumentFunctionsKeyHasEmptyParenthesesSoATableOfItsNameIsAnotherObject()
    {
        // G-228: a zero-argument routine was keyed "public.now_utc", a relation's key, so a table of the same name was the same node.
        const string sql = "create function public.now_utc() returns timestamp language sql as $$select now();$$; create table public.now_utc (a int);";

        var model = BuildModel(sql);

        Assert.IsTrue(model.Functions.ContainsKey("public.now_utc()"));
        Assert.IsTrue(model.Tables.ContainsKey("public.now_utc"));
    }

    private static DatabaseModel BuildModel(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
