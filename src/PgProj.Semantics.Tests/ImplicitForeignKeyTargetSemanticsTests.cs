using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-43: a foreign key written without a referenced column list targets the primary key of the referenced
/// table, and the model must say so explicitly (as PostgreSQL reports it).
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ImplicitForeignKeyTargetSemanticsTests
{
    [TestMethod]
    public void ImplicitTargetResolvesToThePrimaryKeyColumns()
    {
        var model = BuildModel("""
            create table public.p (id bigint primary key);
            create table public.a (parent_id bigint references public.p);
            """);

        Assert.IsFalse(model.HasErrors, string.Join(Environment.NewLine, model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        var foreignKey = model.Constraints["public.a.a_parent_id_fkey"];
        CollectionAssert.AreEqual(new[] { "id" }, foreignKey.ForeignKeyReferencedColumns.ToArray());
        Assert.AreEqual("p", foreignKey.ForeignKeyReferencedTable);
    }

    [TestMethod]
    public void CompositeTargetUsesThePrimaryKeyColumnOrder()
    {
        var model = BuildModel("""
            create table public.p (x integer, y integer, primary key (y, x));
            create table public.a (a integer, b integer, foreign key (a, b) references public.p);
            """);

        CollectionAssert.AreEqual(new[] { "y", "x" }, model.Constraints["public.a.a_a_b_fkey"].ForeignKeyReferencedColumns.ToArray());
    }

    [TestMethod]
    public void PrimaryKeyDeclaredLaterByAlterTableInAnotherFileStillResolves()
    {
        var parser = new PgQueryParser();
        var model = new SemanticPipeline().Execute(
        [
            parser.Parse("create table public.a (parent_id bigint references public.p);"),
            parser.Parse("create table public.p (id bigint);"),
            parser.Parse("alter table public.p add primary key (id);"),
        ]);

        Assert.IsFalse(model.HasErrors, string.Join(Environment.NewLine, model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        CollectionAssert.AreEqual(new[] { "id" }, model.Constraints["public.a.a_parent_id_fkey"].ForeignKeyReferencedColumns.ToArray());
    }

    [TestMethod]
    public void SelfReferenceResolvesToItsOwnPrimaryKey()
    {
        var model = BuildModel("create table public.t (id bigint primary key, parent bigint references public.t);");

        CollectionAssert.AreEqual(new[] { "id" }, model.Constraints["public.t.t_parent_fkey"].ForeignKeyReferencedColumns.ToArray());
    }

    [TestMethod]
    public void ExplicitColumnListIsLeftAlone()
    {
        var model = BuildModel("""
            create table public.p (id bigint primary key, code text unique);
            create table public.a (parent_code text references public.p (code));
            """);

        CollectionAssert.AreEqual(new[] { "code" }, model.Constraints["public.a.a_parent_code_fkey"].ForeignKeyReferencedColumns.ToArray());
    }

    [TestMethod]
    public void ReferencedTableWithoutAPrimaryKeyIsAnError()
    {
        var model = BuildModel("""
            create table public.p (id bigint unique);
            create table public.a (parent_id bigint references public.p);
            """);

        Assert.IsTrue(model.HasErrors);
        Assert.IsTrue(model.Diagnostics.Any(static diagnostic => diagnostic.Message.Contains("has no primary key to reference", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ColumnCountMismatchWithThePrimaryKeyIsAnError()
    {
        var model = BuildModel("""
            create table public.p (x integer, y integer, primary key (x, y));
            create table public.a (x integer references public.p);
            """);

        Assert.IsTrue(model.HasErrors);
        Assert.IsTrue(model.Diagnostics.Any(static diagnostic => diagnostic.Message.Contains("column(s)", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void MissingReferencedTableKeepsTheExistingSingleError()
    {
        // schemas is populated so G-83's schema-scope leniency doesn't mask this genuine, in-schema missing table.
        var model = BuildModel("create table public.a (parent_id bigint references public.missing);", schemas: ["public"]);

        Assert.IsTrue(model.HasErrors);
        Assert.IsFalse(model.Diagnostics.Any(static diagnostic => diagnostic.Message.Contains("has no primary key", StringComparison.Ordinal)));
    }

    private static DatabaseModel BuildModel(string sql, IReadOnlyList<string>? schemas = null)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)], schemas);
}
