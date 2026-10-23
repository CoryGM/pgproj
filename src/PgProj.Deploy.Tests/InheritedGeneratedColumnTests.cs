using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-299: a child that lists a generated column it inherits, without an expression (<c>pg_dump</c>'s <c>(a int, b int) INHERITS (parent)</c>), has the parent's generation.
/// Writing the expression again is refused by PostgreSQL 14 and 15 (<c>42611</c>).
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class InheritedGeneratedColumnTests
{
    private const string Parent = "CREATE TABLE public.gparent (a int, b int GENERATED ALWAYS AS (a * 2) STORED);\n";

    [TestMethod]
    public void AChildThatListsTheColumnWithoutAnExpressionHasTheParentsGenerationAndDoesNotRepeatIt()
    {
        var model = Build(Parent + "CREATE TABLE public.gchild (a int, b int) INHERITS (public.gparent);");

        var column = model.Tables.Values.Single(static table => table.Name == "gchild").Columns.Single(static column => column.Name == "b");
        Assert.IsTrue(column.IsGenerated);
        Assert.IsTrue(column.GenerationIsInherited);
        Assert.AreEqual("a * 2", column.GeneratedExpression);

        var script = Script(DatabaseModel.Empty, model);
        StringAssert.Contains(script, "CREATE TABLE \"public\".\"gparent\"");
        var child = script[script.IndexOf("CREATE TABLE \"public\".\"gchild\"", StringComparison.Ordinal)..];
        Assert.DoesNotContain("GENERATED", child, "the child's column does not repeat the expression");
        StringAssert.Contains(child, "INHERITS");
    }

    [TestMethod]
    public void AChildThatStatesTheExpressionKeepsItAsWritten()
    {
        var model = Build(Parent + "CREATE TABLE public.gchild (a int, b int GENERATED ALWAYS AS (a * 2) STORED) INHERITS (public.gparent);");

        var column = model.Tables.Values.Single(static table => table.Name == "gchild").Columns.Single(static column => column.Name == "b");
        Assert.IsFalse(column.GenerationIsInherited);
        StringAssert.Contains(Script(DatabaseModel.Empty, model), "\"b\" int GENERATED ALWAYS AS (a * 2) STORED");
    }

    [TestMethod]
    public void BothWaysOfWritingTheChildCompareEqual()
    {
        var plain = Build(Parent + "CREATE TABLE public.gchild (a int, b int) INHERITS (public.gparent);");
        var explicitExpression = Build(Parent + "CREATE TABLE public.gchild (a int, b int GENERATED ALWAYS AS ((a * 2)) STORED) INHERITS (public.gparent);");
        var other = Build("CREATE TABLE public.gparent (a int, b int GENERATED ALWAYS AS (a * 3) STORED);\nCREATE TABLE public.gchild (a int, b int) INHERITS (public.gparent);");

        Assert.AreEqual(0, ChangeCount(new DiffEngine().Diff(plain, plain)));
        Assert.AreEqual(0, ChangeCount(new DiffEngine().Diff(plain, explicitExpression)));
        Assert.IsGreaterThan(0, ChangeCount(new DiffEngine().Diff(plain, other)), "a different generation is a change");
    }

    private static int ChangeCount(ChangeSet diff) => diff.Added.Count + diff.Removed.Count + diff.Modified.Count;

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);

    private static string Script(DatabaseModel live, DatabaseModel project)
    {
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(live, project), new DependencyAnalyzer().Analyze(live, project));
        return string.Join("\n", batch.Operations.Where(static operation => operation is not SchemaCreateOperation).Select(static operation => operation.GenerateSql().Replace("\r\n", "\n", StringComparison.Ordinal))).Trim();
    }
}
