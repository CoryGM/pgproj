namespace PgProj.Parser.Tests;

/// <summary>
/// G-128: <c>CREATE RULE</c> is read instead of being warned about and ignored (G-106): the relation it is on, its event, whether it is <c>INSTEAD</c>, and its
/// text as written. A rule is scoped to its table or view, so an unqualified relation is in the default schema.
/// </summary>
[TestClass]
public sealed class RuleParsingTests
{
    [TestMethod]
    public void ARuleIsReadWithItsRelationEventAndText()
    {
        // Act
        var tree = new PgQueryParser().Parse("CREATE OR REPLACE RULE r AS ON UPDATE TO s.v WHERE new.n > 0 DO INSTEAD (UPDATE s.t SET n = new.n WHERE id = old.id; INSERT INTO s.log VALUES (old.id));");
        var rule = tree.Rules.Single();

        // Assert
        Assert.AreEqual(0, tree.Diagnostics.Count);
        Assert.AreEqual("r", rule.Name);
        Assert.AreEqual("s", rule.SchemaName);
        Assert.AreEqual("v", rule.TableName);
        Assert.AreEqual("update", rule.Event);
        Assert.IsTrue(rule.IsInstead);
        StringAssert.StartsWith(rule.RawDefinition, "CREATE OR REPLACE RULE r AS ON UPDATE TO s.v");
        Assert.IsFalse(rule.RawDefinition!.EndsWith(';'));
        StringAssert.Contains(rule.QueryAst, "UpdateStmt");
    }

    [TestMethod]
    [DataRow("CREATE RULE r AS ON INSERT TO t DO ALSO NOTHING;", "insert", false)]
    [DataRow("CREATE RULE r AS ON DELETE TO t DO INSTEAD NOTHING;", "delete", true)]
    [DataRow("CREATE RULE r AS ON UPDATE TO t DO NOTHING;", "update", false)]
    [DataRow("CREATE RULE r AS ON SELECT TO t DO INSTEAD SELECT 1;", "select", true)]
    public void TheEventAndInsteadAreRead(string sql, string ruleEvent, bool instead)
    {
        var rule = new PgQueryParser().Parse(sql).Rules.Single();

        Assert.AreEqual(ruleEvent, rule.Event);
        Assert.AreEqual(instead, rule.IsInstead);
    }

    [TestMethod]
    public void ARelationWithoutASchemaIsInTheDefaultSchema()
    {
        var rule = new PgQueryParser().Parse("CREATE RULE r AS ON DELETE TO t DO INSTEAD NOTHING;").Rules.Single();

        Assert.AreEqual("public", rule.SchemaName);
        Assert.IsTrue(rule.IsSchemaDefaulted);
    }

    [TestMethod]
    public void ARuleIsNoLongerReportedAsUnmodeledAndItsActionsAreNotStatementsOfTheFile()
    {
        var tree = new PgQueryParser().Parse("CREATE RULE r AS ON INSERT TO public.v DO INSTEAD INSERT INTO public.t VALUES (new.id);");

        Assert.IsFalse(tree.Diagnostics.Any(static d => d.Message.Contains("CREATE RULE", StringComparison.Ordinal)));
        Assert.AreEqual(1, tree.Nodes.Count(static node => node is RuleNode));
        Assert.AreEqual(1, tree.Nodes.Count);
    }
}
