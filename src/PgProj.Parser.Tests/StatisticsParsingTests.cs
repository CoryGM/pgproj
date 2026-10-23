namespace PgProj.Parser.Tests;

/// <summary>
/// G-150: <c>CREATE STATISTICS</c> is read instead of being warned about and ignored (G-106): its schema and name, kinds, columns and expressions in the order written, and
/// the table. A name and a table written without a schema take the default schema.
/// </summary>
[TestClass]
public sealed class StatisticsParsingTests
{
    [TestMethod]
    public void StatisticsAreReadWithTheirKindsColumnsAndTable()
    {
        var tree = new PgQueryParser().Parse("CREATE STATISTICS app.t_ab (dependencies, ndistinct) ON a, b FROM app.t;");
        var node = tree.Statistics.Single();

        Assert.AreEqual(0, tree.Diagnostics.Count);
        Assert.AreEqual("t_ab", node.Name);
        Assert.AreEqual("app", node.SchemaName);
        Assert.AreEqual("app", node.TableSchemaName);
        Assert.AreEqual("t", node.TableName);
        CollectionAssert.AreEqual(new[] { "dependencies", "ndistinct" }, node.Kinds.ToArray());
        CollectionAssert.AreEqual(new[] { "a", "b" }, node.Elements.Select(static element => element.Name).ToArray());
    }

    [TestMethod]
    public void NoKindsMeansNoneWrittenAndUnqualifiedNamesTakeTheDefaultSchema()
    {
        var node = new PgQueryParser().Parse("CREATE STATISTICS IF NOT EXISTS s2 ON a, b FROM t;").Statistics.Single();

        Assert.IsEmpty(node.Kinds);
        Assert.AreEqual("public", node.SchemaName);
        Assert.AreEqual("public", node.TableSchemaName);
    }

    [TestMethod]
    public void ExpressionsAreReadAsTextAndAsTheirTreeAndAreNotStatementsOfTheFile()
    {
        var tree = new PgQueryParser().Parse("CREATE STATISTICS s3 (mcv) ON (lower(a)), b, (a + b) FROM app.t;");
        var node = tree.Statistics.Single();

        Assert.AreEqual(0, tree.Diagnostics.Count);
        Assert.AreEqual(3, node.Elements.Count);
        Assert.IsNull(node.Elements[0].Name);
        StringAssert.Contains(node.Elements[0].RawExpression, "lower");
        StringAssert.Contains(node.Elements[0].ExpressionAst, "FuncCall");
        Assert.AreEqual("b", node.Elements[1].Name);
        StringAssert.Contains(node.Elements[2].ExpressionAst, "A_Expr");
        Assert.IsEmpty(tree.Nodes.OfType<ColumnNode>().ToArray());
    }

    [TestMethod]
    public void StatisticsAreNoLongerReportedAsUnmodeledButAlterStatisticsStillIs()
    {
        var created = new PgQueryParser().Parse("CREATE STATISTICS s ON a, b FROM t;");
        var altered = new PgQueryParser().Parse("ALTER STATISTICS s SET STATISTICS 100;");

        Assert.AreEqual(0, created.Diagnostics.Count);
        Assert.AreEqual(1, altered.Diagnostics.Count);
    }
}
