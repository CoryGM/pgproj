using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-275: a quoted type name that differs from a built-in only by case (<c>"Name"</c>, <c>"Date"</c>) was taken for the built-in, so what used
/// it got no dependency on the project's type and could be created first. Built-in names are now matched as PostgreSQL matches them: the
/// parser has already folded an unquoted name to lower case, so a quoted name with a capital is never a built-in.
/// </summary>
[TestClass]
public sealed class BuiltInTypeNameCaseTests
{
    private const string Domains = """
        CREATE DOMAIN public."Name" AS character varying(50);
        CREATE DOMAIN public."Date" AS date;
        """;

    private static DatabaseModel Build(string sql)
    {
        var parser = new PgQueryParser();
        return new SemanticPipeline().Execute([parser.Parse(sql), parser.Parse(Domains)]);
    }

    [TestMethod]
    public void AColumnOfAQuotedTypeNamedLikeABuiltInUsesTheProjectsType()
    {
        var model = Build("""CREATE SCHEMA hr; CREATE TABLE hr.t (id int PRIMARY KEY, n "Name" NOT NULL, d "Date"[]);""");

        Assert.IsFalse(model.HasErrors, string.Join(Environment.NewLine, model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        var table = model.Tables["hr.t"];
        CollectionAssert.AreEqual(new[] { "public.Name" }, table.Columns.Single(static column => column.Name == "n").ReferencedTypes.ToArray());
        CollectionAssert.AreEqual(new[] { "public.Date" }, table.Columns.Single(static column => column.Name == "d").ReferencedTypes.ToArray());
        CollectionAssert.IsSubsetOf(new[] { "public.Name", "public.Date" }, table.ReferencedTypes.ToArray());
    }

    [TestMethod]
    public void ARoutinesParameterAndResultOfSuchATypeUseTheProjectsType()
    {
        var model = Build("""CREATE FUNCTION public.f(p "Name") RETURNS "Date" LANGUAGE sql AS $$ SELECT current_date $$;""");

        Assert.IsFalse(model.HasErrors, string.Join(Environment.NewLine, model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        CollectionAssert.IsSubsetOf(new[] { "public.Name", "public.Date" }, model.Functions.Values.Single().ReferencedTypes.ToArray());
    }

    [TestMethod]
    public void AnUnquotedOrLowerCaseQuotedBuiltInIsStillTheBuiltIn()
    {
        var model = Build("""CREATE TABLE public.t (a NAME, b TEXT, c "text", d VARCHAR(10), e Timestamp With Time Zone, f DATE, g "date");""");

        Assert.IsFalse(model.HasErrors, string.Join(Environment.NewLine, model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.IsEmpty(model.Diagnostics);
        var references = model.Tables["public.t"].Columns.SelectMany(static column => column.ReferencedTypes).ToArray();
        Assert.IsFalse(references.Any(static reference => reference.StartsWith("public.", StringComparison.Ordinal)), string.Join(", ", references));
    }

    [TestMethod]
    public void AQuotedTypeNamedLikeABuiltInThatTheProjectLacksIsMissing()
    {
        // PostgreSQL refuses "Text" unless such a type exists; it was taken for text and passed build.
        var model = new SemanticPipeline().Execute([new PgQueryParser().Parse("""CREATE TABLE public.t (a "Text");""")]);

        Assert.IsTrue(
            model.Diagnostics.Any(static diagnostic => diagnostic.Message.Contains("Text", StringComparison.Ordinal)),
            string.Join(Environment.NewLine, model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }
}
