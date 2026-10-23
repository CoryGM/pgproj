using PgProj.Parser;

namespace PgProj.Parser.Tests;

/// <summary>
/// G-47: an <c>ALTER TABLE</c> statement's actions were ignored without a trace. <c>ADD COLUMN</c> is now captured (with everything
/// a <c>CREATE TABLE</c> column carries) and every other action the model has no place for is recorded so it can be reported.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class AlterTableCommandParsingTests
{
    [TestMethod]
    public void AddColumnIsCapturedWithItsTypeNullabilityAndDefault()
    {
        var add = Parse("ALTER TABLE public.customers ADD COLUMN phone varchar(20) NOT NULL DEFAULT 'x';").Nodes.OfType<AlterTableAddColumnNode>().Single();

        Assert.AreEqual("customers", add.TableName);
        Assert.AreEqual("public", add.SchemaName);
        Assert.IsFalse(add.IfNotExists);
        Assert.AreEqual("phone", add.Column.Name);
        Assert.AreEqual("customers", add.Column.TableName);
        Assert.IsFalse(add.Column.IsNullable ?? true);
        Assert.AreEqual("'x'", add.Column.RawDefaultExpression);
        CollectionAssert.AreEqual(new[] { "20" }, add.Column.TypeModifiers!.ToArray());
    }

    [TestMethod]
    public void IfNotExistsIsCaptured()
    {
        var adds = Parse("ALTER TABLE public.t ADD COLUMN IF NOT EXISTS a int, ADD COLUMN b int;").Nodes.OfType<AlterTableAddColumnNode>().ToArray();

        Assert.HasCount(2, adds);
        Assert.IsTrue(adds[0].IfNotExists);
        Assert.IsFalse(adds[1].IfNotExists);
        CollectionAssert.AreEqual(new[] { "a", "b" }, adds.Select(static add => add.Column.Name).ToArray());
    }

    [TestMethod]
    public void AddColumnWithoutTheColumnKeywordIsCaptured()
    {
        var add = Parse("ALTER TABLE t ADD c int;").Nodes.OfType<AlterTableAddColumnNode>().Single();

        Assert.AreEqual("c", add.Column.Name);
        Assert.AreEqual("public", add.SchemaName, "An unqualified table is the one in public (G-114).");
    }

    [TestMethod]
    public void AColumnWithAnInlineConstraintStillYieldsItsConstraint()
    {
        var tree = Parse("ALTER TABLE public.t ADD COLUMN parent_id bigint REFERENCES public.p (id);");

        Assert.HasCount(1, tree.Nodes.OfType<AlterTableAddColumnNode>().ToArray());
        var constraint = tree.Nodes.OfType<ConstraintNode>().Single();
        Assert.AreEqual("t", constraint.TableName);
    }

    [TestMethod]
    [DataRow("ALTER TABLE public.t DROP COLUMN b;", "DROP COLUMN")]
    [DataRow("ALTER TABLE public.t ALTER COLUMN a TYPE bigint;", "ALTER COLUMN ... TYPE")]
    [DataRow("ALTER TABLE public.t ALTER COLUMN a SET STATISTICS 100;", "ALTER COLUMN ... SET STATISTICS")]
    [DataRow("ALTER TABLE public.t ALTER COLUMN a SET NOT NULL;", "ALTER COLUMN ... SET/DROP NOT NULL")]
    [DataRow("ALTER TABLE public.t ALTER COLUMN a DROP NOT NULL;", "ALTER COLUMN ... SET/DROP NOT NULL")]
    [DataRow("ALTER TABLE public.t OWNER TO CURRENT_USER;", "OWNER TO")]
    [DataRow("ALTER TABLE public.t DROP CONSTRAINT c;", "DROP CONSTRAINT")]
    public void AnActionTheModelDoesNotRepresentIsRecorded(string sql, string action)
    {
        var unsupported = Parse(sql).Nodes.OfType<UnsupportedAlterTableNode>().Single();

        Assert.AreEqual("t", unsupported.TableName);
        Assert.AreEqual("public", unsupported.SchemaName);
        Assert.AreEqual(action, unsupported.Action);
    }

    [TestMethod]
    public void EveryActionOfAStatementIsHandledSeparately()
    {
        var tree = Parse("ALTER TABLE public.t ADD COLUMN x int, DROP COLUMN y, OWNER TO postgres;");

        Assert.HasCount(1, tree.Nodes.OfType<AlterTableAddColumnNode>().ToArray());
        CollectionAssert.AreEqual(new[] { "DROP COLUMN" }, tree.Nodes.OfType<UnsupportedAlterTableNode>().Select(static node => node.Action).ToArray());
        // The owner is a model of its own now (G-146), no longer an action that is ignored.
        var owner = tree.Owners.Single();
        Assert.AreEqual("Table", owner.ObjectKind);
        Assert.AreEqual("postgres", owner.OwnerRole);
    }

    [TestMethod]
    [DataRow("ALTER TABLE public.t ENABLE ROW LEVEL SECURITY;", true, null)]
    [DataRow("ALTER TABLE public.t DISABLE ROW LEVEL SECURITY;", false, null)]
    [DataRow("ALTER TABLE public.t FORCE ROW LEVEL SECURITY;", null, true)]
    [DataRow("ALTER TABLE public.t NO FORCE ROW LEVEL SECURITY;", null, false)]
    public void RowLevelSecurityActionsAreCapturedNotReportedAsUnsupported(string sql, bool? enable, bool? force)
    {
        Assert.IsEmpty(Parse(sql).Nodes.OfType<UnsupportedAlterTableNode>().ToArray());
        var node = Parse(sql).Nodes.OfType<AlterTableRowSecurityNode>().Single();

        Assert.AreEqual("t", node.TableName);
        Assert.AreEqual("public", node.SchemaName);
        Assert.AreEqual(enable, node.EnableRowSecurity);
        Assert.AreEqual(force, node.ForceRowSecurity);
    }

    [TestMethod]
    public void AddConstraintIsNotReportedAsUnsupported()
    {
        var tree = Parse("ALTER TABLE public.t ADD CONSTRAINT c CHECK (a > 0);");

        Assert.IsEmpty(tree.Nodes.OfType<UnsupportedAlterTableNode>().ToArray());
        Assert.IsEmpty(tree.Nodes.OfType<AlterTableAddColumnNode>().ToArray());
        Assert.HasCount(1, tree.Nodes.OfType<ConstraintNode>().ToArray());
    }

    [TestMethod]
    public void EachAddedColumnKeepsItsOwnDefault()
    {
        var adds = Parse("ALTER TABLE ONLY public.t ADD COLUMN a int DEFAULT 1, ADD COLUMN b text, ADD COLUMN IF NOT EXISTS c int DEFAULT coalesce(1, 2) NOT NULL;")
            .Nodes.OfType<AlterTableAddColumnNode>().ToDictionary(static add => add.Column.Name!, static add => add.Column);

        Assert.AreEqual("1", adds["a"].RawDefaultExpression);
        Assert.IsNull(adds["b"].RawDefaultExpression);
        Assert.AreEqual("coalesce(1, 2)", adds["c"].RawDefaultExpression);
        Assert.IsFalse(adds["c"].IsNullable ?? true);
    }

    [TestMethod]
    public void ADefaultInAnAddConstraintDoesNotBecomeAColumnDefault()
    {
        var add = Parse("ALTER TABLE public.t ADD COLUMN a int, ADD CONSTRAINT a_check CHECK (a > 0);").Nodes.OfType<AlterTableAddColumnNode>().Single();

        Assert.IsNull(add.Column.RawDefaultExpression);
    }

    [TestMethod]
    public void ADefaultThatCallsAFunctionKeepsItsText()
    {
        var add = Parse("ALTER TABLE public.t ADD COLUMN created timestamptz NOT NULL DEFAULT public.utc_now();").Nodes.OfType<AlterTableAddColumnNode>().Single();

        Assert.AreEqual("public.utc_now()", add.Column.RawDefaultExpression);
    }

    [TestMethod]
    public void ACreateTableProducesNoAlterTableNodes()
    {
        var tree = Parse("CREATE TABLE public.t (a int, b text);");

        Assert.IsEmpty(tree.Nodes.OfType<AlterTableAddColumnNode>().ToArray());
        Assert.IsEmpty(tree.Nodes.OfType<UnsupportedAlterTableNode>().ToArray());
    }

    private static SqlSyntaxTree Parse(string sql) => new PgQueryParser().Parse(sql);
}
