namespace PgProj.Parser.Tests;

[TestClass]
public sealed class IndexParsingTests
{
    [TestMethod]
    public void CreateIndexProducesOrderedKeyColumns()
    {
        var parser = new PgQueryParser();
        var tree = parser.Parse("CREATE INDEX ix_orders_customer_date ON public.orders (customer_id, order_date);");

        Assert.IsFalse(tree.HasErrors, string.Join(Environment.NewLine, tree.Diagnostics.Select(static diagnostic => diagnostic.Message)));

        var index = tree.Indexes.Single();
        Assert.AreEqual("ix_orders_customer_date", index.Name);
        Assert.AreEqual("public", index.SchemaName);
        Assert.AreEqual("orders", index.TableName);
        CollectionAssert.AreEqual(new[] { "customer_id", "order_date" }, index.KeyColumns.ToArray());
        Assert.IsFalse(index.IsUnique);
    }

    [TestMethod]
    public void CreateUniqueIndexSetsIsUniqueTrue()
    {
        var parser = new PgQueryParser();
        var tree = parser.Parse("CREATE UNIQUE INDEX ix_orders_number ON public.orders (order_number);");

        Assert.IsFalse(tree.HasErrors, string.Join(Environment.NewLine, tree.Diagnostics.Select(static diagnostic => diagnostic.Message)));

        var index = tree.Indexes.Single();
        Assert.IsTrue(index.IsUnique);
        CollectionAssert.AreEqual(new[] { "order_number" }, index.KeyColumns.ToArray());
    }

    [TestMethod]
    public void CreateIndexPreservesKeyColumnOrder()
    {
        var parser = new PgQueryParser();
        var forward = parser.Parse("CREATE INDEX ix_ab ON public.t (a, b);").Indexes.Single();
        var reversed = parser.Parse("CREATE INDEX ix_ba ON public.t (b, a);").Indexes.Single();

        CollectionAssert.AreEqual(new[] { "a", "b" }, forward.KeyColumns.ToArray());
        CollectionAssert.AreEqual(new[] { "b", "a" }, reversed.KeyColumns.ToArray());
    }

    [TestMethod]
    public void CreateIndexWithAccessMethodCapturesAccessMethod()
    {
        var parser = new PgQueryParser();
        var tree = parser.Parse("CREATE INDEX ix_orders_data ON public.orders USING gin (data);");

        var index = tree.Indexes.Single();
        Assert.AreEqual("gin", index.AccessMethod);
        CollectionAssert.AreEqual(new[] { "data" }, index.KeyColumns.ToArray());
    }

    [TestMethod]
    public void CreateIndexWithIncludeCapturesIncludeColumns()
    {
        var parser = new PgQueryParser();
        var tree = parser.Parse("CREATE INDEX ix_orders_covering ON public.orders (customer_id) INCLUDE (status, total);");

        var index = tree.Indexes.Single();
        CollectionAssert.AreEqual(new[] { "status", "total" }, index.IncludeColumns.ToArray());
    }

    [TestMethod]
    public void CreateIndexWithWhereClauseCapturesRawPredicateExpression()
    {
        var parser = new PgQueryParser();
        var tree = parser.Parse("CREATE INDEX ix_orders_active ON public.orders (customer_id) WHERE deleted_at IS NULL;");

        var index = tree.Indexes.Single();
        Assert.IsFalse(string.IsNullOrWhiteSpace(index.RawPredicateExpression));
        StringAssert.Contains(index.RawPredicateExpression!, "deleted_at");
    }

    [TestMethod]
    public void CreateIndexWithExpressionCollationOpclassAndOrderingCapturesKeyElements()
    {
        var parser = new PgQueryParser();
        var tree = parser.Parse(
            "CREATE INDEX ix_orders_expr ON public.orders (lower(email) COLLATE \"C\" text_pattern_ops DESC NULLS FIRST, customer_id);");

        var index = tree.Indexes.Single();
        Assert.AreEqual(2, index.KeyElements.Count);

        var first = index.KeyElements[0];
        Assert.IsNull(first.Name);
        StringAssert.Contains(first.RawExpression!, "lower");
        Assert.AreEqual("C", first.CollationName);
        Assert.AreEqual("text_pattern_ops", first.OperatorClassName);
        Assert.IsTrue(first.IsDescending);
        Assert.AreEqual(true, first.NullsFirst);

        var second = index.KeyElements[1];
        Assert.AreEqual("customer_id", second.Name);
        Assert.IsFalse(second.IsDescending);
    }
}
