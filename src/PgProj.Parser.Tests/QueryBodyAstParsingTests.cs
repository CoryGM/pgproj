namespace PgProj.Parser.Tests;

/// <summary>
/// G-90: a view carries pg_query's tree for its query, and a SQL-language routine its body's statements, so what they read is bound from
/// the tree instead of the text.
/// </summary>
[TestClass]
public sealed class QueryBodyAstParsingTests
{
    [TestMethod]
    [DataRow("CREATE VIEW public.v AS SELECT id FROM public.o;")]
    [DataRow("CREATE MATERIALIZED VIEW public.v AS SELECT id FROM public.o;")]
    public void AViewCarriesItsQueryTree(string sql)
    {
        var view = new PgQueryParser().Parse(sql).Views.Single();

        StringAssert.StartsWith(view.QueryAst, "{\"SelectStmt\"");
        StringAssert.Contains(view.QueryAst, "\"relname\":\"o\"");
    }

    [TestMethod]
    public void ASqlFunctionCarriesItsBodysStatements()
    {
        var function = new PgQueryParser().Parse("CREATE FUNCTION public.f() RETURNS int LANGUAGE sql AS $$ SELECT 1; SELECT count(*)::int FROM public.o $$;").Functions.Single();

        StringAssert.StartsWith(function.BodyAst, "[");
        StringAssert.Contains(function.BodyAst, "\"relname\":\"o\"");
    }

    [TestMethod]
    public void ABeginAtomicBodyCarriesItsTree()
    {
        var function = new PgQueryParser().Parse("CREATE FUNCTION public.f() RETURNS bigint LANGUAGE sql BEGIN ATOMIC SELECT count(*) FROM public.o; END;").Functions.Single();

        StringAssert.Contains(function.BodyAst, "\"relname\":\"o\"");
    }

    [TestMethod]
    public void ASqlProcedureCarriesItsBodysStatements()
        => StringAssert.Contains(
            new PgQueryParser().Parse("CREATE PROCEDURE public.p() LANGUAGE sql AS $$ DELETE FROM public.o $$;").Procedures.Single().BodyAst,
            "\"relname\":\"o\"");

    [TestMethod]
    [DataRow("CREATE FUNCTION public.f() RETURNS int LANGUAGE plpgsql AS $$ BEGIN RETURN 1; END $$;")]
    [DataRow("CREATE FUNCTION public.f() RETURNS int LANGUAGE sql AS $$ SELECT FROM WHERE $$;")]
    public void AnotherLanguageOrAnUnparsableBodyHasNoTree(string sql)
        => Assert.IsNull(new PgQueryParser().Parse(sql).Functions.Single().BodyAst);
}
