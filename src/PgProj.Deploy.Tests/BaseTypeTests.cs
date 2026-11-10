using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-267: base types are modeled (the user's design of 2026-11-09), replacing G-241's tests, which asserted that a base type and the functions it names were left out of
/// the project. A shell plus its definition is one type; <c>publish</c> writes the shell, the support functions, the type as written, then what uses it; the options
/// <c>ALTER TYPE ... SET</c> covers change in place; any other change drops the type with <c>CASCADE</c> (behind a check that only its functions depend on it) and creates it
/// and its functions again, or is refused when something uses it; a removed type is dropped the same way, its functions with it.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class BaseTypeTests
{
    private const string Functions = """
        CREATE FUNCTION public.xint4in(cstring) RETURNS public.xint4 LANGUAGE internal IMMUTABLE STRICT AS $$int4in$$;
        CREATE FUNCTION public.xint4out(public.xint4) RETURNS cstring LANGUAGE internal IMMUTABLE STRICT AS $$int4out$$;
        CREATE FUNCTION public.xint4recv(internal) RETURNS public.xint4 LANGUAGE internal IMMUTABLE STRICT AS $$int4recv$$;

        """;

    private const string Type = "CREATE TYPE public.xint4 (INTERNALLENGTH = 4, INPUT = public.xint4in, OUTPUT = public.xint4out, ALIGNMENT = int4, PASSEDBYVALUE);\n";

    private const string PgDumpForm = "CREATE TYPE public.xint4;\n" + Functions + Type;

    [TestMethod]
    public void APgDumpBaseTypeIsOneTypeWithItsFunctionsAndNoWarning()
    {
        var model = Build(PgDumpForm + "CREATE CAST (integer AS public.xint4) WITHOUT FUNCTION;");

        Assert.IsFalse(model.Diagnostics.Any(static diagnostic => diagnostic.Severity is SemanticDiagnosticSeverity.Error or SemanticDiagnosticSeverity.Warning && !diagnostic.Message.Contains("body", StringComparison.Ordinal)), Messages(model));
        var type = model.Types.Values.Single();
        Assert.IsTrue(type.IsBaseType);
        Assert.AreEqual("public.xint4in", type.BaseTypeOptions!.Functions["INPUT"]);
        Assert.AreEqual(4, type.BaseTypeOptions.InternalLength);
        Assert.IsTrue(type.BaseTypeOptions.PassedByValue);
        CollectionAssert.AreEquivalent(new[] { "xint4in", "xint4out", "xint4recv" }, model.Functions.Values.Select(static function => function.Name).ToArray());
    }

    [TestMethod]
    public void ANewBaseTypeIsWrittenAsShellFunctionsTypeThenWhatUsesIt()
    {
        var statements = Statements(DatabaseModel.Empty, Build(PgDumpForm + "CREATE TABLE public.t (v public.xint4);"), out var batch);

        var shell = statements.IndexOf("CREATE TYPE \"public\".\"xint4\";");
        var input = statements.FindIndex(static statement => statement.StartsWith("CREATE OR REPLACE FUNCTION \"public\".\"xint4in\"", StringComparison.Ordinal));
        var output = statements.FindIndex(static statement => statement.StartsWith("CREATE OR REPLACE FUNCTION \"public\".\"xint4out\"", StringComparison.Ordinal));
        var type = statements.IndexOf("CREATE TYPE public.xint4 (INTERNALLENGTH = 4, INPUT = public.xint4in, OUTPUT = public.xint4out, ALIGNMENT = int4, PASSEDBYVALUE);");
        var table = statements.FindIndex(static statement => statement.StartsWith("CREATE TABLE", StringComparison.Ordinal));
        Assert.IsTrue(shell >= 0 && shell < input && shell < output && input < type && output < type && type < table, string.Join("\n", statements));
        Assert.AreEqual(1, statements.Count(static statement => statement.StartsWith("CREATE TYPE \"public\".\"xint4\";", StringComparison.Ordinal)), "One shell, before the first function.");
        Assert.IsEmpty(batch.GetDestructiveOperations());
    }

    [TestMethod]
    public void TheSameTypeWrittenDifferentlyIsNoChange()
    {
        // Unqualified functions in the type's schema, VARIABLE and the defaults stated or not, LIKE against the same LIKE, a type alias: one type each time.
        string[][] pairs =
        [
            ["CREATE TYPE public.xint4 (INPUT = xint4in, OUTPUT = xint4out, INTERNALLENGTH = 4, PASSEDBYVALUE = true, ALIGNMENT = int4);", Type],
            ["CREATE TYPE public.xint4 (INPUT = xint4in, OUTPUT = xint4out);", "CREATE TYPE public.xint4 (INPUT = xint4in, OUTPUT = xint4out, INTERNALLENGTH = VARIABLE, ALIGNMENT = int4, STORAGE = plain, CATEGORY = 'U', PREFERRED = false, DELIMITER = ',', COLLATABLE = false);"],
            ["CREATE TYPE public.xint4 (INPUT = xint4in, OUTPUT = xint4out, LIKE = int4);", "CREATE TYPE public.xint4 (INPUT = xint4in, OUTPUT = xint4out, LIKE = integer);"],
            ["CREATE TYPE public.xint4 (INPUT = xint4in, OUTPUT = xint4out, ELEMENT = int4);", "CREATE TYPE public.xint4 (INPUT = xint4in, OUTPUT = xint4out, ELEMENT = integer, SUBSCRIPT = raw_array_subscript_handler);"],
        ];

        foreach (var pair in pairs)
        {
            var changes = new DiffEngine().Diff(Build(Functions + pair[0]), Build(Functions + pair[1]));
            Assert.IsEmpty(changes.Modified, pair[0] + "\n" + string.Join("\n", changes.Modified.SelectMany(static change => change.Details).Select(static detail => $"{detail.PropertyName}: {detail.OldValue} -> {detail.NewValue}")));
        }

        Assert.IsEmpty(new DiffEngine().Diff(Build(PgDumpForm), Build(PgDumpForm)).Modified);
    }

    [TestMethod]
    [DataRow("INTERNALLENGTH = 8", "INTERNALLENGTH")]
    [DataRow("CATEGORY = 'N'", "CATEGORY")]
    [DataRow("ELEMENT = int4", "ELEMENT")]
    [DataRow("RECEIVE = xint4recv", "RECEIVE")]
    [DataRow("STORAGE = main", "STORAGE")]
    [DataRow("LIKE = int8", "LIKE")]
    public void AChangedOptionIsAChange(string option, string name)
    {
        var changes = new DiffEngine().Diff(Build(Functions + Type), Build(Functions + Type.Replace("PASSEDBYVALUE)", "PASSEDBYVALUE, " + option + ")", StringComparison.Ordinal)));

        var detail = changes.Modified.Single().Details.Single(static detail => detail.PropertyName == nameof(TypeDefinition.BaseTypeOptions));
        StringAssert.Contains(detail.NewValue, name);
    }

    [TestMethod]
    public void AChangeToAnOptionSetChangesIsAnAlterThatNeedsASuperuser()
    {
        var statements = Statements(Build(Functions + Type), Build(Functions + Type.Replace("PASSEDBYVALUE)", "PASSEDBYVALUE, RECEIVE = xint4recv, STORAGE = plain)", StringComparison.Ordinal)), out var batch);

        CollectionAssert.AreEqual(new[] { "ALTER TYPE \"public\".\"xint4\" SET (RECEIVE = xint4recv);" }, statements.ToArray(), "STORAGE = plain is the default: no change.");
        Assert.IsEmpty(batch.GetDestructiveOperations());

        var removed = Statements(Build(Functions + Type.Replace("PASSEDBYVALUE)", "PASSEDBYVALUE, RECEIVE = xint4recv)", StringComparison.Ordinal)), Build(Functions + Type), out _);
        CollectionAssert.AreEqual(new[] { "ALTER TYPE \"public\".\"xint4\" SET (RECEIVE = NONE);" }, removed.ToArray());
    }

    [TestMethod]
    public void AnyOtherChangeWithNothingUsingTheTypeDropsAndCreatesItAndItsFunctions()
    {
        // Without xint4recv, which returns the type but is not one it names, so it uses the type, and the change would be refused (as below).
        var ownFunctionsOnly = string.Join("\n", PgDumpForm.Split('\n').Where(static line => !line.Contains("xint4recv", StringComparison.Ordinal)));
        var statements = Statements(Build(ownFunctionsOnly), Build(ownFunctionsOnly.Replace("INTERNALLENGTH = 4", "INTERNALLENGTH = 4, CATEGORY = 'N'", StringComparison.Ordinal)), out var batch);

        var drop = statements.FindIndex(static statement => statement.Contains("DROP TYPE \"public\".\"xint4\" CASCADE;", StringComparison.Ordinal));
        Assert.IsGreaterThanOrEqualTo(0, drop, string.Join("\n", statements));
        StringAssert.Contains(statements[drop], "cannot be dropped: DROP TYPE ... CASCADE would also drop %", "The check before the CASCADE.");
        var shell = statements.IndexOf("CREATE TYPE \"public\".\"xint4\";");
        var input = statements.FindIndex(static statement => statement.StartsWith("CREATE OR REPLACE FUNCTION \"public\".\"xint4in\"", StringComparison.Ordinal));
        var type = statements.FindIndex(static statement => statement.Contains("CATEGORY = 'N'", StringComparison.Ordinal));
        Assert.IsTrue(drop >= 0 && drop < shell && shell < input && input < type, string.Join("\n", statements));
        Assert.IsFalse(statements.Any(static statement => statement.StartsWith("DROP FUNCTION", StringComparison.Ordinal)), "CASCADE drops the functions.\n" + string.Join("\n", statements));
        Assert.IsEmpty(batch.GetDestructiveOperations(), "Nothing uses it: nothing is lost.");
    }

    [TestMethod]
    public void AnyOtherChangeWhileSomethingUsesTheTypeIsRefusedNamingIt()
    {
        const string table = "CREATE TABLE public.t (v public.xint4);";
        var batch = new ScriptGenerator().Generate(
            new DiffEngine().Diff(Build(PgDumpForm + table), Build(PgDumpForm.Replace("INTERNALLENGTH = 4", "INTERNALLENGTH = 4, CATEGORY = 'N'", StringComparison.Ordinal) + table)),
            new DependencyAnalyzer().Analyze(Build(PgDumpForm + table), Build(PgDumpForm + table)));

        var blocked = batch.Operations.OfType<BlockedOperation>().Single();
        StringAssert.Contains(blocked.Message, "The base type public.xint4 changes an option that PostgreSQL cannot change in place");
        StringAssert.Contains(blocked.Message, "public.t");
        Assert.IsFalse(batch.Operations.Any(static operation => operation.GenerateSql().Contains("DROP TYPE", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ARemovedBaseTypeIsDroppedWithCascadeAndItsFunctionsWithIt()
    {
        var statements = Statements(Build(PgDumpForm + "CREATE FUNCTION public.keep() RETURNS integer LANGUAGE sql AS $$ SELECT 1 $$;"), Build("CREATE FUNCTION public.keep() RETURNS integer LANGUAGE sql AS $$ SELECT 1 $$;"), out var batch);

        // xint4recv is not one the type names: it is dropped on its own, before the type it returns.
        CollectionAssert.AreEqual(new[] { "DROP FUNCTION \"public\".\"xint4recv\"(internal);" }, statements.Where(static statement => statement.StartsWith("DROP FUNCTION", StringComparison.Ordinal)).ToArray(), string.Join("\n", statements));
        StringAssert.EndsWith(statements.Last(), "DROP TYPE \"public\".\"xint4\" CASCADE;");
        Assert.IsEmpty(batch.GetDestructiveOperations(), "Removing a base type is not destructive in itself; a column of it would be.");
    }

    [TestMethod]
    public void AShellTypeAloneIsAWarningAndLeavesNothing()
    {
        var model = Build("CREATE TYPE public.sh;");

        Assert.IsEmpty(model.Types);
        StringAssert.Contains(Messages(model), "CREATE TYPE (shell type) public.sh is not modeled and is ignored: the project has no definition for it");
    }

    [TestMethod]
    public void TheShellAndTheDefinitionInDifferentFilesAreOneType()
    {
        var model = new SemanticPipeline().Execute([new PgQueryParser().Parse("CREATE TYPE public.xint4;"), new PgQueryParser().Parse(Functions), new PgQueryParser().Parse(Type)]);

        Assert.IsTrue(model.Types.Values.Single().IsBaseType);
        Assert.IsFalse(Messages(model).Contains("shell", StringComparison.Ordinal), Messages(model));
    }

    private static string Messages(DatabaseModel model) => string.Join(" | ", model.Diagnostics.Select(static diagnostic => diagnostic.Message));

    private static List<string> Statements(DatabaseModel oldModel, DatabaseModel newModel, out OperationBatch batch)
    {
        batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));
        return batch.Operations
            .Where(static operation => operation is not SchemaCreateOperation)
            .Select(static operation => operation.GenerateSql().Replace("\r\n", "\n", StringComparison.Ordinal).Trim())
            .ToList();
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
