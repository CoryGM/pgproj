namespace PgProj.Parser.Tests;

[TestClass]
public sealed class ConstraintParsingTests
{
    [TestMethod]
    public void TableLevelPrimaryKeyCapturesKeyColumns()
    {
        var parser = new PgQueryParser();
        var tree = parser.Parse("CREATE TABLE public.orders (id int, customer_id int, PRIMARY KEY (id, customer_id));");

        Assert.IsFalse(tree.HasErrors, string.Join(Environment.NewLine, tree.Diagnostics.Select(static diagnostic => diagnostic.Message)));

        var constraint = tree.Constraints.Single(static c => c.ConstraintType == "CONSTR_PRIMARY");
        Assert.AreEqual("orders", constraint.TableName);
        CollectionAssert.AreEqual(new[] { "id", "customer_id" }, constraint.KeyColumns.ToArray());
    }

    [TestMethod]
    public void InlinePrimaryKeyCapturesEnclosingColumnName()
    {
        var parser = new PgQueryParser();
        var tree = parser.Parse("CREATE TABLE public.orders (id int PRIMARY KEY);");

        Assert.IsFalse(tree.HasErrors, string.Join(Environment.NewLine, tree.Diagnostics.Select(static diagnostic => diagnostic.Message)));

        var constraint = tree.Constraints.Single(static c => c.ConstraintType == "CONSTR_PRIMARY");
        CollectionAssert.AreEqual(new[] { "id" }, constraint.KeyColumns.ToArray());
    }

    [TestMethod]
    public void InlineUniqueCapturesEnclosingColumnName()
    {
        var parser = new PgQueryParser();
        var tree = parser.Parse("CREATE TABLE public.orders (id int, order_number text UNIQUE);");

        Assert.IsFalse(tree.HasErrors, string.Join(Environment.NewLine, tree.Diagnostics.Select(static diagnostic => diagnostic.Message)));

        var constraint = tree.Constraints.Single(static c => c.ConstraintType == "CONSTR_UNIQUE");
        CollectionAssert.AreEqual(new[] { "order_number" }, constraint.KeyColumns.ToArray());
    }

    [TestMethod]
    public void TableLevelUniqueCapturesKeyColumns()
    {
        var parser = new PgQueryParser();
        var tree = parser.Parse("CREATE TABLE public.orders (a int, b int, UNIQUE (a, b));");

        Assert.IsFalse(tree.HasErrors, string.Join(Environment.NewLine, tree.Diagnostics.Select(static diagnostic => diagnostic.Message)));

        var constraint = tree.Constraints.Single(static c => c.ConstraintType == "CONSTR_UNIQUE");
        CollectionAssert.AreEqual(new[] { "a", "b" }, constraint.KeyColumns.ToArray());
    }

    [TestMethod]
    public void TableLevelForeignKeyCapturesTargetAndColumns()
    {
        var parser = new PgQueryParser();
        var tree = parser.Parse(
            "CREATE TABLE public.orders (customer_id int, FOREIGN KEY (customer_id) REFERENCES public.customers (id) ON DELETE CASCADE);");

        Assert.IsFalse(tree.HasErrors, string.Join(Environment.NewLine, tree.Diagnostics.Select(static diagnostic => diagnostic.Message)));

        var constraint = tree.Constraints.Single(static c => c.ConstraintType == "CONSTR_FOREIGN");
        Assert.AreEqual("customers", constraint.ForeignKeyReferencedTableName);
        CollectionAssert.AreEqual(new[] { "customer_id" }, constraint.ForeignKeyColumns.ToArray());
        CollectionAssert.AreEqual(new[] { "id" }, constraint.ForeignKeyReferencedColumns.ToArray());
        Assert.AreEqual("c", constraint.ForeignKeyDeleteAction);
    }

