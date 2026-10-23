namespace PgProj.Parser.Tests;

/// <summary>G-290e: psql's <c>:"name"</c> and <c>:'name'</c> in a tenant's scripts, substituted on PostgreSQL's tokens only, as psql substitutes them.</summary>
[TestClass]
public sealed class ScriptVariablesTests
{
    private static readonly Dictionary<string, string> Values = new(StringComparer.Ordinal) { ["tenant_schema"] = "tenant_acme", ["tenant_id"] = "acme" };

    [TestMethod]
    [DataRow("SELECT * FROM :\"tenant_schema\".orders;", "SELECT * FROM \"tenant_acme\".orders;")]
    [DataRow("INSERT INTO log VALUES (:'tenant_id', :'tenant_schema');", "INSERT INTO log VALUES ('acme', 'tenant_acme');")]
    [DataRow("SELECT NULL:::\"tenant_schema\".order_status;", "SELECT NULL::\"tenant_acme\".order_status;")]
    [DataRow("SELECT ':\"tenant_schema\"' AS text, $$ :'tenant_id' $$ AS body; -- :'tenant_id'", "SELECT ':\"tenant_schema\"' AS text, $$ :'tenant_id' $$ AS body; -- :'tenant_id'")]
    [DataRow("SELECT a[1:2] FROM t;", "SELECT a[1:2] FROM t;")]
    public void VariablesAreSubstitutedWhereSqlHasThemAndNowhereElse(string sql, string expected)
        => Assert.AreEqual(expected, ScriptVariables.Substitute(sql, Values).Text);

    [TestMethod]
    public void AValueIsQuotedSoItCannotEndTheNameOrString()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["tenant_schema"] = "tenant_O\"Neil", ["tenant_id"] = "O'Neil" };

        Assert.AreEqual("SELECT * FROM \"tenant_O\"\"Neil\".t WHERE id = 'O''Neil';", ScriptVariables.Substitute("SELECT * FROM :\"tenant_schema\".t WHERE id = :'tenant_id';", values).Text);
    }

    [TestMethod]
    public void AnUnknownVariableIsReportedAndLeftAsWritten()
    {
        var result = ScriptVariables.Substitute("SELECT :'tenant_name', :'tenant_id';", Values);

        CollectionAssert.AreEqual(new[] { "tenant_name" }, result.Unknown.ToArray());
        Assert.AreEqual("SELECT :'tenant_name', 'acme';", result.Text);
        Assert.IsTrue(result.UsesAny);
    }

    [TestMethod]
    public void ScriptsWithoutVariablesAreUnchanged()
    {
        var result = ScriptVariables.Substitute("UPDATE t SET a = 1 WHERE b::text = 'x';", Values);

        Assert.AreEqual("UPDATE t SET a = 1 WHERE b::text = 'x';", result.Text);
        Assert.IsFalse(result.UsesAny);
    }
}
