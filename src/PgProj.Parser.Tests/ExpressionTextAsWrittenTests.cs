using System.Text.Json;

namespace PgProj.Parser.Tests;

/// <summary>
/// G-207 step 2: an expression the parser used to rebuild from its tree is now the author's own text, which is what is deployed. These are
/// the expressions behind G-202 (a routine parameter's default), G-208 (an exclusion constraint's expression element), G-209 (an extended
/// statistics expression), G-210 (a publication's row filter), and G-197 (a routine's <c>SET</c> clause). When the text cannot be read,
/// the rebuilt text is used and a warning says so.
/// </summary>
[TestClass]
public sealed class ExpressionTextAsWrittenTests
{
    [TestMethod]
    [DataRow("'{a,b}'::text[]")]
    [DataRow("ARRAY[1,2]")]
    [DataRow("CASE WHEN true THEN 1 ELSE 2 END")]
    [DataRow("current_user")]
    [DataRow("'{}'")]
    [DataRow("interval '1 day'")]
    [DataRow("1.50::numeric(5,2)")]
    public void ARoutineParameterDefaultIsAsWritten(string expression)
    {
        var tree = new PgQueryParser().Parse($"CREATE FUNCTION f(a int DEFAULT 1, b text DEFAULT {expression}) RETURNS int LANGUAGE sql AS $$ SELECT 1 $$;");

        Assert.AreEqual(expression, tree.Functions.Single().Signature!.Parameters[1].DefaultExpression);
        Assert.IsEmpty(tree.Diagnostics);
    }

    [TestMethod]
    public void AnExclusionConstraintsExpressionElementIsAsWrittenWithItsTree()
    {
        var tree = new PgQueryParser().Parse("CREATE TABLE t (a int, r int4range, EXCLUDE USING gist ((CASE WHEN a > 0 THEN 1 ELSE 2 END) WITH =, r WITH &&));");

        var elements = tree.Nodes.OfType<ConstraintNode>().Single().ExclusionElements;
        Assert.AreEqual("CASE WHEN a > 0 THEN 1 ELSE 2 END", elements[0].RawExpression);
        Assert.IsNotNull(elements[0].ExpressionAst);
        Assert.AreEqual("r", elements[1].Name);
        Assert.IsNull(elements[1].ExpressionAst);
    }

    [TestMethod]
    public void AStatisticsExpressionIsAsWritten()
    {
        var tree = new PgQueryParser().Parse("CREATE STATISTICS s ON (CASE WHEN a > 0 THEN 1 ELSE 2 END), b FROM t;");

        var elements = tree.Nodes.OfType<StatisticsNode>().Single().Elements;
        Assert.AreEqual("CASE WHEN a > 0 THEN 1 ELSE 2 END", elements[0].RawExpression);
        Assert.AreEqual("b", elements[1].Name);
    }

    [TestMethod]
    public void APublicationRowFilterIsAsWritten()
    {
        var tree = new PgQueryParser().Parse("CREATE PUBLICATION p FOR TABLE t WHERE (CASE WHEN a > 0 THEN true ELSE false END AND b <> 'x');");

        Assert.AreEqual("CASE WHEN a > 0 THEN true ELSE false END AND b <> 'x'", tree.Nodes.OfType<PublicationNode>().Single().Tables.Single().RawFilter);
    }

    [TestMethod]
    [DataRow("set search_path = pg_catalog, public")]
    [DataRow("SET search_path TO 'pg_catalog', 'public'")]
    [DataRow("set work_mem = '64MB'")]
    [DataRow("set extra_float_digits = -1")]
    [DataRow("SET myapp.tenant FROM CURRENT")]
    public void ASettingIsAsWritten(string clause)
    {
        var setting = new PgQueryParser().Parse($"CREATE FUNCTION f() RETURNS int LANGUAGE sql {clause} AS $$ SELECT 1 $$;").Functions.Single().Signature!.Settings.Single();

        Assert.AreEqual(clause, setting.WrittenText);
        Assert.AreEqual(clause, setting.Render());
    }

    [TestMethod]
    public void TextThatCannotBeReadIsRebuiltWithAWarningNamingWhereItIs()
    {
        const string sql = "CREATE TABLE t (a int,\n  c int DEFAULT a + 1);";
        using var document = JsonDocument.Parse(new PgQueryParser().Parse(sql).RawJsonAst!);
        var expression = document.RootElement.GetProperty("stmts")[0].GetProperty("stmt").GetProperty("CreateStmt").GetProperty("tableElts")[1]
            .GetProperty("ColumnDef").GetProperty("constraints")[0].GetProperty("Constraint").GetProperty("raw_expr");
        var diagnostics = new List<SqlDiagnostic>();

        // The same positions in a different text: the author's text cannot be found, so the tree is rebuilt.
        var text = PgQueryParser.ReadExpressionText("CREATE TABLE t (a int,\n  c int DEFAULT a - 9);", 0, expression, sql, diagnostics);

        Assert.AreEqual("a + 1", text);
        var warning = diagnostics.Single();
        Assert.AreEqual(SqlDiagnosticSeverity.Warning, warning.Severity);
        StringAssert.Contains(warning.Message, "could not be read as written");
        StringAssert.EndsWith(warning.Message, ": a + 1");
        Assert.AreEqual(2, warning.LineNumber);
        Assert.AreEqual(17, warning.Column);

        // G-308: the tree gives where an expression starts and not where it ends, so the warning runs to the end of its statement
        // (the closing parenthesis; the `;` is not part of the statement).
        Assert.AreEqual(2, warning.EndLine);
        Assert.AreEqual(23, warning.EndColumn);
    }
}
