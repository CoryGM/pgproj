using System.Text.Json;
using PgProj.Parser;

namespace PgProj.Parser.Tests;

/// <summary>
/// G-303a: the SQL a PL/pgSQL body runs is read from libpg_query's parse of the body, each piece parsed again as SQL. libpg_query cannot write out a body with RETURN QUERY or RETURN NEXT
/// (the JSON stops), and writes a trigger function's special variables as unbalanced braces; both are handled, and a body that still cannot be read has no statements.
/// </summary>
[TestClass]
public sealed class PlpgsqlBodyReaderTests
{
    private static string[] Relations(string header, string body, string returns = "int")
    {
        var sql = $"CREATE FUNCTION f({header}) RETURNS {returns} LANGUAGE plpgsql AS $$ {body} $$;";
        var asts = PlpgsqlBodyReader.Read(sql, " " + body + " ");
        Assert.IsNotNull(asts, "the body should be readable");
        return [.. asts.SelectMany(static ast => Names(ast, "relname")).Distinct().OrderBy(static name => name, StringComparer.Ordinal)];
    }

    private static IEnumerable<string> Names(string json, string property)
    {
        using var document = JsonDocument.Parse(json);
        var found = new List<string>();
        void Walk(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                foreach (var member in node.EnumerateObject())
                {
                    if (member.Name == property && member.Value.ValueKind == JsonValueKind.String)
                    {
                        found.Add(member.Value.GetString()!);
                    }

                    Walk(member.Value);
                }
            }
            else if (node.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in node.EnumerateArray())
                {
                    Walk(item);
                }
            }
        }

        Walk(document.RootElement);
        return found;
    }

    [TestMethod]
    public void EveryKindOfStatementThatRunsSqlIsRead()
        => CollectionAssert.AreEqual(
            new[] { "audit", "customers", "orders" },
            Relations(
                "p_id int",
                "DECLARE v int; r record; BEGIN SELECT total INTO v FROM orders WHERE id = p_id; FOR r IN SELECT id FROM customers LOOP PERFORM g(r.id); END LOOP; INSERT INTO audit(msg) VALUES ('x'); RETURN v; END"));

    [TestMethod]
    public void AnExpressionIsReadAsOneAndAnAssignmentsRightHandSideToo()
    {
        var sql = "CREATE FUNCTION f() RETURNS int LANGUAGE plpgsql AS $$ DECLARE v int; BEGIN v := helper(2); IF other(v) > 1 THEN RETURN 1; END IF; RETURN 0; END $$;";

        var asts = PlpgsqlBodyReader.Read(sql, null)!;

        var calls = asts.SelectMany(static ast => Names(ast, "sval")).ToArray();
        CollectionAssert.Contains(calls, "helper");
        CollectionAssert.Contains(calls, "other");
    }

    [TestMethod]
    public void ReturnQueryAndReturnNextAreRewrittenSoTheBodyCanBeRead()
    {
        CollectionAssert.AreEqual(new[] { "nowhere" }, Relations(string.Empty, "BEGIN RETURN QUERY SELECT id FROM nowhere; END", "SETOF int"));
        CollectionAssert.AreEqual(new[] { "t" }, Relations(string.Empty, "DECLARE r record; BEGIN FOR r IN SELECT * FROM t LOOP RETURN NEXT r; END LOOP; RETURN; END", "SETOF record"));
        CollectionAssert.AreEqual(new[] { "one", "two" }, Relations(string.Empty, "BEGIN RETURN QUERY SELECT 1 FROM one; RETURN QUERY EXECUTE 'select 1'; RETURN QUERY SELECT 2 FROM two; END", "SETOF int"));
    }

    [TestMethod]
    public void TheRewriteChangesOnlyTheTwoStatementsAndOnlyOutsideStringsAndComments()
    {
        Assert.AreEqual("BEGIN " + new string(' ', 12) + " SELECT 1; perform x; perform 1; END", PlpgsqlBodyReader.Rewrite("BEGIN RETURN QUERY SELECT 1; RETURN NEXT x; RETURN NEXT; END"));
        Assert.IsNull(PlpgsqlBodyReader.Rewrite("BEGIN RETURN 1; END"));
        Assert.IsNull(PlpgsqlBodyReader.Rewrite("BEGIN RAISE NOTICE 'return query'; -- return next\n RETURN 1; END"));
    }

    [TestMethod]
    public void ATriggerFunctionIsReadAlthoughLibpgQueryWritesBrokenJsonForItsVariables()
        => CollectionAssert.AreEqual(
            new[] { "edit" },
            Relations(string.Empty, "BEGIN NEW.status = (SELECT status FROM edit WHERE id = NEW.edit); RETURN NEW; END", "trigger"));

    [TestMethod]
    public void ADynamicStatementsTextIsNotReadAsSql()
        => CollectionAssert.AreEqual(Array.Empty<string>(), Relations(string.Empty, "BEGIN EXECUTE 'select * from ' || 'dyn_table'; RETURN 1; END"));

    [TestMethod]
    public void ABodyThatCannotBeParsedHasNoStatements()
    {
        Assert.IsNull(PlpgsqlBodyReader.Read("CREATE FUNCTION f() RETURNS int LANGUAGE plpgsql AS $$ BEGIN RETURN ((; END $$;", null));
        Assert.IsNull(PlpgsqlBodyReader.Read(string.Empty, null));
    }

    [TestMethod]
    public void TheParserKeepsThemOnAPlpgsqlRoutineAndOnNoOther()
    {
        var tree = new PgQueryParser().Parse(
            "CREATE FUNCTION a() RETURNS int LANGUAGE plpgsql AS $$ BEGIN RETURN (SELECT count(*) FROM t); END $$; " +
            "CREATE FUNCTION b() RETURNS int LANGUAGE sql AS 'SELECT 1'; " +
            "CREATE PROCEDURE c() LANGUAGE plpgsql AS $$ BEGIN INSERT INTO u VALUES (1); END $$;");

        var functions = tree.Nodes.OfType<FunctionNode>().ToDictionary(static function => function.Name!);
        Assert.IsNotEmpty(functions["a"].PlpgsqlStatementAsts);
        Assert.IsEmpty(functions["b"].PlpgsqlStatementAsts);
        Assert.IsNotEmpty(tree.Nodes.OfType<ProcedureNode>().Single().PlpgsqlStatementAsts);
    }
}