    [TestMethod]
    public void InlineForeignKeyCapturesEnclosingColumnName()
    {
        var parser = new PgQueryParser();
        var tree = parser.Parse("CREATE TABLE public.orders (customer_id int REFERENCES public.customers (id));");

        Assert.IsFalse(tree.HasErrors, string.Join(Environment.NewLine, tree.Diagnostics.Select(static diagnostic => diagnostic.Message)));

        var constraint = tree.Constraints.Single(static c => c.ConstraintType == "CONSTR_FOREIGN");
        Assert.AreEqual("customers", constraint.ForeignKeyReferencedTableName);
        CollectionAssert.AreEqual(new[] { "customer_id" }, constraint.ForeignKeyColumns.ToArray());
        CollectionAssert.AreEqual(new[] { "id" }, constraint.ForeignKeyReferencedColumns.ToArray());
    }

    [TestMethod]
    public void CheckConstraintCapturesRawExpression()
    {
        var parser = new PgQueryParser();
        var tree = parser.Parse("CREATE TABLE public.orders (quantity int, CHECK (quantity > 0));");

        Assert.IsFalse(tree.HasErrors, string.Join(Environment.NewLine, tree.Diagnostics.Select(static diagnostic => diagnostic.Message)));

        var constraint = tree.Constraints.Single(static c => c.ConstraintType == "CONSTR_CHECK");
        Assert.IsFalse(string.IsNullOrWhiteSpace(constraint.RawCheckExpression));
        StringAssert.Contains(constraint.RawCheckExpression, "quantity");
    }

    [TestMethod]
    public void DeferrableForeignKeyCapturesDeferrableAndInitiallyDeferred()
    {
        var parser = new PgQueryParser();
        var tree = parser.Parse(
            "CREATE TABLE public.orders (customer_id int REFERENCES public.customers (id) DEFERRABLE INITIALLY DEFERRED);");

        Assert.IsFalse(tree.HasErrors, string.Join(Environment.NewLine, tree.Diagnostics.Select(static diagnostic => diagnostic.Message)));

        var constraint = tree.Constraints.Single(static c => c.ConstraintType == "CONSTR_FOREIGN");
        Assert.IsTrue(constraint.IsDeferrable);
        Assert.AreEqual(true, constraint.IsInitiallyDeferred);
    }

    [TestMethod]
    public void NotDeferrableConstraintLeavesDeferrableFalse()
    {
        var parser = new PgQueryParser();
        var tree = parser.Parse("CREATE TABLE public.orders (id int, CONSTRAINT uq_order UNIQUE (id) NOT DEFERRABLE);");

        Assert.IsFalse(tree.HasErrors, string.Join(Environment.NewLine, tree.Diagnostics.Select(static diagnostic => diagnostic.Message)));

        var constraint = tree.Constraints.Single(static c => c.ConstraintType == "CONSTR_UNIQUE");
        Assert.IsFalse(constraint.IsDeferrable);
    }

    [TestMethod]
    public void ExclusionConstraintCapturesAccessMethodElementsAndPredicate()
    {
        var parser = new PgQueryParser();
        var tree = parser.Parse(
            "CREATE TABLE public.reservations (room_id int, during tsrange, CONSTRAINT no_overlap EXCLUDE USING gist (room_id WITH =, during WITH &&) WHERE (room_id IS NOT NULL) DEFERRABLE INITIALLY DEFERRED);");

        Assert.IsFalse(tree.HasErrors, string.Join(Environment.NewLine, tree.Diagnostics.Select(static diagnostic => diagnostic.Message)));

        var constraint = tree.Constraints.Single(static c => c.ConstraintType == "CONSTR_EXCLUSION");
        Assert.AreEqual("gist", constraint.ExclusionAccessMethod);
        Assert.AreEqual(2, constraint.ExclusionElements.Count);
        Assert.AreEqual("room_id", constraint.ExclusionElements[0].Name);
        Assert.AreEqual("=", constraint.ExclusionElements[0].OperatorName);
        Assert.AreEqual("during", constraint.ExclusionElements[1].Name);
        Assert.AreEqual("&&", constraint.ExclusionElements[1].OperatorName);
        Assert.IsFalse(string.IsNullOrWhiteSpace(constraint.RawExclusionPredicateExpression));
        Assert.IsTrue(constraint.IsDeferrable);
        Assert.AreEqual(true, constraint.IsInitiallyDeferred);
    }
}
