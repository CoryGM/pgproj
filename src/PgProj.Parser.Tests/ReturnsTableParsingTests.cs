namespace PgProj.Parser.Tests;

/// <summary>
/// G-198: a <c>RETURNS TABLE (...)</c> function's columns were skipped by the parser, so the function was deployed as <c>RETURNS SETOF record</c>
/// with no columns (every call then failed) and a changed column was not seen. They are kept as parameters of mode <c>TABLE</c>: part of the
/// result, not of the arguments.
/// </summary>
[TestClass]
public sealed class ReturnsTableParsingTests
{
    private const string Sql = "CREATE FUNCTION f(p int) RETURNS TABLE (id INT, \"Name\" text, m public.mood) LANGUAGE sql AS $$ SELECT 1, 'a', NULL::public.mood $$;";

    [TestMethod]
    public void TheColumnsAreTableParameters()
    {
        var tree = new PgQueryParser().Parse(Sql);
        var signature = tree.Functions.Single().Signature!;

        CollectionAssert.AreEqual(new[] { "IN p", "TABLE id", "TABLE Name", "TABLE m" }, signature.Parameters.Select(static parameter => $"{parameter.Mode} {parameter.Name}").ToArray());
        Assert.AreEqual("INT", signature.Parameters[1].WrittenDataType);
        Assert.IsEmpty(tree.Diagnostics);
    }

    [TestMethod]
    public void TheColumnsAreNotArguments()
    {
        var signature = new PgQueryParser().Parse(Sql).Functions.Single().Signature!;

        Assert.AreEqual("p int", signature.RenderParameterList());
        Assert.AreEqual("pg_catalog.int4", signature.RenderArgumentTypeSignature());
        Assert.AreEqual("int", signature.RenderArgumentTypeSignatureSql());
    }

    [TestMethod]
    public void TheResultIsWrittenAsATable()
    {
        Assert.AreEqual("TABLE (id INT, \"Name\" text, m public.mood)", new PgQueryParser().Parse(Sql).Functions.Single().Signature!.RenderReturnsClause());
    }

    [TestMethod]
    public void AnOrdinaryResultIsUnchanged()
    {
        var signature = new PgQueryParser().Parse("CREATE FUNCTION f() RETURNS SETOF INT LANGUAGE sql AS $$ SELECT 1 $$;").Functions.Single().Signature!;

        Assert.AreEqual("SETOF INT", signature.RenderReturnsClause());
    }
}
