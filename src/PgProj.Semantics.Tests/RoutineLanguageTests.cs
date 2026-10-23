using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-211: any routine language is accepted (the user's option A): PostgreSQL rejects one it does not have at publish. A routine in a language whose
/// body pgproj does not read (PL/Perl, PL/Python, PL/Tcl, PL/v8, ...) gets one warning, since the objects its body uses are not ordered before it;
/// <c>sql</c> and <c>plpgsql</c> bodies are read, and <c>internal</c> and <c>c</c> name compiled code.
/// </summary>
[TestClass]
public sealed class RoutineLanguageTests
{
    [TestMethod]
    [DataRow("plperl")]
    [DataRow("plpython3u")]
    [DataRow("pltcl")]
    [DataRow("plv8")]
    [DataRow("plsomethingnew")]
    public void ARoutineInALanguagePgprojDoesNotReadIsAcceptedWithOneWarning(string language)
    {
        var model = Build($"CREATE FUNCTION public.f(a int) RETURNS int LANGUAGE {language} AS $$ body $$; CREATE PROCEDURE public.p() LANGUAGE {language} AS $$ body $$;");

        Assert.IsFalse(model.Diagnostics.Any(static diagnostic => diagnostic.Severity == SemanticDiagnosticSeverity.Error), Dump(model));
        Assert.AreEqual(1, model.Diagnostics.Count(diagnostic => diagnostic.Message.StartsWith("Function 'public.f(int4)' is written in LANGUAGE " + language, StringComparison.Ordinal)), Dump(model));
        Assert.AreEqual(1, model.Diagnostics.Count(diagnostic => diagnostic.Message.StartsWith("Procedure 'public.p()' is written in LANGUAGE " + language, StringComparison.Ordinal)), Dump(model));
    }

    [TestMethod]
    [DataRow("sql", "AS $$ SELECT 1 $$")]
    [DataRow("plpgsql", "AS $$ BEGIN RETURN 1; END $$")]
    [DataRow("internal", "AS 'int4abs'")]
    [DataRow("c", "AS '$libdir/plpgsql', 'plpgsql_call_handler'")]
    public void ARoutineInALanguagePgprojReadsOrThatIsCompiledHasNoWarning(string language, string body)
    {
        var model = Build($"CREATE FUNCTION public.f(a int) RETURNS int LANGUAGE {language} {body};");

        Assert.IsFalse(model.Diagnostics.Any(static diagnostic => diagnostic.Message.Contains("is written in LANGUAGE", StringComparison.Ordinal)), Dump(model));
        Assert.IsFalse(model.Diagnostics.Any(static diagnostic => diagnostic.Severity == SemanticDiagnosticSeverity.Error), Dump(model));
    }

    private static string Dump(DatabaseModel model) => string.Join("; ", model.Diagnostics.Select(static diagnostic => $"{diagnostic.Severity}: {diagnostic.Message}"));

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
