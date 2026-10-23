using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-136: a typed table has the columns of its composite type, in the type's order, with the defaults and NOT NULLs it gives some of them; the type may be in another file; the table
/// depends on the type; the type knows it has typed tables; and a typed table of a type the project does not declare is an error.
/// </summary>
[TestClass]
public sealed class TypedTableSemanticsTests
{
    private const string Type = "CREATE TYPE public.r AS (k integer, v varchar(10), w numeric(8,2)[]); ";

    [TestMethod]
    public void ATypedTableHasTheColumnsOfItsTypeInItsOrderWithTheirTypes()
    {
        var model = Build(Type + "CREATE TABLE public.t OF public.r;");

        var table = model.Tables["public.t"];
        CollectionAssert.AreEqual(new[] { "k", "v", "w" }, table.Columns.Select(static column => column.Name).ToArray());
        StringAssert.EndsWith(table.Columns[1].DataType, "varchar");
        Assert.AreEqual(1, table.Columns[2].ArrayDimensions);
        Assert.AreEqual("r", table.OfTypeName);
        Assert.AreEqual("public.r", table.OfTypeKey);
        Assert.AreEqual(0, model.Diagnostics.Count, string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
    }

    [TestMethod]
    public void WithOptionsGivesAColumnItsDefaultAndNotNull()
    {
        var model = Build(Type + "CREATE TABLE public.t OF public.r (k WITH OPTIONS PRIMARY KEY, v WITH OPTIONS DEFAULT 'x' NOT NULL);");

        var table = model.Tables["public.t"];
        Assert.AreEqual("'x'", table.Columns.Single(static column => column.Name == "v").RawDefaultExpression);
        Assert.AreEqual(false, table.Columns.Single(static column => column.Name == "v").IsNullable);
        Assert.AreEqual(false, table.Columns.Single(static column => column.Name == "k").IsNullable, "A primary key column is NOT NULL.");
        Assert.IsNull(table.Columns.Single(static column => column.Name == "w").RawDefaultExpression);
        Assert.AreEqual(1, model.Constraints.Count);
    }

    [TestMethod]
    public void TheTypeMayBeInAnotherFileAndAfterTheTable()
    {
        var tables = new PgQueryParser().Parse("CREATE TABLE public.t OF public.r (v WITH OPTIONS DEFAULT 'x');");
        var types = new PgQueryParser().Parse(Type);

        var model = new SemanticPipeline().Execute([tables, types]);

        CollectionAssert.AreEqual(new[] { "k", "v", "w" }, model.Tables["public.t"].Columns.Select(static column => column.Name).ToArray());
        Assert.AreEqual(0, model.Diagnostics.Count, string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
    }

    [TestMethod]
    public void TheTableDependsOnItsTypeAndTheTypeKnowsItHasTypedTables()
    {
        var model = Build(Type + "CREATE TABLE public.t OF public.r; CREATE TYPE public.other AS (a int);");

        CollectionAssert.Contains(model.Tables["public.t"].ReferencedTypes.ToArray(), "public.r");
        Assert.IsTrue(model.Types["public.r"].HasTypedTables);
        Assert.IsFalse(model.Types["public.other"].HasTypedTables);
    }

    [TestMethod]
    public void ATypedTableOfATypeThatIsNotDeclaredIsAnError()
    {
        var model = Build("CREATE TABLE public.t OF public.nowhere;");

        Assert.IsTrue(model.Diagnostics.Any(static d => d.Severity == SemanticDiagnosticSeverity.Error && d.Message.Contains("not a composite type this project declares", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void AWithOptionsColumnTheTypeHasNoFieldForIsAnError()
    {
        var model = Build(Type + "CREATE TABLE public.t OF public.r (nope WITH OPTIONS NOT NULL);");

        Assert.IsTrue(model.Diagnostics.Any(static d => d.Message.Contains("'nope'", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void AnOrdinaryTableIsNotTypedAndItsTypeHasNoTypedTables()
    {
        var model = Build(Type + "CREATE TABLE public.t (a public.r);");

        Assert.IsFalse(model.Tables["public.t"].IsTypedTable);
        Assert.IsFalse(model.Types["public.r"].HasTypedTables);
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
