namespace PgProj.Parser.Tests;

/// <summary>G-205: a routine's <c>SUPPORT</c> and <c>TRANSFORM FOR TYPE</c> were not read, so they were left out of <c>CREATE FUNCTION</c>. Each keeps its text as written.</summary>
[TestClass]
public sealed class RoutineSupportTransformParsingTests
{
    [TestMethod]
    public void SupportAndTransformAreReadWithTheirWrittenText()
    {
        var signature = new PgQueryParser().Parse("create function f(text) returns bool language plpgsql support pg_catalog.textlike_support transform for type hstore, FOR TYPE \"My T\" as $$ begin return true; end $$;")
            .Functions.Single().Signature!;

        Assert.AreEqual("pg_catalog.textlike_support", signature.SupportFunction);
        Assert.AreEqual("support pg_catalog.textlike_support", signature.WrittenSupportClause);
        CollectionAssert.AreEqual(new[] { "hstore", "My T" }, signature.TransformTypes.ToArray());
        Assert.AreEqual("transform for type hstore, FOR TYPE \"My T\"", signature.WrittenTransformClause);
    }

    [TestMethod]
    public void WithoutWrittenTextTheClausesAreBuiltFromTheirParts()
    {
        var signature = new RoutineSignature { SupportFunction = "pg_catalog.textlike_support", TransformTypes = ["hstore"] };

        Assert.AreEqual("SUPPORT pg_catalog.textlike_support", signature.RenderSupportClause());
        Assert.AreEqual("TRANSFORM FOR TYPE hstore", signature.RenderTransformClause());
    }

    [TestMethod]
    public void ARoutineWithNeitherHasNone()
    {
        var signature = new PgQueryParser().Parse("create function f() returns int language sql as $$ select 1 $$;").Functions.Single().Signature!;

        Assert.IsNull(signature.RenderSupportClause());
        Assert.IsNull(signature.RenderTransformClause());
    }
}
