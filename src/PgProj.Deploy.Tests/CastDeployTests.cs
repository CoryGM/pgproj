using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-131: a cast is created after the function it runs and the types it converts, and dropped before them; it is written in each of its three
/// forms; a change is a drop and a create; and one that only disappears is a destructive change.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class CastDeployTests
{
    private const string Setup = "CREATE TYPE public.mood AS ENUM ('a', 'b'); "
        + "CREATE FUNCTION public.t2m(text) RETURNS public.mood LANGUAGE sql AS $$ SELECT $1::public.mood $$; ";

    [TestMethod]
    public void ACastWithAFunctionIsCreatedAfterTheFunctionAndTheType()
    {
        var statements = Statements(DatabaseModel.Empty, Build(Setup + "CREATE CAST (text AS public.mood) WITH FUNCTION public.t2m(text) AS ASSIGNMENT;"));

        var typeAt = statements.FindIndex(static s => s.StartsWith("CREATE TYPE", StringComparison.Ordinal));
        var functionAt = statements.FindIndex(static s => s.Contains("FUNCTION \"public\".\"t2m\"", StringComparison.Ordinal));
        var castAt = statements.FindIndex(static s => s.StartsWith("CREATE CAST", StringComparison.Ordinal));
        Assert.IsTrue(typeAt >= 0 && functionAt >= 0 && castAt > typeAt && castAt > functionAt, string.Join("\n---\n", statements));
        Assert.AreEqual("CREATE CAST (text AS public.mood)\n    WITH FUNCTION \"public\".\"t2m\"(text) AS ASSIGNMENT;", statements[castAt]);
    }

    [TestMethod]
    [DataRow("WITHOUT FUNCTION AS IMPLICIT", "WITHOUT FUNCTION AS IMPLICIT;")]
    [DataRow("WITH INOUT", "WITH INOUT;")]
    [DataRow("WITH INOUT AS ASSIGNMENT", "WITH INOUT AS ASSIGNMENT;")]
    public void TheOtherMethodsAreWritten(string clause, string expectedEnding)
    {
        var statements = Statements(DatabaseModel.Empty, Build("CREATE TYPE public.a AS ENUM ('x'); CREATE TYPE public.b AS ENUM ('x'); CREATE CAST (public.a AS public.b) " + clause + ";"));

        var cast = statements.Single(static s => s.StartsWith("CREATE CAST", StringComparison.Ordinal));
        StringAssert.EndsWith(cast, expectedEnding);
        Assert.IsFalse(cast.Contains("WITH FUNCTION", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ArrayTypesAndBuiltInSpellingsAreWrittenAsAColumnsTypeIs()
    {
        var statements = Statements(
            DatabaseModel.Empty,
            Build("CREATE FUNCTION public.f(integer[]) RETURNS text LANGUAGE sql AS $$ SELECT 'x' $$; CREATE CAST (integer[] AS text) WITH FUNCTION public.f(integer[]) AS IMPLICIT;"));

        var cast = statements.Single(static s => s.StartsWith("CREATE CAST", StringComparison.Ordinal));
        StringAssert.Contains(cast, "(integer[] AS text)");
        StringAssert.Contains(cast, "\"public\".\"f\"(integer[])");
    }

    [TestMethod]
    public void AChangedCastIsDroppedAndCreatedAgainAndIsNotDestructive()
    {
        var oldModel = Build(Setup + "CREATE CAST (text AS public.mood) WITH FUNCTION public.t2m(text);");
        var newModel = Build(Setup + "CREATE CAST (text AS public.mood) WITH FUNCTION public.t2m(text) AS IMPLICIT;");
        var batch = Generate(oldModel, newModel);

        var script = string.Join("\n", Sql(batch));
        StringAssert.Contains(script, "DROP CAST (text AS public.mood);");
        StringAssert.Contains(script, "AS IMPLICIT;");
        Assert.IsEmpty(batch.GetDestructiveOperations());
    }

    [TestMethod]
    public void ARemovedCastIsDroppedBeforeItsFunctionAndIsDestructive()
    {
        var oldModel = Build(Setup + "CREATE CAST (text AS public.mood) WITH FUNCTION public.t2m(text);");
        var newModel = Build("CREATE TABLE public.keep (a integer);");
        var batch = Generate(oldModel, newModel);

        var statements = Sql(batch);
        var castAt = statements.FindIndex(static s => s.StartsWith("DROP CAST", StringComparison.Ordinal));
        var functionAt = statements.FindIndex(static s => s.StartsWith("DROP FUNCTION", StringComparison.Ordinal));
        Assert.IsTrue(castAt >= 0 && functionAt > castAt, string.Join("\n---\n", statements));
        StringAssert.Contains(batch.GetDestructiveOperations().Single().Reasons.Single(), "cast dropped");
    }

    [TestMethod]
    public void TheSameCastWrittenTwoWaysDeploysNothing()
    {
        Assert.IsEmpty(Statements(
            Build(Setup + "CREATE CAST (text AS public.mood) WITH FUNCTION public.t2m(text) AS ASSIGNMENT;"),
            Build(Setup + "CREATE CAST (pg_catalog.text AS mood) WITH FUNCTION t2m(pg_catalog.text) AS ASSIGNMENT;")));
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
