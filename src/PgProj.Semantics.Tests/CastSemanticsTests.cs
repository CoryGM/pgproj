using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-131: a cast is a model object of its own, keyed by its pair of types in comparison form, so <c>integer</c> and <c>pg_catalog.int4</c> name one cast;
/// the function and the user-defined types are dependencies.
/// </summary>
[TestClass]
public sealed class CastSemanticsTests
{
    private const string Types = "CREATE TYPE public.mood AS ENUM ('a', 'b'); CREATE FUNCTION public.text_to_mood(text) RETURNS public.mood LANGUAGE sql AS $$ SELECT $1::public.mood $$; ";

    [TestMethod]
    public void ACastIsInTheModelKeyedByItsCanonicalTypes()
    {
        // Act
        var model = Build(Types + "CREATE CAST (text AS public.mood) WITH FUNCTION public.text_to_mood(text) AS ASSIGNMENT;");

        // Assert
        var cast = model.Casts["cast:(text AS mood)"];
        Assert.AreEqual("function", cast.Method);
        Assert.AreEqual("assignment", cast.Context);
        Assert.AreEqual("public.text_to_mood", cast.FunctionName);
        CollectionAssert.AreEqual(new[] { "text" }, cast.NormalizedFunctionArguments.ToArray());
        CollectionAssert.AreEqual(new[] { "public.text_to_mood" }, cast.ReferencedFunctions.ToArray());
        Assert.AreEqual(0, model.Diagnostics.Count, string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
    }

    [TestMethod]
    [DataRow("integer", "pg_catalog.int4")]
    [DataRow("varchar(10)", "character varying")]
    [DataRow("public.mood", "mood")]
    public void TheSameTypeWrittenTwoWaysIsOneKey(string first, string second)
    {
        Assert.AreEqual(CastTypeNames.CreateKey(first, "text"), CastTypeNames.CreateKey(second, "text"));
    }

    [TestMethod]
    [DataRow("WITHOUT FUNCTION", "binary")]
    [DataRow("WITH INOUT", "inout")]
    public void TheMethodIsInTheModel(string clause, string method)
    {
        // Act
        var cast = Build($"CREATE TYPE public.a AS ENUM ('x'); CREATE TYPE public.b AS ENUM ('x'); CREATE CAST (public.a AS public.b) {clause};").Casts.Values.Single();

        // Assert
        Assert.AreEqual(method, cast.Method);
        Assert.IsEmpty(cast.ReferencedFunctions);
    }

    [TestMethod]
    public void AnArrayTypeIsPartOfTheKey()
    {
        Assert.AreNotEqual(CastTypeNames.CreateKey("integer[]", "text"), CastTypeNames.CreateKey("integer", "text"));
    }

    [TestMethod]
    public void TwoCastsBetweenTheSameTypesAreAnError()
    {
        // Act
        var model = Build(Types + "CREATE CAST (text AS public.mood) WITH FUNCTION public.text_to_mood(text); CREATE CAST (pg_catalog.text AS mood) WITH FUNCTION public.text_to_mood(text) AS IMPLICIT;");

        // Assert
        Assert.AreEqual(1, model.Diagnostics.Count(static d => d.Severity == SemanticDiagnosticSeverity.Error && d.Message.Contains("Duplicate Cast", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ACastIsAModelNodeSoTheGraphSeesIt()
    {
        // Act
        var model = Build(Types + "CREATE CAST (text AS public.mood) WITH FUNCTION public.text_to_mood(text);");

        // Assert
        Assert.IsTrue(model.GetAllNodes().Any(static node => node is CastDefinition));
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
