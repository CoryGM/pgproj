using PgProj.Core;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core.Tests;

/// <summary>
/// G-267: the statement the live loader writes for a base type (every option explicit, as PostgreSQL stores it, functions with their schema) reads back as the same type a
/// project writes in its own way, and <c>init</c> writes the type's shell, its support functions, and its definition in the type's file, in that order, the functions in no
/// file of their own. (The catalog query is checked against a real server by the integration tests.)
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class LiveBaseTypeSnapshotTests
{
    private const string LiveStatement = "CREATE TYPE public.xint4 (INPUT = public.xint4in, OUTPUT = public.xint4out, INTERNALLENGTH = 4, PASSEDBYVALUE, ALIGNMENT = int4, STORAGE = plain, CATEGORY = 'U', PREFERRED = false, DELIMITER = ',', COLLATABLE = false)";

    private static readonly LiveFunctionSnapshot Input = new("public", "xint4in", "CREATE OR REPLACE FUNCTION public.xint4in(cstring)\n RETURNS xint4\n LANGUAGE internal\n IMMUTABLE STRICT\nAS $function$int4in$function$", IdentityArguments: "cstring");
    private static readonly LiveFunctionSnapshot Output = new("public", "xint4out", "CREATE OR REPLACE FUNCTION public.xint4out(xint4)\n RETURNS cstring\n LANGUAGE internal\n IMMUTABLE STRICT\nAS $function$int4out$function$", IdentityArguments: "xint4");
    private static readonly LiveFunctionSnapshot Plain = new("public", "plain", "CREATE OR REPLACE FUNCTION public.plain()\n RETURNS integer\n LANGUAGE sql\nAS $function$ SELECT 1 $function$", IdentityArguments: string.Empty);

    private static readonly LiveTypeSnapshot Type = new("public", "xint4", "base", LiveStatement) { SupportFunctions = ["public.xint4in(cstring)", "public.xint4out(xint4)"] };

    [TestMethod]
    public void TheRenderingReadsBackAsTheTypeAProjectWritesItsOwnWay()
    {
        var loader = new LiveDatabaseModelLoader(new PgQueryParser(), new SemanticBuilder());
        var live = loader.BuildModelFromSnapshotSql(LiveDatabaseModelLoader.GenerateSnapshotSql([new LiveSchemaSnapshot("public")], [], [], [], functions: [Input, Output], types: [Type]));
        var project = new SemanticPipeline().Execute([new PgQueryParser().Parse("""
            CREATE TYPE public.xint4;
            CREATE FUNCTION public.xint4in(cstring) RETURNS public.xint4 LANGUAGE internal IMMUTABLE STRICT AS $$int4in$$;
            CREATE FUNCTION public.xint4out(public.xint4) RETURNS cstring LANGUAGE internal IMMUTABLE STRICT AS $$int4out$$;
            CREATE TYPE public.xint4 (INTERNALLENGTH = 4, INPUT = xint4in, OUTPUT = xint4out, ALIGNMENT = int4, PASSEDBYVALUE);
            """)]);

        var liveType = live.Types.Values.Single();
        Assert.IsTrue(liveType.IsBaseType);
        Assert.IsEmpty(liveType.BaseTypeOptions!.DifferencesFrom(project.Types.Values.Single().BaseTypeOptions!));
        CollectionAssert.AreEquivalent(new[] { "xint4in", "xint4out" }, live.Functions.Values.Select(static function => function.Name).ToArray(), "Its functions are read, as the project's are.");
    }

    [TestMethod]
    public void InitWritesTheShellTheFunctionsAndTheDefinitionInTheTypesFile()
    {
        var files = LiveDatabaseModelLoader.BuildScaffoldFiles([new LiveSchemaSnapshot("public")], [], [], [], [], [Input, Output, Plain], [Type], [], [], [], []);

        var typeFile = files.Single(static file => file.RelativePath == "Schema/public/Type/xint4.sql").Content.Replace("\r\n", "\n", StringComparison.Ordinal);
        var shell = typeFile.IndexOf("CREATE TYPE public.xint4;", StringComparison.Ordinal);
        var input = typeFile.IndexOf("FUNCTION public.xint4in(cstring)", StringComparison.Ordinal);
        var output = typeFile.IndexOf("FUNCTION public.xint4out(xint4)", StringComparison.Ordinal);
        var definition = typeFile.IndexOf("CREATE TYPE public.xint4 (", StringComparison.Ordinal);
        Assert.IsTrue(shell >= 0 && shell < input && shell < output && input < definition && output < definition, typeFile);

        CollectionAssert.DoesNotContain(files.Select(static file => file.RelativePath).ToArray(), "Schema/public/Function/xint4in.sql");
        CollectionAssert.Contains(files.Select(static file => file.RelativePath).ToArray(), "Schema/public/Function/plain.sql");
    }

    [TestMethod]
    public void AnotherKindOfTypeHasNoShell()
    {
        var files = LiveDatabaseModelLoader.BuildScaffoldFiles([new LiveSchemaSnapshot("public")], [], [], [], [], [], [new LiveTypeSnapshot("public", "mood", "user-defined", "CREATE TYPE public.mood AS ENUM ('a')")], [], [], [], []);

        Assert.IsFalse(files.Single(static file => file.RelativePath == "Schema/public/Type/mood.sql").Content.Contains("CREATE TYPE public.mood;", StringComparison.Ordinal));
    }
}
