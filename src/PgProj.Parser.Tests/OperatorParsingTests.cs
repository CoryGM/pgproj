namespace PgProj.Parser.Tests;

/// <summary>
/// G-129: <c>CREATE OPERATOR</c> is read instead of being warned about and ignored (G-106): the schema and name, the operand types, the function, the
/// commutator and negator, the estimators, and the flags. An operator is in a schema, so an unqualified one is in the default schema.
/// </summary>
[TestClass]
public sealed class OperatorParsingTests
{
    [TestMethod]
    public void AnOperatorIsReadWithEveryOption()
    {
        // Act
        var tree = new PgQueryParser().Parse(
            "CREATE OPERATOR app.=== (LEFTARG = integer, RIGHTARG = public.mood, FUNCTION = app.f, COMMUTATOR = OPERATOR(app.===), NEGATOR = <>, RESTRICT = eqsel, JOIN = eqjoinsel, HASHES, MERGES);");
        var node = tree.Operators.Single();

        // Assert
        Assert.AreEqual(0, tree.Diagnostics.Count);
        Assert.AreEqual("===", node.Name);
        Assert.AreEqual("app", node.SchemaName);
        Assert.AreEqual("pg_catalog.int4", node.LeftType);
        Assert.AreEqual("public.mood", node.RightType);
        Assert.AreEqual("app.f", node.FunctionName);
        Assert.AreEqual("app.===", node.Commutator);
        Assert.AreEqual("<>", node.Negator);
        Assert.AreEqual("eqsel", node.RestrictFunction);
        Assert.AreEqual("eqjoinsel", node.JoinFunction);
        Assert.IsTrue(node.IsHashes);
        Assert.IsTrue(node.IsMerges);
    }

    [TestMethod]
    public void AnOperatorWithoutASchemaIsInTheDefaultSchema()
    {
        // Act
        var node = new PgQueryParser().Parse("CREATE OPERATOR ~~~ (RIGHTARG = text, PROCEDURE = f);").Operators.Single();

        // Assert
        Assert.AreEqual("public", node.SchemaName);
        Assert.IsTrue(node.IsSchemaDefaulted);
        Assert.IsNull(node.LeftType);
        Assert.AreEqual("text", node.RightType);
        Assert.AreEqual("f", node.FunctionName, "PROCEDURE is the older spelling of FUNCTION.");
        Assert.IsFalse(node.IsHashes);
    }

    [TestMethod]
    public void AnArrayOperandKeepsItsBrackets()
    {
        var node = new PgQueryParser().Parse("CREATE OPERATOR @@ (LEFTARG = integer[], FUNCTION = f);").Operators.Single();

        Assert.AreEqual("pg_catalog.int4[]", node.LeftType);
    }

    [TestMethod]
    public void AnOperatorIsNoLongerReportedAsUnmodeled()
    {
        // Act
        var tree = new PgQueryParser().Parse("CREATE OPERATOR public.=== (LEFTARG = int, RIGHTARG = int, FUNCTION = int4eq);");

        // Assert
        Assert.IsFalse(tree.Diagnostics.Any(static d => d.Message.Contains("CREATE OPERATOR", StringComparison.Ordinal)));
    }
}
