using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-221: what a table's <c>LIKE</c> copies is in the model, marked with its source; the table references its source (a table, or a composite type), so a
/// source the project does not have is a missing table like any other: an error in one of the project's schemas, a warning outside them. An <c>ALTER TABLE</c>
/// on the table finds a copied column, and the source's own unnamed constraint keeps its name.
/// </summary>
[TestClass]
public sealed class TableLikeSemanticsTests
{
    [TestMethod]
    public void TheCopiesAreInTheModelMarkedWithTheirSource()
    {
        var model = Build("CREATE TABLE a (id int PRIMARY KEY, n text CHECK (n <> '')); CREATE INDEX a_n ON a (n); CREATE TABLE b (LIKE a INCLUDING ALL, extra int);");

        var table = model.Tables["public.b"];
        CollectionAssert.AreEqual(new string?[] { "public.a", "public.a", null }, table.Columns.Select(static column => column.CopiedByLike).ToArray());
        CollectionAssert.Contains(table.ReferencedTables.ToArray(), "public.a");
        Assert.AreEqual("public.a", model.Constraints["public.b.b_pkey"].CopiedByLike);
        Assert.AreEqual("public.a", model.Constraints["public.b.a_n_check"].CopiedByLike);
        Assert.AreEqual("public.a", model.Indexes["public.b_n_idx"].CopiedByLike);
        Assert.IsNull(model.Constraints["public.a.a_n_check"].CopiedByLike);
        Assert.AreEqual("LIKE a INCLUDING ALL", table.LikeClauses.Single().RawText);
        Assert.IsFalse(model.HasErrors, string.Join("\n", model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    [TestMethod]
    public void TheSourcesUnnamedCheckKeepsItsName()
    {
        var model = Build("CREATE TABLE a (id int CHECK (id > 0)); CREATE TABLE b (LIKE a INCLUDING CONSTRAINTS);");

        Assert.IsTrue(model.Constraints.ContainsKey("public.a.a_id_check"));
        Assert.IsTrue(model.Constraints.ContainsKey("public.b.a_id_check"));
        Assert.IsFalse(model.Diagnostics.Any(static diagnostic => diagnostic.Message.Contains("a_id_check1", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void AnAlterTableFindsACopiedColumnAndAddsAfterTheCopies()
    {
        var model = Build("CREATE TABLE a (id int); CREATE TABLE b (LIKE a, own int); ALTER TABLE b ADD COLUMN later int; ALTER TABLE b ADD COLUMN id int;");

        CollectionAssert.AreEqual(new[] { "id", "own", "later" }, model.Tables["public.b"].Columns.Select(static column => column.Name).ToArray());
        Assert.IsTrue(model.Diagnostics.Any(static diagnostic => diagnostic.Message.Contains("column 'id' already exists", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ACompositeTypeSourceIsAReferencedType()
    {
        var table = Build("CREATE TYPE ct AS (p int, q text); CREATE TABLE e (LIKE ct);").Tables["public.e"];

        CollectionAssert.Contains(table.ReferencedTypes.ToArray(), "public.ct");
        CollectionAssert.DoesNotContain(table.ReferencedTables.ToArray(), "public.ct");
        CollectionAssert.AreEqual(new[] { "p", "q" }, table.Columns.Select(static column => column.Name).ToArray());
    }

    [TestMethod]
    public void AMissingSourceInAProjectSchemaIsAnError()
    {
        var model = new SemanticPipeline().Execute([new PgQueryParser().Parse("CREATE SCHEMA app; CREATE TABLE app.b (LIKE app.nope);")], schemas: ["app"]);

        Assert.IsTrue(model.Diagnostics.Any(static diagnostic => diagnostic.Severity == SemanticDiagnosticSeverity.Error && diagnostic.Message.Contains("missing table 'app.nope'", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ASourceOutsideTheProjectsSchemasIsAWarningAndListed()
    {
        var model = new SemanticPipeline().Execute([new PgQueryParser().Parse("CREATE SCHEMA app; CREATE TABLE app.b (LIKE ext.src, x int);")], schemas: ["app"]);

        Assert.IsFalse(model.HasErrors);
        Assert.IsTrue(model.Diagnostics.Any(static diagnostic => diagnostic.Severity == SemanticDiagnosticSeverity.Warning && diagnostic.Message.Contains("missing table 'ext.src'", StringComparison.Ordinal)));
        CollectionAssert.AreEqual(new[] { "ext.src" }, model.ExternalLikeSources.ToArray());
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
