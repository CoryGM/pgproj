namespace PgProj.Parser.Tests;

[TestClass]
public sealed class ScriptedObjectParsingTests
{
    [TestMethod]
    public void CreateFunctionPreservesBodyVerbatimIncludingFormattingAndComments()
    {
        const string sql = """
            CREATE FUNCTION public.calc(a integer, b integer)
            RETURNS integer
            LANGUAGE plpgsql
            AS $$
            BEGIN
                -- deliberately    odd   spacing
                RETURN a +      b;
            END;
            $$;
            """;

        var parser = new PgQueryParser();
        var tree = parser.Parse(sql);

        Assert.IsFalse(tree.HasErrors, string.Join(Environment.NewLine, tree.Diagnostics.Select(static diagnostic => diagnostic.Message)));

        var function = tree.Functions.Single();
        Assert.AreEqual("calc", function.Name);
        Assert.AreEqual("public", function.SchemaName);
        Assert.AreEqual("$$", function.BodyDelimiter);

        StringAssert.Contains(function.RawBody, "-- deliberately    odd   spacing");
        StringAssert.Contains(function.RawBody, "RETURN a +      b;");
    }

    [TestMethod]
    public void CreateFunctionPreservesCustomDollarQuoteTag()
    {
        const string sql = """
            CREATE FUNCTION public.greet()
            RETURNS text
            LANGUAGE plpgsql
            AS $body$
            BEGIN
                RETURN 'hello $$ world';
            END;
            $body$;
            """;

        var parser = new PgQueryParser();
        var tree = parser.Parse(sql);

        var function = tree.Functions.Single();
        Assert.AreEqual("$body$", function.BodyDelimiter);
        StringAssert.Contains(function.RawBody, "hello $$ world");
    }

    [TestMethod]
    public void CreateFunctionCapturesSignatureMetadata()
    {
        const string sql = """
            CREATE FUNCTION public.calc(a integer, b integer DEFAULT 5)
            RETURNS integer
            LANGUAGE sql
            AS $$ SELECT a + b; $$;
            """;

        var parser = new PgQueryParser();
        var tree = parser.Parse(sql);

        var signature = tree.Functions.Single().Signature;
        Assert.IsNotNull(signature);
        Assert.AreEqual(2, signature!.Parameters.Count);
        Assert.AreEqual("a", signature.Parameters[0].Name);
        Assert.AreEqual("b", signature.Parameters[1].Name);
        StringAssert.Contains(signature.ReturnType, "int");
    }

    [TestMethod]
    public void CreateProcedureProducesProcedureNodeNotFunctionNode()
    {
        const string sql = """
            CREATE PROCEDURE public.do_work(job_id integer)
            LANGUAGE plpgsql
            AS $$
            BEGIN
                UPDATE public.jobs SET done = true WHERE id = job_id;
            END;
            $$;
            """;

        var parser = new PgQueryParser();
        var tree = parser.Parse(sql);

        Assert.IsFalse(tree.HasErrors, string.Join(Environment.NewLine, tree.Diagnostics.Select(static diagnostic => diagnostic.Message)));

        Assert.AreEqual(0, tree.Functions.Count());

        var procedure = tree.Procedures.Single();
        Assert.AreEqual("do_work", procedure.Name);
        Assert.AreEqual("public", procedure.SchemaName);
        StringAssert.Contains(procedure.RawBody, "UPDATE public.jobs SET done = true WHERE id = job_id;");
    }

    [TestMethod]
    public void CreateTriggerProducesTriggerNodeWithTimingAndEvents()
    {
        const string sql = """
            CREATE TRIGGER audit_orders
            AFTER INSERT OR UPDATE ON public.orders
            FOR EACH ROW
            EXECUTE FUNCTION public.log_change();
            """;

        var parser = new PgQueryParser();
        var tree = parser.Parse(sql);

        Assert.IsFalse(tree.HasErrors, string.Join(Environment.NewLine, tree.Diagnostics.Select(static diagnostic => diagnostic.Message)));

        var trigger = tree.Triggers.Single();
        Assert.AreEqual("audit_orders", trigger.Name);
        Assert.AreEqual("orders", trigger.TableName);
        Assert.AreEqual("AFTER", trigger.Timing);
        Assert.IsTrue(trigger.IsRowLevel);
        CollectionAssert.AreEquivalent(new[] { "INSERT", "UPDATE" }, trigger.Events.ToArray());
        StringAssert.Contains(trigger.RawStatement, "EXECUTE FUNCTION public.log_change()");
    }
}
