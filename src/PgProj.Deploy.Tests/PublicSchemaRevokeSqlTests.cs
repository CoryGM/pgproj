using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-341: the <c>public</c> schema's revoked defaults are deployed as <c>REVOKE USAGE|CREATE ON SCHEMA public FROM PUBLIC</c>, and the <c>USAGE</c> one undone by
/// <c>GRANT USAGE ... TO PUBLIC</c>. The <c>public</c> schema always exists, so taking either from <c>PUBLIC</c> is destructive (the user's choice of 2026-11-10),
/// and giving <c>USAGE</c> back takes nothing away.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class PublicSchemaRevokeSqlTests
{
    private const string Table = "CREATE TABLE public.t (id integer); ";

    [TestMethod]
    [DataRow("REVOKE USAGE ON SCHEMA public FROM PUBLIC;", "REVOKE USAGE ON SCHEMA \"public\" FROM PUBLIC;")]
    [DataRow("REVOKE CREATE ON SCHEMA public FROM PUBLIC;", "REVOKE CREATE ON SCHEMA \"public\" FROM PUBLIC;")]
    public void TakingADefaultFromThePublicSchemaIsARevokeAndDestructive(string revoke, string expected)
    {
        var statements = Statements(Build(Table), Build(Table + revoke), out var batch);

        CollectionAssert.AreEqual(new[] { expected }, statements.ToArray());
        StringAssert.Contains(batch.GetDestructiveOperations().Single().Reasons.Single(), "grant revoked");
    }

    [TestMethod]
    public void RevokeAllTakesBothAndEachIsDestructive()
    {
        var statements = Statements(Build(""), Build("REVOKE ALL ON SCHEMA public FROM PUBLIC;"), out var batch);

        CollectionAssert.AreEquivalent(new[] { "REVOKE USAGE ON SCHEMA \"public\" FROM PUBLIC;", "REVOKE CREATE ON SCHEMA \"public\" FROM PUBLIC;" }, statements.ToArray());
        Assert.HasCount(2, batch.GetDestructiveOperations());
    }

    [TestMethod]
    public void GivingUsageBackIsAGrantToPublicAndTakesNothingAway()
    {
        var statements = Statements(Build(Table + "REVOKE USAGE ON SCHEMA public FROM PUBLIC;"), Build(Table), out var batch);

        CollectionAssert.AreEqual(new[] { "GRANT USAGE ON SCHEMA \"public\" TO PUBLIC;" }, statements.ToArray());
        Assert.IsEmpty(batch.GetDestructiveOperations());
    }

    [TestMethod]
    public void CreateIsNeverGivenBack()
    {
        Assert.IsEmpty(Statements(Build(Table + "REVOKE CREATE ON SCHEMA public FROM PUBLIC;"), Build(Table), out _));
    }

    [TestMethod]
    public void ARevokeOnThePublicSchemaComesAfterItsObjectsAreCreatedWithoutAnOrderOfItsOwn()
    {
        // Nothing in the public schema waits for its revoke, and the revoke waits for nothing: the schema already exists.
        var statements = Statements(Build(""), Build(Table + "REVOKE USAGE ON SCHEMA public FROM PUBLIC;"), out _);

        Assert.HasCount(2, statements);
        Assert.IsTrue(statements.Any(static statement => statement.StartsWith("CREATE TABLE \"public\".\"t\"", StringComparison.Ordinal)), string.Join("\n", statements));
        Assert.IsTrue(statements.Contains("REVOKE USAGE ON SCHEMA \"public\" FROM PUBLIC;"), string.Join("\n", statements));
    }

    private static IReadOnlyList<string> Statements(DatabaseModel oldModel, DatabaseModel newModel, out OperationBatch batch)
    {
        batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));
        return batch.Operations
            .Where(static operation => operation is not SchemaCreateOperation)
            .Select(static operation => operation.GenerateSql().Trim())
            .ToArray();
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
