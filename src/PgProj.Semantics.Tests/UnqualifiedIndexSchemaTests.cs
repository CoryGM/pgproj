using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-113: an index lives in its table's schema, so one declared without a schema takes its table's. The live model reads every index on a
/// <c>public</c> table this way (<c>pg_get_indexdef</c> prints <c>ON t</c>, not <c>ON public.t</c>), and its key never matched the project's.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class UnqualifiedIndexSchemaTests
{
    [TestMethod]
    public void AnIndexReadAsPgGetIndexdefPrintsItTakesItsPublicTablesSchema()
    {
        var model = Build("CREATE TABLE public.t (z int); CREATE INDEX t_z_idx ON t USING btree (z);");

        var index = model.Indexes.Values.Single();
        Assert.AreEqual("public.t_z_idx", index.Key);
        Assert.AreEqual("public", index.SchemaName);
        Assert.IsEmpty(model.Diagnostics, string.Join("; ", model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    [TestMethod]
    public void TheResolvedIndexStillGetsItsExpressionAndDependencies()
    {
        var model = Build("CREATE TABLE public.t (note text); CREATE INDEX t_lower_idx ON t USING btree (lower(note)) WHERE note IS NOT NULL;");

        var index = model.Indexes["public.t_lower_idx"];
        Assert.IsNotEmpty(index.KeyElements);
        Assert.IsNotNull(index.NormalizedPredicateExpression);
        CollectionAssert.Contains(index.ReferencedTables.ToList(), "public.t");
        Assert.IsEmpty(model.Diagnostics, string.Join("; ", model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    [TestMethod]
    public void TheTableMayBeDeclaredInALaterFile()
    {
        var model = new SemanticPipeline().Execute(
        [
            new PgQueryParser().Parse("CREATE INDEX t_z_idx ON t (z); COMMENT ON INDEX public.t_z_idx IS 'z lookup';"),
            new PgQueryParser().Parse("CREATE TABLE public.t (z int);"),
        ]);

        var index = model.Indexes["public.t_z_idx"];
        Assert.AreEqual("z lookup", index.Comment, "A comment written against the qualified name still attaches.");
    }

    [TestMethod]
    public void APublicTableWinsOverASameNamedTableElsewhere()
    {
        var model = Build("CREATE SCHEMA s; CREATE TABLE s.t (z int); CREATE TABLE public.t (z int); CREATE INDEX i ON t (z);");

        Assert.AreEqual("public.i", model.Indexes.Values.Single().Key);
    }

    [TestMethod]
    public void AnUnqualifiedIndexIsInPublicEvenWhenItsTableNameExistsOnlyElsewhere()
    {
        // G-114: an unqualified name is the object in public, as PostgreSQL's default search_path finds it; a table only in s is not
        // found by `ON t` on a server either. (G-113 used to fall back to the only table of that name here.)
        var model = Build("CREATE SCHEMA s; CREATE TABLE s.t (z int); CREATE INDEX i ON t (z);");

        Assert.AreEqual("public.i", model.Indexes.Values.Single().Key);
    }

    [TestMethod]
    public void AQualifiedIndexIsUnaffected()
    {
        var model = Build("CREATE SCHEMA s; CREATE TABLE s.t (z int); CREATE TABLE public.t (z int); CREATE INDEX i ON s.t (z);");

        Assert.AreEqual("s.i", model.Indexes.Values.Single().Key);
    }

    [TestMethod]
    public void DeclarationOrderIsKept()
    {
        // Re-keying must not move an index to the end: a publish emits indexes in declaration order (G-112).
        var model = Build("CREATE TABLE public.t (z int, a int); CREATE INDEX t_z_idx ON t (z); CREATE INDEX t_a_idx ON public.t (a);");

        CollectionAssert.AreEqual(new[] { "public.t_z_idx", "public.t_a_idx" }, model.Indexes.Keys.ToArray());
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
