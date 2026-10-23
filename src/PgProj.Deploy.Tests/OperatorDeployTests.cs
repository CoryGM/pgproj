using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-129: an operator is created after the function it runs and the types it takes, and dropped before them; it is written with every option; a change is a
/// drop and a create; and one that only disappears is a destructive change.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class OperatorDeployTests
{
    private const string Setup = "CREATE TYPE public.mood AS ENUM ('a', 'b'); "
        + "CREATE FUNCTION public.mood_eq(public.mood, public.mood) RETURNS boolean LANGUAGE sql AS $$ SELECT $1::text = $2::text $$; ";

    private const string Operator = "CREATE OPERATOR public.=== (LEFTARG = public.mood, RIGHTARG = public.mood, FUNCTION = public.mood_eq);";

    [TestMethod]
    public void AnOperatorIsCreatedAfterItsFunctionAndItsType()
    {
        var statements = Statements(DatabaseModel.Empty, Build(Setup + Operator));

        var typeAt = statements.FindIndex(static s => s.StartsWith("CREATE TYPE", StringComparison.Ordinal));
        var functionAt = statements.FindIndex(static s => s.Contains("FUNCTION \"public\".\"mood_eq\"", StringComparison.Ordinal));
        var operatorAt = statements.FindIndex(static s => s.StartsWith("CREATE OPERATOR", StringComparison.Ordinal));
        Assert.IsTrue(typeAt >= 0 && functionAt >= 0 && operatorAt > typeAt && operatorAt > functionAt, string.Join("\n---\n", statements));
        Assert.AreEqual("CREATE OPERATOR \"public\".=== (\n    LEFTARG = public.mood,\n    RIGHTARG = public.mood,\n    FUNCTION = \"public\".\"mood_eq\"\n);", statements[operatorAt]);
    }

    [TestMethod]
    public void EveryOptionIsWritten()
    {
        var statements = Statements(
            DatabaseModel.Empty,
            Build(Setup + "CREATE OPERATOR app.<<< (LEFTARG = integer, RIGHTARG = public.mood, FUNCTION = public.mood_eq, COMMUTATOR = OPERATOR(app.>>>), NEGATOR = <>, RESTRICT = scalarltsel, JOIN = scalarltjoinsel, HASHES, MERGES);"));

        var sql = statements.Single(static s => s.StartsWith("CREATE OPERATOR", StringComparison.Ordinal));
        StringAssert.Contains(sql, "CREATE OPERATOR \"app\".<<<");
        StringAssert.Contains(sql, "LEFTARG = integer");
        StringAssert.Contains(sql, "COMMUTATOR = OPERATOR(\"app\".>>>)");
        StringAssert.Contains(sql, "NEGATOR = OPERATOR(<>)");
        StringAssert.Contains(sql, "RESTRICT = \"scalarltsel\"");
        StringAssert.Contains(sql, "JOIN = \"scalarltjoinsel\"");
        StringAssert.Contains(sql, "HASHES");
        StringAssert.Contains(sql, "MERGES");
    }

    [TestMethod]
    public void APrefixOperatorHasNoLeftOperand()
    {
        var statements = Statements(DatabaseModel.Empty, Build("CREATE OPERATOR public.@@@ (RIGHTARG = text, FUNCTION = upper);"));

        var sql = statements.Single(static s => s.StartsWith("CREATE OPERATOR", StringComparison.Ordinal));
        Assert.IsFalse(sql.Contains("LEFTARG", StringComparison.Ordinal));
        StringAssert.Contains(sql, "RIGHTARG = text");
    }

    [TestMethod]
    public void AChangedOperatorIsDroppedAndCreatedAgainAndIsNotDestructive()
    {
        var oldModel = Build(Setup + Operator);
        var newModel = Build(Setup + "CREATE OPERATOR public.=== (LEFTARG = public.mood, RIGHTARG = public.mood, FUNCTION = public.mood_eq, HASHES);");
        var batch = Generate(oldModel, newModel);

        var script = string.Join("\n", Sql(batch));
        StringAssert.Contains(script, "DROP OPERATOR \"public\".=== (public.mood, public.mood);");
        StringAssert.Contains(script, "HASHES");
        Assert.IsEmpty(batch.GetDestructiveOperations());
    }

    [TestMethod]
    public void ARemovedOperatorIsDroppedBeforeItsFunctionAndIsDestructive()
    {
        var oldModel = Build(Setup + Operator);
        var newModel = Build("CREATE TABLE public.keep (a integer);");
        var batch = Generate(oldModel, newModel);

        var statements = Sql(batch);
        var operatorAt = statements.FindIndex(static s => s.StartsWith("DROP OPERATOR", StringComparison.Ordinal));
        var functionAt = statements.FindIndex(static s => s.StartsWith("DROP FUNCTION", StringComparison.Ordinal));
        Assert.IsTrue(operatorAt >= 0 && functionAt > operatorAt, string.Join("\n---\n", statements));
        StringAssert.Contains(batch.GetDestructiveOperations().Single().Reasons.Single(), "operator dropped");
    }

    [TestMethod]
    public void AMissingOperandIsDroppedAsNone()
    {
        var oldModel = Build("CREATE OPERATOR public.@@@ (RIGHTARG = text, FUNCTION = upper);");
        var batch = Generate(oldModel, DatabaseModel.Empty);

        StringAssert.Contains(string.Join("\n", Sql(batch)), "DROP OPERATOR \"public\".@@@ (NONE, text);");
    }

    [TestMethod]
    public void TheSameOperatorWrittenTwoWaysDeploysNothing()
    {
        Assert.IsEmpty(Statements(
            Build(Setup + "CREATE OPERATOR public.=== (LEFTARG = public.mood, RIGHTARG = public.mood, FUNCTION = public.mood_eq, RESTRICT = eqsel);"),
            Build(Setup + "CREATE OPERATOR === (LEFTARG = mood, RIGHTARG = mood, PROCEDURE = mood_eq, RESTRICT = pg_catalog.eqsel);")));
    }

    private static OperationBatch Generate(DatabaseModel oldModel, DatabaseModel newModel)
        => new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));

    private static List<string> Sql(OperationBatch batch)
        => batch.Operations
            .Where(static operation => operation is not SchemaCreateOperation)
            .Select(static operation => operation.GenerateSql().Trim().Replace("\r\n", "\n", StringComparison.Ordinal))
            .ToList();

    private static List<string> Statements(DatabaseModel oldModel, DatabaseModel newModel) => Sql(Generate(oldModel, newModel));

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
