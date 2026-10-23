using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>G-211: a routine in a language pgproj does not read is compared by its body as text: a change to the code is a change, whitespace alone is not; a C function's link symbol is compared too.</summary>
[TestClass]
[DoNotParallelize]
public sealed class RoutineLanguageBodyDiffTests
{
    private const string F = "CREATE FUNCTION public.add(a int, b int) RETURNS int LANGUAGE plperl AS $${0}$$;";

    [TestMethod]
    public void AChangedBodyIsAChangeBothWays()
    {
        Assert.AreEqual(1, ModifiedCount(string.Format(F, " return $_[0] + $_[1]; "), string.Format(F, " return $_[0] + $_[1] + 1; ")));
        Assert.AreEqual(1, ModifiedCount(string.Format(F, " return $_[0] + $_[1] + 1; "), string.Format(F, " return $_[0] + $_[1]; ")));
    }

    [TestMethod]
    public void WhitespaceAloneIsNoChange()
    {
        Assert.AreEqual(0, ModifiedCount(string.Format(F, " return $_[0] + $_[1]; "), string.Format(F, "\n    return $_[0] + $_[1];\n")));
    }

    [TestMethod]
    public void ACFunctionsLinkSymbolIsComparedAndDeployed()
    {
        const string c = "CREATE FUNCTION public.h() RETURNS language_handler LANGUAGE c AS '$libdir/plpgsql', '{0}';";

        Assert.AreEqual("plpgsql_call_handler", Build(string.Format(c, "plpgsql_call_handler")).Functions.Values.Single().Signature!.LinkSymbol);
        Assert.AreEqual(1, ModifiedCount(string.Format(c, "plpgsql_call_handler"), string.Format(c, "plpgsql_validator")));
        Assert.AreEqual(0, ModifiedCount(string.Format(c, "plpgsql_call_handler"), string.Format(c, "plpgsql_call_handler")));
    }

    private static int ModifiedCount(string oldSql, string newSql)
        => new DiffEngine().Diff(Build(oldSql), Build(newSql)).Modified.Count;

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
