namespace PgProj.Parser.Tests;

/// <summary>
/// G-131: <c>CREATE CAST</c> is read instead of being warned about and ignored (G-106): the two types, the function and its argument types,
/// and when the cast applies. A cast belongs to no schema.
/// </summary>
[TestClass]
public sealed class CastParsingTests
{
    [TestMethod]
    public void ACastWithAFunctionIsReadWithItsTypesAndFunction()
    {
        // Act
        var tree = new PgQueryParser().Parse("CREATE CAST (text AS public.mood) WITH FUNCTION public.text_to_mood(text);");
        var cast = tree.Casts.Single();

        // Assert
        Assert.AreEqual(0, tree.Diagnostics.Count);
        Assert.AreEqual("text", cast.SourceType);
        Assert.AreEqual("public.mood", cast.TargetType);
        Assert.AreEqual("public.text_to_mood", cast.FunctionName);
        CollectionAssert.AreEqual(new[] { "text" }, cast.FunctionArguments.ToArray());
        Assert.AreEqual("explicit", cast.Context);
        Assert.IsFalse(cast.IsInOut);
        Assert.IsNull(cast.SchemaName);
    }

    [TestMethod]
    [DataRow("CREATE CAST (public.a AS public.b) WITH FUNCTION f(public.a) AS ASSIGNMENT;", "assignment")]
    [DataRow("CREATE CAST (public.a AS public.b) WITH FUNCTION f(public.a) AS IMPLICIT;", "implicit")]
    [DataRow("CREATE CAST (public.a AS public.b) WITH FUNCTION f(public.a);", "explicit")]
    public void TheContextIsRead(string sql, string context)
    {
        // Act
        var cast = new PgQueryParser().Parse(sql).Casts.Single();

        // Assert
        Assert.AreEqual(context, cast.Context);
    }

    [TestMethod]
    public void WithoutFunctionAndWithInOutHaveNoFunction()
    {
        // Act
        var binary = new PgQueryParser().Parse("CREATE CAST (public.a AS public.b) WITHOUT FUNCTION AS IMPLICIT;").Casts.Single();
        var inOut = new PgQueryParser().Parse("CREATE CAST (public.a AS public.b) WITH INOUT;").Casts.Single();

        // Assert
        Assert.IsNull(binary.FunctionName);
        Assert.IsFalse(binary.IsInOut);
        Assert.AreEqual("implicit", binary.Context);
        Assert.IsNull(inOut.FunctionName);
        Assert.IsTrue(inOut.IsInOut);
    }

    [TestMethod]
    public void ATypeIsRenderedFromItsTreeWithArrayBoundsAndNoModifiers()
    {
        // Act
        var cast = new PgQueryParser().Parse("CREATE CAST (integer[] AS varchar(10)) WITH FUNCTION s.f(integer[], text);").Casts.Single();

        // Assert
        Assert.AreEqual("pg_catalog.int4[]", cast.SourceType);
        Assert.AreEqual("pg_catalog.varchar", cast.TargetType);
        Assert.AreEqual("s.f", cast.FunctionName);
        CollectionAssert.AreEqual(new[] { "pg_catalog.int4[]", "text" }, cast.FunctionArguments.ToArray());
    }

    [TestMethod]
    public void DropAndCommentOnACastAreStillReportedAsNotModeled()
    {
        // Act
        var diagnostics = new PgQueryParser().Parse("DROP CAST (text AS public.mood); COMMENT ON CAST (text AS public.mood) IS 'x';").Diagnostics;

        // Assert
        Assert.AreEqual(2, diagnostics.Count(static d => d.Severity == SqlDiagnosticSeverity.Warning && d.Message.Contains("is not modeled", StringComparison.Ordinal)));
    }
}
