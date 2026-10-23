using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

[TestClass]
public sealed class ScriptedObjectOperationTests
{
    private const string OddlyFormattedBody = """

        BEGIN
            -- keep    this    spacing
            RETURN a +      b;
        END;
        """;

    [TestMethod]
    public void CreateOperationEmitsFunctionBodyVerbatim()
    {
        var function = CreateFunction(OddlyFormattedBody, "$$");

        var sql = new CreateOperation
        {
            NodeId = function.Key,
            NodeKind = ChangeNodeKind.Function,
            Node = function,
        }.GenerateSql();

        StringAssert.Contains(sql, OddlyFormattedBody);
        StringAssert.Contains(sql, "-- keep    this    spacing");
        StringAssert.Contains(sql, "RETURN a +      b;");
        Assert.IsFalse(sql.Contains("TODO", StringComparison.Ordinal), sql);
    }

    [TestMethod]
    public void CreateOperationRegeneratesHeaderFromSignature()
    {
        var function = CreateFunction("SELECT a + b;", "$$");

        var sql = new CreateOperation
        {
            NodeId = function.Key,
            NodeKind = ChangeNodeKind.Function,
            Node = function,
        }.GenerateSql();

        StringAssert.Contains(sql, "CREATE OR REPLACE FUNCTION \"public\".\"calc\"(a integer, b integer)");
        StringAssert.Contains(sql, "RETURNS integer");
        StringAssert.Contains(sql, "LANGUAGE plpgsql");
    }

    [TestMethod]
    public void CreateOperationChoosesNonCollidingDelimiterWhenBodyContainsTag()
    {
        var function = CreateFunction("SELECT 'literal $$ inside';", "$$");

        var sql = new CreateOperation
        {
            NodeId = function.Key,
            NodeKind = ChangeNodeKind.Function,
            Node = function,
        }.GenerateSql();

        // The original tag would terminate the body early, so a safe tag must be selected.
        StringAssert.Contains(sql, "$body1$");
        StringAssert.Contains(sql, "SELECT 'literal $$ inside';");
    }

    [TestMethod]
    public void CreateOperationEmitsProcedureBodyVerbatim()
    {
        var procedure = new ProcedureDefinition(
            "public.do_work",
            "do_work",
            "public",
            "plpgsql",
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [])
        {
            RawBody = OddlyFormattedBody,
            BodyDelimiter = "$$",
            Signature = new RoutineSignature
            {
                Parameters = [new RoutineParameter("job_id", "integer", "IN", null)],
                Language = "plpgsql",
            },
        };

        var sql = new CreateOperation
        {
            NodeId = procedure.Key,
            NodeKind = ChangeNodeKind.Procedure,
            Node = procedure,
        }.GenerateSql();

        StringAssert.Contains(sql, "CREATE OR REPLACE PROCEDURE \"public\".\"do_work\"(job_id integer)");
        StringAssert.Contains(sql, OddlyFormattedBody);
        Assert.IsFalse(sql.Contains("RETURNS", StringComparison.Ordinal), sql);
    }

    [TestMethod]
    public void CreateOperationReplaysTriggerStatementVerbatim()
    {
        const string rawStatement = "CREATE TRIGGER audit_orders\nAFTER INSERT ON public.orders\nFOR EACH ROW\nEXECUTE FUNCTION public.log_change()";

        var trigger = new TriggerDefinition("public.audit_orders", "audit_orders", "public", "orders")
        {
            RawStatement = rawStatement,
        };

        var sql = new CreateOperation
        {
            NodeId = trigger.Key,
            NodeKind = ChangeNodeKind.Trigger,
            Node = trigger,
        }.GenerateSql();

        Assert.AreEqual(rawStatement + ";", sql);
    }

    [TestMethod]
    public void ModifiedFunctionWithTheSameSignatureEmitsCreateOrReplaceOnly()
    {
        var oldFunction = CreateFunction("SELECT 1;", "$$");
        var newFunction = CreateFunction("SELECT 2;", "$$");

        var changes = new ChangeSet
        {
            Added = [],
            Removed = [],
            Modified =
            [
                new ModifiedChange
                {
                    NodeId = newFunction.Key,
                    NodeKind = ChangeNodeKind.Function,
                    OldNode = oldFunction,
                    NewNode = newFunction,
                    Details = [],
                },
            ],
        };

        var batch = new ScriptGenerator().Generate(changes, new DependencyGraph());

        // G-64: nothing about the signature changed, so the function is replaced in place; a drop fails while anything uses it.
        Assert.AreEqual(1, batch.Operations.Count);
        Assert.IsInstanceOfType<CreateOperation>(batch.Operations[0]);

        StringAssert.Contains(batch.Operations[0].GenerateSql(), "CREATE OR REPLACE FUNCTION");
        StringAssert.Contains(batch.Operations[0].GenerateSql(), "SELECT 2;");
    }

    [TestMethod]
    public void DropOperationEmitsFunctionArgumentTypesForOverloadSafety()
    {
        var function = CreateFunction("SELECT a + b;", "$$");

        var sql = new DropOperation
        {
            NodeId = function.Key,
            NodeKind = ChangeNodeKind.Function,
            Node = function,
        }.GenerateSql();

        Assert.AreEqual("DROP FUNCTION \"public\".\"calc\"(integer, integer);", sql);
    }

    [TestMethod]
    public void DropOperationEmitsProcedureArgumentTypesForOverloadSafety()
    {
        var procedure = new ProcedureDefinition(
            "public.do_work",
            "do_work",
            "public",
            "plpgsql",
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [])
        {
            Signature = new RoutineSignature
            {
                Parameters = [new RoutineParameter("job_id", "integer", "IN", null)],
                Language = "plpgsql",
            },
        };

        var sql = new DropOperation
        {
            NodeId = procedure.Key,
            NodeKind = ChangeNodeKind.Procedure,
            Node = procedure,
        }.GenerateSql();

        Assert.AreEqual("DROP PROCEDURE \"public\".\"do_work\"(integer);", sql);
    }

    private static FunctionDefinition CreateFunction(string rawBody, string delimiter)
        => new(
            "public.calc",
            "calc",
            "public",
            "plpgsql",
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [])
        {
            RawBody = rawBody,
            BodyDelimiter = delimiter,
            Signature = new RoutineSignature
            {
                Parameters =
                [
                    new RoutineParameter("a", "integer", "IN", null),
                    new RoutineParameter("b", "integer", "IN", null),
                ],
                ReturnType = "integer",
                Language = "plpgsql",
            },
        };
}
