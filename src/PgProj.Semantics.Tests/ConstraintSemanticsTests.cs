using PgProj.Parser;

namespace PgProj.Semantics.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ConstraintSemanticsTests
{
    [TestMethod]
    public void TableLevelPrimaryKeyPopulatesOrderedColumns()
    {
        const string sql = "CREATE TABLE public.orders (id int, customer_id int, PRIMARY KEY (id, customer_id));";

        var model = BuildModel(sql);

        Assert.IsFalse(model.HasErrors, string.Join(Environment.NewLine, model.Diagnostics.Select(static d => d.Message)));
        var constraint = model.Constraints.Values.SingleOrDefault(static c => c.ConstraintType == "primary key");
        Assert.IsNotNull(constraint, "Constraint types found: " + string.Join(",", model.Constraints.Values.Select(c => c.ConstraintType ?? "<null>")));
        CollectionAssert.AreEqual(new[] { "id", "customer_id" }, constraint.PrimaryKeyColumns.ToArray());
    }

    [TestMethod]
    public void UniqueColumnOrderDoesNotAffectEquivalence()
    {
        const string sqlForward = "CREATE TABLE public.t (a int, b int, UNIQUE (a, b));";
        const string sqlReversed = "CREATE TABLE public.t (a int, b int, UNIQUE (b, a));";

        var forward = BuildModel(sqlForward).Constraints.Values.Single(static c => c.ConstraintType == "unique");
        var reversed = BuildModel(sqlReversed).Constraints.Values.Single(static c => c.ConstraintType == "unique");

        CollectionAssert.AreEquivalent(forward.UniqueColumns.ToArray(), reversed.UniqueColumns.ToArray());
    }

    [TestMethod]
    public void ForeignKeyPopulatesReferencedTableAndColumns()
    {
        const string sql =
            "CREATE TABLE public.customers (id int PRIMARY KEY); " +
            "CREATE TABLE public.orders (customer_id int, FOREIGN KEY (customer_id) REFERENCES public.customers (id));";

        var model = BuildModel(sql);

        Assert.IsFalse(model.HasErrors, string.Join(Environment.NewLine, model.Diagnostics.Select(static d => d.Message)));
        var constraint = model.Constraints.Values.Single(static c => c.ConstraintType == "foreign key");
        Assert.AreEqual("customers", constraint.ForeignKeyReferencedTable);
        CollectionAssert.AreEqual(new[] { "id" }, constraint.ForeignKeyReferencedColumns.ToArray());
        Assert.IsTrue(constraint.ReferencedTables.Contains("public.customers"));
    }

    [TestMethod]
    public void ForeignKeyPopulatesLocalColumnsSchemaAndActions()
    {
        const string sql =
            "CREATE TABLE public.customers (id int PRIMARY KEY); " +
            "CREATE TABLE public.orders (customer_id int, FOREIGN KEY (customer_id) REFERENCES public.customers (id) ON DELETE CASCADE ON UPDATE SET NULL);";

        var model = BuildModel(sql);

        Assert.IsFalse(model.HasErrors, string.Join(Environment.NewLine, model.Diagnostics.Select(static d => d.Message)));
        var constraint = model.Constraints.Values.Single(static c => c.ConstraintType == "foreign key");
        CollectionAssert.AreEqual(new[] { "customer_id" }, constraint.ForeignKeyColumns.ToArray());
        Assert.AreEqual("public", constraint.ForeignKeyReferencedSchemaName);
        Assert.AreEqual("c", constraint.ForeignKeyDeleteAction);
        Assert.AreEqual("n", constraint.ForeignKeyUpdateAction);
    }

    [TestMethod]
    public void CheckConstraintNormalizesExpressionForPhantomDiff()
    {
        const string sqlLower = "CREATE TABLE public.orders (quantity int, CHECK (quantity > 0));";
        const string sqlSpaced = "CREATE TABLE public.orders (quantity int, CHECK ( QUANTITY>0 ));";

        var lower = BuildModel(sqlLower).Constraints.Values.Single(static c => c.ConstraintType == "check");
        var spaced = BuildModel(sqlSpaced).Constraints.Values.Single(static c => c.ConstraintType == "check");

        Assert.IsNotNull(lower.CheckExpressionNode);
        Assert.IsNotNull(spaced.CheckExpressionNode);
        CollectionAssert.AreEqual(lower.NormalizedExpressions.ToArray(), spaced.NormalizedExpressions.ToArray());
    }

    private static DatabaseModel BuildModel(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
