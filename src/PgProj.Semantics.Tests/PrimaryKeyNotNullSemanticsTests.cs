using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-39: PostgreSQL forces NOT NULL on primary-key columns however the key is declared, so the
/// project model must report those columns as not nullable when nullability is unspecified.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class PrimaryKeyNotNullSemanticsTests
{
    [TestMethod]
    public void InlinePrimaryKeyColumnIsNotNullable()
    {
        var model = BuildModel("create table public.t (id bigint primary key, name text);");

        Assert.AreEqual(false, GetColumn(model, "public.t", "id").IsNullable);
    }

    [TestMethod]
    public void TableLevelCompositePrimaryKeyMakesEveryKeyColumnNotNullable()
    {
        var model = BuildModel("create table public.t (a integer, b integer, c integer, constraint t_pk primary key (a, b));");

        Assert.AreEqual(false, GetColumn(model, "public.t", "a").IsNullable);
        Assert.AreEqual(false, GetColumn(model, "public.t", "b").IsNullable);
        Assert.IsNull(GetColumn(model, "public.t", "c").IsNullable);
    }

    [TestMethod]
    public void PrimaryKeyAddedByAlterTableMakesColumnNotNullable()
    {
        var model = BuildModel("""
            create table public.t (id bigint, name text);
            alter table public.t add constraint t_pk primary key (id);
            """);

        Assert.AreEqual(false, GetColumn(model, "public.t", "id").IsNullable);
        Assert.IsNull(GetColumn(model, "public.t", "name").IsNullable);
    }

    [TestMethod]
    public void PrimaryKeyDeclaredInAnotherSyntaxTreeStillApplies()
    {
        var parser = new PgQueryParser();
        var model = new SemanticPipeline().Execute(
        [
            parser.Parse("create table public.t (id bigint, name text);"),
            parser.Parse("alter table public.t add primary key (id);"),
        ]);

        Assert.AreEqual(false, GetColumn(model, "public.t", "id").IsNullable);
    }

    [TestMethod]
    public void PrimaryKeyOnQuotedMixedCaseColumnIsMatchedCaseInsensitively()
    {
        var model = BuildModel("create table public.t (\"Id\" bigint, constraint t_pk primary key (\"Id\"));");

        Assert.AreEqual(false, model.Tables["public.t"].Columns.Single().IsNullable);
    }

    [TestMethod]
    public void NonPrimaryKeyColumnsAndUniqueConstraintsStayUnspecified()
    {
        var model = BuildModel("create table public.t (id bigint unique, name text, constraint t_name_key unique (name));");

        Assert.IsNull(GetColumn(model, "public.t", "id").IsNullable);
        Assert.IsNull(GetColumn(model, "public.t", "name").IsNullable);
    }

    [TestMethod]
    public void ExplicitNotNullOnPrimaryKeyColumnIsUnchanged()
    {
        var model = BuildModel("create table public.t (id bigint not null primary key);");

        Assert.AreEqual(false, GetColumn(model, "public.t", "id").IsNullable);
    }

    private static ColumnDefinition GetColumn(DatabaseModel model, string tableKey, string columnName)
        => model.Tables[tableKey].Columns.Single(column => string.Equals(column.Name, columnName, StringComparison.OrdinalIgnoreCase));

    private static DatabaseModel BuildModel(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
