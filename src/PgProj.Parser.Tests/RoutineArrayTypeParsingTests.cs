namespace PgProj.Parser.Tests;

/// <summary>
/// G-49: routine parameter and return types must keep their array-ness. It was dropped, so
/// <c>f(integer)</c> and <c>f(integer[])</c> collided and the regenerated header lost the <c>[]</c>.
/// </summary>
[TestClass]
public sealed class RoutineArrayTypeParsingTests
{
    [TestMethod]
    [DataRow("a integer[]", "pg_catalog.int4[]")]
    [DataRow("a integer ARRAY", "pg_catalog.int4[]")]
    [DataRow("a integer[][]", "pg_catalog.int4[]")]
    [DataRow("a integer[3]", "pg_catalog.int4[]")]
    [DataRow("a text[]", "text[]")]
    [DataRow("a public.mood[]", "public.mood[]")]
    [DataRow("VARIADIC a integer[]", "pg_catalog.int4[]")]
    [DataRow("a integer", "pg_catalog.int4")]
    [DataRow("a text", "text")]
    [DataRow("a numeric(10,2)", "pg_catalog.numeric")]
    [DataRow("a integer, b text[]", "pg_catalog.int4, text[]")]
    public void ParameterTypesKeepArrayness(string parameters, string expectedDataTypes)
    {
        var signature = ParseFunction($"create function public.f({parameters}) returns integer language sql as $$ select 1 $$;");

        Assert.AreEqual(expectedDataTypes, string.Join(", ", signature.Parameters.Select(static parameter => parameter.DataType)));
    }

    [TestMethod]
    [DataRow("integer[]", "pg_catalog.int4[]", false)]
    [DataRow("text[]", "text[]", false)]
    [DataRow("integer", "pg_catalog.int4", false)]
    [DataRow("setof integer[]", "pg_catalog.int4[]", true)]
    [DataRow("setof integer", "pg_catalog.int4", true)]
    public void ReturnTypesKeepArrayness(string returns, string expectedReturnType, bool expectedSet)
    {
        var signature = ParseFunction($"create function public.f() returns {returns} language sql as $$ select null $$;");

        Assert.AreEqual(expectedReturnType, signature.ReturnType);
        Assert.AreEqual(expectedSet, signature.ReturnsSet);
    }

    [TestMethod]
    public void ArgumentTypeSignatureDistinguishesArrayOverloads()
    {
        var scalar = ParseFunction("create function public.f(a integer) returns integer language sql as $$ select 1 $$;");
        var array = ParseFunction("create function public.f(a integer[]) returns integer language sql as $$ select 1 $$;");

        Assert.AreEqual("pg_catalog.int4", scalar.RenderArgumentTypeSignature());
        Assert.AreEqual("pg_catalog.int4[]", array.RenderArgumentTypeSignature());
    }

    [TestMethod]
    public void RegeneratedParameterListKeepsTheBrackets()
    {
        var signature = ParseFunction("create function public.f(a integer[], VARIADIC b text[]) returns integer language sql as $$ select 1 $$;");

        Assert.AreEqual("a integer[], VARIADIC b text[]", signature.RenderParameterList());
    }

    [TestMethod]
    public void ProcedureParametersKeepArrayness()
    {
        var tree = new PgQueryParser().Parse("create procedure public.p(a integer[]) language sql as $$ select 1 $$;");

        Assert.AreEqual("pg_catalog.int4[]", tree.Procedures.Single().Signature!.RenderArgumentTypeSignature());
    }

    private static RoutineSignature ParseFunction(string sql)
        => new PgQueryParser().Parse(sql).Functions.Single().Signature!;
}
