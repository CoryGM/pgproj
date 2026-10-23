using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Engine.Tests;

/// <summary>
/// G-306: <c>document</c> of a project listed no columns for a view, because a project has no server to ask; the columns are now worked out from the view's query, by PostgreSQL's
/// rules for a select-list item. When they cannot be known the list is empty rather than wrong. Also: a database's entries are <c>name type</c> and were shown whole as the name.
/// </summary>
[TestClass]
public sealed class ViewOutputColumnsTests
{
    private const string Tables = "CREATE TABLE orders (id int PRIMARY KEY, total numeric(10,2), note text); CREATE TABLE customers (id int, name text);";

    private static string[] Names(string viewSql, string viewName = "v")
    {
        var model = new SemanticPipeline().Execute([new PgQueryParser().Parse(Tables + viewSql)]);
        return [.. ViewOutputColumns.Derive(model.Views["public." + viewName], model).Select(static column => column.Name)];
    }

    [TestMethod]
    public void AColumnIsNamedAsItIsAndAnAliasWins()
        => CollectionAssert.AreEqual(new[] { "id", "the_total" }, Names("CREATE VIEW v AS SELECT id, total AS the_total FROM orders;"));

    [TestMethod]
    public void ACallIsNamedForTheFunctionACastForItsArgumentAndOtherThingsAreUnnamed()
        => CollectionAssert.AreEqual(
            new[] { "upper", "total", "numeric", "?column?", "coalesce", "case", "count" },
            Names("CREATE VIEW v AS SELECT upper(note), total::int, 5::numeric, total * 2, coalesce(note, 'x'), CASE WHEN id > 1 THEN 1 END, count(*) FROM orders GROUP BY note, total, id;"));

    [TestMethod]
    public void AStarIsExpandedInTheTablesOrderAndAQualifiedStarOnlyForItsRelation()
    {
        CollectionAssert.AreEqual(new[] { "id", "total", "note" }, Names("CREATE VIEW v AS SELECT * FROM orders;"));
        CollectionAssert.AreEqual(new[] { "id", "total", "note", "id", "name" }, Names("CREATE VIEW v AS SELECT * FROM orders JOIN customers ON true;"));
        CollectionAssert.AreEqual(new[] { "id", "name" }, Names("CREATE VIEW v AS SELECT c.* FROM orders o JOIN customers c ON c.id = o.id;"));
    }

    [TestMethod]
    public void AViewOverAViewTakesTheInnerViewsColumns()
        => CollectionAssert.AreEqual(new[] { "the_id", "name" }, Names("CREATE VIEW inner_v AS SELECT id AS the_id, name FROM customers; CREATE VIEW v AS SELECT * FROM inner_v;"));

    [TestMethod]
    public void AUnionTakesTheFirstLegsNamesAndValuesAreNumbered()
    {
        CollectionAssert.AreEqual(new[] { "id", "total" }, Names("CREATE VIEW v AS SELECT id, total FROM orders UNION ALL SELECT 1, 2;"));
        CollectionAssert.AreEqual(new[] { "column1", "column2" }, Names("CREATE VIEW v AS VALUES (1, 'a'), (2, 'b');"));
    }

    [TestMethod]
    public void ATheViewsColumnListNamesTheLeadingColumns()
        => CollectionAssert.AreEqual(new[] { "x", "y" }, Names("CREATE VIEW v (x, y) AS SELECT id, total FROM orders;"));

    [TestMethod]
    public void WhatCannotBeKnownIsLeftEmptyNotGuessed()
    {
        Assert.IsEmpty(Names("CREATE VIEW v AS SELECT * FROM somewhere_else.thing;"), "a star over a relation the project does not define");
        Assert.IsEmpty(Names("CREATE VIEW v AS SELECT * FROM (SELECT 1 AS a) s;"), "a star over a subquery");
        Assert.IsEmpty(Names("CREATE VIEW v AS SELECT * FROM generate_series(1, 3);"), "a star over a function");
    }

    [TestMethod]
    public void AColumnOfAProjectTableHasItsTypeAndAnExpressionHasNone()
    {
        var model = new SemanticPipeline().Execute([new PgQueryParser().Parse(Tables + "CREATE VIEW v AS SELECT id, total * 2 AS twice FROM orders;")]);

        var columns = ViewOutputColumns.Derive(model.Views["public.v"], model);

        Assert.IsNotNull(columns[0].Type);
        StringAssert.Contains(columns[0].Type, "int");
        Assert.IsNull(columns[1].Type);
    }

    [TestMethod]
    public void ADatabaseEntryIsANameAndATypeAndANameThatNeededQuotesIsUnquoted()
    {
        Assert.AreEqual(("id", "integer"), ViewOutputColumns.SplitEntry("id integer"));
        Assert.AreEqual(("total", "numeric(10,2)"), ViewOutputColumns.SplitEntry("total numeric(10,2)"));
        Assert.AreEqual(("Two Words", "text"), ViewOutputColumns.SplitEntry("\"Two Words\" text"));
        Assert.AreEqual(("a\"b", "character varying(5)"), ViewOutputColumns.SplitEntry("\"a\"\"b\" character varying(5)"));
        Assert.AreEqual(("bare", null), ViewOutputColumns.SplitEntry("bare"));
    }
}
