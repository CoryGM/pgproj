using PgProj.Parser;

namespace PgProj.Parser.Tests;

/// <summary>
/// G-76: <c>CREATE POLICY</c> was not recognized by the parser at all. Shapes here were confirmed against real pg_query AST
/// dumps before writing this parser code (G-76): the table identity lives under
/// <c>"table"</c>, not <c>"relation"</c>; <c>cmd_name</c> and <c>roles</c> are always present with PostgreSQL's own defaults
/// filled in; <c>permissive</c> is present as <c>true</c> for an explicit/omitted <c>PERMISSIVE</c> and absent entirely
/// (not <c>false</c>) for <c>RESTRICTIVE</c>.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class PolicyParsingTests
{
    [TestMethod]
    public void ABarePolicyGetsEveryDefault()
    {
        var policy = Parse("CREATE POLICY p ON t;");

        Assert.AreEqual("p", policy.Name);
        Assert.AreEqual("public", policy.SchemaName, "An unqualified table is the one in public (G-114).");
        Assert.AreEqual("t", policy.TableName);
        Assert.AreEqual("all", policy.Command);
        Assert.IsTrue(policy.IsPermissive);
        CollectionAssert.AreEqual(new[] { "PUBLIC" }, policy.Roles.ToArray());
        Assert.IsNull(policy.RawUsingExpression);
        Assert.IsNull(policy.RawWithCheckExpression);
    }

    [TestMethod]
    public void ASchemaQualifiedTableIsCaptured()
    {
        var policy = Parse("CREATE POLICY p ON s.t AS RESTRICTIVE USING (true);");

        Assert.AreEqual("s", policy.SchemaName);
        Assert.AreEqual("t", policy.TableName);
    }

    [TestMethod]
    public void RestrictiveIsAbsentNotFalse()
    {
        Assert.IsFalse(Parse("CREATE POLICY p ON t AS RESTRICTIVE USING (true);").IsPermissive);
    }

    [TestMethod]
    public void PermissiveExplicitOrOmittedIsTheSame()
    {
        Assert.IsTrue(Parse("CREATE POLICY p ON t AS PERMISSIVE USING (true);").IsPermissive);
        Assert.IsTrue(Parse("CREATE POLICY p ON t USING (true);").IsPermissive);
    }

    [TestMethod]
    [DataRow("SELECT", "select")]
    [DataRow("INSERT", "insert")]
    [DataRow("UPDATE", "update")]
    [DataRow("DELETE", "delete")]
    [DataRow("ALL", "all")]
    public void EveryForCommandIsCapturedLowercase(string written, string expected)
    {
        Assert.AreEqual(expected, Parse($"CREATE POLICY p ON t FOR {written} USING (true);").Command);
    }

    [TestMethod]
    public void ForOmittedDefaultsToAll()
    {
        Assert.AreEqual("all", Parse("CREATE POLICY p ON t USING (true);").Command);
    }

    [TestMethod]
    public void RolesAreCapturedInTheOrderWritten()
    {
        var policy = Parse("CREATE POLICY p ON t TO app_user, other_role USING (true);");

        CollectionAssert.AreEqual(new[] { "app_user", "other_role" }, policy.Roles.ToArray());
    }

    [TestMethod]
    public void ToPublicIsCapturedExplicitly()
    {
        CollectionAssert.AreEqual(new[] { "PUBLIC" }, Parse("CREATE POLICY p ON t TO PUBLIC USING (true);").Roles.ToArray());
    }

    [TestMethod]
    public void UsingExpressionIsKeptAsWritten()
    {
        var policy = Parse("CREATE POLICY p ON t USING (owner_id = current_setting('app.user_id')::integer);");

        Assert.AreEqual("owner_id = current_setting('app.user_id')::integer", policy.RawUsingExpression);
        Assert.IsNotNull(policy.UsingExpressionAst);
    }

    [TestMethod]
    public void WithCheckExpressionIsKeptAsWritten()
    {
        var policy = Parse("CREATE POLICY p ON t FOR INSERT WITH CHECK (status = 'active');");

        Assert.AreEqual("status = 'active'", policy.RawWithCheckExpression);
        Assert.IsNotNull(policy.WithCheckExpressionAst);
        Assert.IsNull(policy.RawUsingExpression);
    }

    [TestMethod]
    public void UsingAndWithCheckCanBothBePresent()
    {
        var policy = Parse("CREATE POLICY p ON t USING (owner_id = 1) WITH CHECK (status = 'active');");

        Assert.AreEqual("owner_id = 1", policy.RawUsingExpression);
        Assert.AreEqual("status = 'active'", policy.RawWithCheckExpression);
    }

    /// <summary>
    /// A nested subquery inside USING can legitimately contain the word "with" (a CTE) at a deeper paren depth; the top-level
    /// scan for the WITH CHECK clause must not be confused by it (see ConstraintIndexTextReader.TryReadKeywordParenthesizedExpression).
    /// </summary>
    [TestMethod]
    public void ANestedWithInsideUsingIsNotMistakenForWithCheck()
    {
        var policy = Parse("CREATE POLICY p ON t USING (owner_id IN (WITH x AS (SELECT 1 AS id) SELECT id FROM x)) WITH CHECK (true);");

        StringAssert.Contains(policy.RawUsingExpression!, "WITH x AS");
        Assert.AreEqual("true", policy.RawWithCheckExpression);
    }

    [TestMethod]
    public void APolicyWithEverythingWrittenCapturesItAll()
    {
        var policy = Parse(
            "CREATE POLICY p1 ON t AS PERMISSIVE FOR SELECT TO app_user, other_role "
            + "USING (owner_id = current_setting('app.user_id')::integer) WITH CHECK (status = 'active');");

        Assert.AreEqual("p1", policy.Name);
        Assert.AreEqual("select", policy.Command);
        Assert.IsTrue(policy.IsPermissive);
        CollectionAssert.AreEqual(new[] { "app_user", "other_role" }, policy.Roles.ToArray());
        Assert.AreEqual("owner_id = current_setting('app.user_id')::integer", policy.RawUsingExpression);
        Assert.AreEqual("status = 'active'", policy.RawWithCheckExpression);
    }

    [TestMethod]
    public void MultiplePoliciesOnOneStatementListAreEachCaptured()
    {
        var tree = new PgQueryParser().Parse(
            "CREATE TABLE t (id integer); "
            + "CREATE POLICY p1 ON t FOR SELECT USING (true); "
            + "CREATE POLICY p2 ON t FOR ALL TO PUBLIC USING (true);");

        var policies = tree.Policies.ToArray();
        Assert.HasCount(2, policies);
        CollectionAssert.AreEqual(new[] { "p1", "p2" }, policies.Select(static p => p.Name).ToArray());
    }

    private static PolicyNode Parse(string sql) => new PgQueryParser().Parse(sql).Nodes.OfType<PolicyNode>().Single();
}
