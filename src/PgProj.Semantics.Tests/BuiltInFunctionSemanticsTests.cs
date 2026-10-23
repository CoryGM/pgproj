using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-51: calls to PostgreSQL built-in functions must not be reported as "missing function". Genuinely
/// undeclared functions must still be reported.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class BuiltInFunctionSemanticsTests
{
    [TestMethod]
    public void BuiltInFunctionInColumnDefaultIsNotMissing()
    {
        var model = Build("create table public.t (id uuid default gen_random_uuid(), created timestamptz default now());");

        AssertNoMissingFunction(model);
    }

    [TestMethod]
    public void SchemaQualifiedBuiltInFunctionInColumnDefaultIsNotMissing()
    {
        var model = Build("create table public.t (created timestamptz default pg_catalog.now());");

        AssertNoMissingFunction(model);
    }

    [TestMethod]
    public void BuiltInFunctionInCheckConstraintIsNotMissing()
    {
        var model = Build("create table public.t (c text, constraint c_lower check (lower(c) = c));");

        AssertNoMissingFunction(model);
    }

    [TestMethod]
    public void BuiltInFunctionInIndexExpressionIsNotMissing()
    {
        var model = Build("create table public.t (c text); create index t_lower on public.t (lower(c));");

        AssertNoMissingFunction(model);
    }

    [TestMethod]
    public void BuiltInFunctionInIndexPredicateIsNotMissing()
    {
        var model = Build("create table public.t (c text); create index t_len on public.t (c) where length(c) > 3;");

        AssertNoMissingFunction(model);
    }

    [TestMethod]
    public void BuiltInAggregateInViewIsNotMissing()
    {
        var model = Build(
            "create table public.t (g integer, v integer); "
            + "create view public.totals as select g, sum(v) as total, count(*) as n, coalesce(max(v), 0) as top from public.t group by g;");

        AssertNoMissingFunction(model);
    }

    [TestMethod]
    public void BuiltInFunctionInViewFilterIsNotMissing()
    {
        var model = Build(
            "create table public.t (c text); "
            + "create view public.v as select upper(c) as c from public.t where length(c) > 1;");

        AssertNoMissingFunction(model);
    }

    [TestMethod]
    public void BuiltInFunctionInFunctionBodyIsNotMissing()
    {
        var model = Build(
            "create function public.shout(x text) returns text language sql as $$ select upper(x) || repeat('!', 3) $$;");

        AssertNoMissingFunction(model);
    }

    [TestMethod]
    public void BuiltInFunctionInGeneratedColumnIsNotMissing()
    {
        var model = Build("create table public.t (c text, l integer generated always as (length(c)) stored);");

        AssertNoMissingFunction(model);
    }

    [TestMethod]
    public void UndeclaredFunctionStillProducesMissingFunctionDiagnostic()
    {
        var model = Build("create table public.t (id uuid default my_undeclared_function());");

        Assert.IsTrue(
            model.Diagnostics.Any(diagnostic => diagnostic.Message.Contains("missing function 'my_undeclared_function'", StringComparison.OrdinalIgnoreCase)),
            string.Join(Environment.NewLine, model.Diagnostics.Select(diagnostic => diagnostic.Message)));
    }

    [TestMethod]
    public void UndeclaredFunctionInCheckAndViewStillProducesMissingFunctionDiagnostic()
    {
        var model = Build(
            "create table public.t (c text, constraint c_x check (my_undeclared_check(c))); "
            + "create view public.v as select my_undeclared_view(c) as c from public.t;");

        Assert.IsTrue(model.Diagnostics.Any(diagnostic => diagnostic.Message.Contains("missing function 'my_undeclared_check'", StringComparison.OrdinalIgnoreCase)),
            string.Join(Environment.NewLine, model.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.IsTrue(model.Diagnostics.Any(diagnostic => diagnostic.Message.Contains("missing function 'my_undeclared_view'", StringComparison.OrdinalIgnoreCase)),
            string.Join(Environment.NewLine, model.Diagnostics.Select(diagnostic => diagnostic.Message)));
    }

    [TestMethod]
    public void BuiltInFunctionNameIsRecognizedCaseInsensitivelyAndSchemaQualified()
    {
        Assert.IsTrue(BuiltInFunctions.IsBuiltIn("NOW"));
        Assert.IsTrue(BuiltInFunctions.IsBuiltIn("pg_catalog.lower"));
        Assert.IsTrue(BuiltInFunctions.IsBuiltIn("\"pg_catalog\".\"lower\""));
        Assert.IsFalse(BuiltInFunctions.IsBuiltIn("public.now"));
        Assert.IsFalse(BuiltInFunctions.IsBuiltIn("my_undeclared_function"));
        Assert.IsFalse(BuiltInFunctions.IsBuiltIn(null));
    }

    // G-51 follow-up (user decision, 2026-09-22): a function an extension provides has no per-extension member list, so a
    // call to one is still unresolved; when the project declares any extension, that is downgraded from an error to a
    // warning (`build` succeeds) rather than refusing the whole project over a function pgproj cannot know about. Without
    // any extension declared, it is still an error, so a genuine typo in a project function's own name is still caught.
    [TestMethod]
    public void AnExtensionFunctionInADefaultIsAWarningWhenAnExtensionIsDeclared()
    {
        var model = Build("CREATE EXTENSION \"uuid-ossp\"; CREATE TABLE public.t (id uuid DEFAULT uuid_generate_v4());");

        Assert.IsFalse(model.HasErrors, string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
        Assert.IsTrue(model.Diagnostics.Any(static d => d.Severity == SemanticDiagnosticSeverity.Warning && d.Message.Contains("uuid_generate_v4", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void AnExtensionFunctionInACheckConstraintIsAWarningWhenAnExtensionIsDeclared()
    {
        var model = Build("CREATE EXTENSION pgcrypto; CREATE TABLE public.t (pw text CHECK (pw = crypt('x', 'y')));");

        Assert.IsFalse(model.HasErrors, string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
    }

    [TestMethod]
    public void AnExtensionFunctionInAViewIsAWarningWhenAnExtensionIsDeclared()
    {
        var model = Build("CREATE EXTENSION pgcrypto; CREATE TABLE public.t (pw text); CREATE VIEW public.v AS SELECT crypt(pw, 'y') FROM public.t;");

        Assert.IsFalse(model.HasErrors, string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
    }

    [TestMethod]
    public void AnExtensionFunctionInAnIndexExpressionIsAWarningWhenAnExtensionIsDeclared()
    {
        var model = Build("CREATE EXTENSION pgcrypto; CREATE TABLE public.t (pw text); CREATE INDEX i ON public.t (crypt(pw, 'y'));");

        Assert.IsFalse(model.HasErrors, string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
    }

    [TestMethod]
    public void AnUndeclaredFunctionWithNoExtensionDeclaredIsStillAnError()
    {
        // schemas is populated so G-83's own (independent) leniency doesn't mask this test's actual subject: G-51's
        // no-extension-declared severity, which must stay Error regardless.
        var model = Build("CREATE TABLE public.t (id integer DEFAULT my_undeclared_function());", schemas: ["public"]);

        Assert.IsTrue(model.HasErrors);
        Assert.IsTrue(model.Diagnostics.Any(static d => d.Severity == SemanticDiagnosticSeverity.Error && d.Message.Contains("my_undeclared_function", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ATypoInAProjectFunctionsOwnNameIsOnlyAWarningWhenAnExtensionIsDeclared()
    {
        // The accepted tradeoff of the user's decision: once any extension is declared, pgproj cannot tell a real
        // extension function from a misspelled project function, so both are warnings, not errors.
        var model = Build("CREATE EXTENSION pgcrypto; CREATE TABLE public.t (id integer DEFAULT my_project_function_with_a_typo());");

        Assert.IsFalse(model.HasErrors, string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
    }

    private static void AssertNoMissingFunction(DatabaseModel model)
        => Assert.IsFalse(
            model.Diagnostics.Any(diagnostic => diagnostic.Message.Contains("missing function", StringComparison.OrdinalIgnoreCase)),
            string.Join(Environment.NewLine, model.Diagnostics.Select(diagnostic => diagnostic.Message)));

    private static DatabaseModel Build(string sql, IReadOnlyList<string>? schemas = null)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)], schemas);
}
