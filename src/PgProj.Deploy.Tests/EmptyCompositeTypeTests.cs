using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-268: a composite type with no fields, <c>CREATE TYPE t AS ()</c>, is deployed as written. It was emitted as a TODO placeholder that <c>publish --execute</c> refuses, as an
/// empty enum was before G-249. An empty composite and an empty enum are told apart.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class EmptyCompositeTypeTests
{
    [TestMethod]
    public void ACompositeWithNoFieldsIsDeployed()
    {
        var script = Script(DatabaseModel.Empty, Build("CREATE TYPE public.empty AS ();"));

        StringAssert.Contains(script, "CREATE TYPE \"public\".\"empty\" AS (\n\n);");
        Assert.IsFalse(script.Contains("TODO", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void FieldsAddedToAnEmptyCompositeAreAddedInPlace()
    {
        var script = Script(Build("CREATE TYPE public.c AS ();"), Build("CREATE TYPE public.c AS (a integer, b text);"));

        StringAssert.Contains(script, "ADD ATTRIBUTE");
        Assert.IsFalse(script.Contains("DROP TYPE", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void TheSameEmptyCompositeOnBothSidesIsNoChange()
    {
        var model = Build("CREATE TYPE public.c AS ();");

        Assert.AreEqual(string.Empty, Script(model, Build("CREATE TYPE public.c AS ( );")));
        Assert.AreEqual(0, Count(new DiffEngine().Diff(model, model)));
    }

    [TestMethod]
    public void AnEmptyEnumAndAnEmptyCompositeAreDifferent()
    {
        var enumModel = Build("CREATE TYPE public.t AS ENUM ();");
        var compositeModel = Build("CREATE TYPE public.t AS ();");

        var diff = new DiffEngine().Diff(enumModel, compositeModel);
        Assert.AreEqual(1, diff.Modified.Count, "The form is the only difference, and it is one.");

        var script = Script(enumModel, compositeModel);
        StringAssert.Contains(script, "DROP TYPE");
        StringAssert.Contains(script, "CREATE TYPE \"public\".\"t\" AS (\n\n);");
        Assert.IsFalse(script.Contains("ADD VALUE", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void AnEmptyCompositeIsNotATypeOfAnotherForm()
    {
        var type = Build("CREATE TYPE public.c AS ();").Types["public.c"];

        Assert.IsTrue(type.IsComposite);
        Assert.IsFalse(type.IsEnum);
        Assert.IsFalse(Build("CREATE TYPE public.e AS ENUM ();").Types["public.e"].IsComposite);
    }

    private static int Count(ChangeSet diff) => diff.Added.Count + diff.Removed.Count + diff.Modified.Count;

    private static string Script(DatabaseModel oldModel, DatabaseModel newModel)
    {
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));
        Assert.IsEmpty(batch.GetUnsupportedOperations());
        return string.Join("\n", batch.Operations.Where(static operation => operation is not SchemaCreateOperation).Select(static operation => operation.GenerateSql().Replace("\r\n", "\n", StringComparison.Ordinal))).Trim();
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
