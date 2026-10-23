using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-241: a base type (<c>CREATE TYPE name (INPUT = ..., OUTPUT = ...)</c>) and a shell type are not modeled, by the user's decision of 2026-10-13 (modeling them is G-267).
/// A project written as <c>pg_dump</c> writes one (a shell, the functions, the full definition) used to fail <c>build</c> with <c>Duplicate Type definition</c>. Both statements
/// are now reported and ignored, and the functions the type names are left out with it, with their owners, comments, and grants, so the project builds and publishes what uses
/// the type, and converges with a database whose live read leaves the same functions out.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class BaseTypeTests
{
    private const string PgDumpForm = """
        CREATE TYPE public.xint4;
        CREATE FUNCTION public.xint4in(cstring) RETURNS public.xint4 LANGUAGE internal IMMUTABLE STRICT AS $$int4in$$;
        CREATE FUNCTION public.xint4out(public.xint4) RETURNS cstring LANGUAGE internal IMMUTABLE STRICT AS $$int4out$$;
        CREATE TYPE public.xint4 (INTERNALLENGTH = 4, INPUT = public.xint4in, OUTPUT = public.xint4out, ALIGNMENT = int4, PASSEDBYVALUE);
        """;

    [TestMethod]
    public void APgDumpBaseTypeBuildsWithoutADuplicateAndItsFunctionsAreLeftOut()
    {
        var model = Build(PgDumpForm + "CREATE CAST (integer AS public.xint4) WITHOUT FUNCTION; CREATE FUNCTION public.plain() RETURNS integer LANGUAGE sql AS $$ SELECT 1 $$;");

        Assert.IsFalse(model.Diagnostics.Any(static diagnostic => diagnostic.Severity == SemanticDiagnosticSeverity.Error), Messages(model));
        Assert.IsFalse(model.Types.Any(static type => type.Value.Name == "xint4"), "A base type is not a type of the model.");
        CollectionAssert.AreEquivalent(new[] { "plain" }, model.Functions.Values.Select(static function => function.Name).ToArray());
        Assert.HasCount(1, model.Casts);

        var script = Script(model);
        Assert.IsFalse(script.Contains("xint4in", StringComparison.Ordinal), script);
        Assert.IsFalse(script.Contains("CREATE TYPE", StringComparison.Ordinal), script);
        StringAssert.Contains(script, "CREATE CAST (integer AS public.xint4)\n    WITHOUT FUNCTION;");
    }

    [TestMethod]
    public void TheWarningNamesTheFunctionsLeftOut()
    {
        var trees = BaseTypeApplier.Apply([new PgQueryParser().Parse(PgDumpForm)]);

        var warning = trees.Single().Diagnostics.Single(static diagnostic => diagnostic.Source == BaseTypeApplier.DiagnosticSource);
        Assert.AreEqual(SqlDiagnosticSeverity.Warning, warning.Severity);
        Assert.AreEqual("Base type public.xint4 is not modeled, so the functions it names are left out with it: public.xint4in, public.xint4out.", warning.Message);
        Assert.IsFalse(trees.Single().Nodes.Any(static node => node is BaseTypeNode or FunctionNode));
    }

    [TestMethod]
    public void TheFunctionsAreLeftOutWhateverFileTheyAreInWithTheirOwnersCommentsAndGrants()
    {
        var types = new PgQueryParser().Parse("CREATE TYPE app.xint4 (INPUT = xint4in, OUTPUT = xint4out); ALTER TYPE app.xint4 OWNER TO postgres; COMMENT ON TYPE app.xint4 IS 'x';");
        var functions = new PgQueryParser().Parse("""
            CREATE FUNCTION app.xint4in(cstring) RETURNS app.xint4 LANGUAGE internal AS $$int4in$$;
            CREATE FUNCTION app.xint4out(app.xint4) RETURNS cstring LANGUAGE internal AS $$int4out$$;
            CREATE FUNCTION app.keep() RETURNS integer LANGUAGE sql AS $$ SELECT 1 $$;
            ALTER FUNCTION app.xint4in(cstring) OWNER TO postgres;
            COMMENT ON FUNCTION app.xint4out(app.xint4) IS 'x';
            GRANT EXECUTE ON FUNCTION app.xint4in(cstring), app.keep() TO PUBLIC;
            """);

        var nodes = BaseTypeApplier.Apply([types, functions]).SelectMany(static tree => tree.Nodes).ToArray();

        CollectionAssert.AreEqual(new[] { "keep" }, nodes.OfType<FunctionNode>().Select(static function => function.Name).ToArray(), "An unqualified support function is the one in the type's schema.");
        Assert.IsEmpty(nodes.OfType<OwnerNode>());
        Assert.IsEmpty(nodes.OfType<ObjectCommentNode>());
        CollectionAssert.AreEqual(new[] { "keep" }, nodes.OfType<GrantNode>().Single().Targets.Select(static target => target.Name).ToArray());
    }

    [TestMethod]
    public void AFunctionOfTheSameNameInAnotherSchemaIsKept()
    {
        var model = Build("""
            CREATE TYPE public.xint4 (INPUT = public.xint4in, OUTPUT = public.xint4out);
            CREATE FUNCTION public.xint4in(cstring) RETURNS public.xint4 LANGUAGE internal AS $$int4in$$;
            CREATE FUNCTION other.xint4in(text) RETURNS integer LANGUAGE sql AS $$ SELECT 1 $$;
            """);

        CollectionAssert.AreEqual(new[] { "other" }, model.Functions.Values.Select(static function => function.SchemaName).ToArray());
    }

    [TestMethod]
    public void AShellTypeAloneIsReportedAndLeavesNothing()
    {
        var model = Build("CREATE TYPE public.sh;");

        Assert.IsEmpty(model.Types);
        Assert.IsFalse(model.Diagnostics.Any(static diagnostic => diagnostic.Severity == SemanticDiagnosticSeverity.Error), Messages(model));
    }

    [TestMethod]
    public void AProjectWithABaseTypeDiffsEmptyAgainstItself()
    {
        var model = Build(PgDumpForm + "CREATE CAST (integer AS public.xint4) WITHOUT FUNCTION;");

        var diff = new DiffEngine().Diff(model, Build(PgDumpForm + "CREATE CAST (integer AS public.xint4) WITHOUT FUNCTION;"));

        Assert.IsEmpty(diff.Added);
        Assert.IsEmpty(diff.Removed);
        Assert.IsEmpty(diff.Modified);
    }

    private static string Messages(DatabaseModel model) => string.Join(" | ", model.Diagnostics.Select(static diagnostic => diagnostic.Message));

    private static string Script(DatabaseModel newModel)
    {
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(DatabaseModel.Empty, newModel), new DependencyAnalyzer().Analyze(DatabaseModel.Empty, newModel));
        return string.Join("\n", batch.Operations.Where(static operation => operation is not SchemaCreateOperation).Select(static operation => operation.GenerateSql().Replace("\r\n", "\n", StringComparison.Ordinal))).Trim();
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
