using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-24b: a table's <c>UNLOGGED</c> keyword, <c>WITH (...)</c> storage parameters, and <c>TABLESPACE</c> are deployed, both on
/// create and as an in-place <c>ALTER TABLE</c> when changed.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class TableStorageDeployTests
{
    [TestMethod]
    public void ANewUnloggedTableWithOptionsAndATablespaceIsCreatedWithThem()
    {
        var statements = Statements(DatabaseModel.Empty, Build("CREATE UNLOGGED TABLE public.t (a integer) WITH (fillfactor=70, autovacuum_enabled=false) TABLESPACE fastspace;"));

        var create = statements.Single(static s => s.StartsWith("CREATE", StringComparison.Ordinal));
        StringAssert.Contains(create, "CREATE UNLOGGED TABLE \"public\".\"t\"");
        StringAssert.Contains(create, "WITH (fillfactor=70, autovacuum_enabled=false)");
        StringAssert.Contains(create, "TABLESPACE \"fastspace\"");
    }

    [TestMethod]
    public void AnOrdinaryTableHasNoStorageClauses()
    {
        var statements = Statements(DatabaseModel.Empty, Build("CREATE TABLE public.t (a integer);"));

        var create = statements.Single(static s => s.StartsWith("CREATE", StringComparison.Ordinal));
        Assert.IsFalse(create.Contains("UNLOGGED", StringComparison.Ordinal), create);
        Assert.IsFalse(create.Contains("WITH (", StringComparison.Ordinal), create);
        Assert.IsFalse(create.Contains("TABLESPACE", StringComparison.Ordinal), create);
    }

    [TestMethod]
    public void BecomingUnloggedIsSetUnlogged()
    {
        var statements = Statements(Build("CREATE TABLE public.t (a integer);"), Build("CREATE UNLOGGED TABLE public.t (a integer);"));

        Assert.AreEqual("ALTER TABLE \"public\".\"t\" SET UNLOGGED;", string.Join("\n", statements));
    }

    [TestMethod]
    public void BecomingLoggedIsSetLogged()
    {
        var statements = Statements(Build("CREATE UNLOGGED TABLE public.t (a integer);"), Build("CREATE TABLE public.t (a integer);"));

        Assert.AreEqual("ALTER TABLE \"public\".\"t\" SET LOGGED;", string.Join("\n", statements));
    }

    [TestMethod]
    public void AChangedTablespaceIsSetTablespace()
    {
        var statements = Statements(
            Build("CREATE TABLE public.t (a integer) TABLESPACE fastspace;"),
            Build("CREATE TABLE public.t (a integer) TABLESPACE slowspace;"));

        Assert.AreEqual("ALTER TABLE \"public\".\"t\" SET TABLESPACE \"slowspace\";", string.Join("\n", statements));
    }

    [TestMethod]
    public void ARemovedTablespaceBlocksExecutionInsteadOfGuessing()
    {
        var oldModel = Build("CREATE TABLE public.t (a integer) TABLESPACE fastspace;");
        var newModel = Build("CREATE TABLE public.t (a integer);");
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));

        var script = string.Join("\n", batch.Operations.Select(static operation => operation.GenerateSql()));
        StringAssert.Contains(script, "-- TODO");
        StringAssert.Contains(script, "TABLESPACE");
        Assert.IsNotEmpty(batch.GetUnsupportedOperations().ToArray());
    }

    [TestMethod]
    public void AnAddedOptionIsSet()
    {
        var statements = Statements(Build("CREATE TABLE public.t (a integer);"), Build("CREATE TABLE public.t (a integer) WITH (fillfactor=70);"));

        Assert.AreEqual("ALTER TABLE \"public\".\"t\" SET (fillfactor=70);", string.Join("\n", statements));
    }

    [TestMethod]
    public void AChangedOptionIsSetWithItsNewValue()
    {
        var statements = Statements(Build("CREATE TABLE public.t (a integer) WITH (fillfactor=70);"), Build("CREATE TABLE public.t (a integer) WITH (fillfactor=80);"));

        Assert.AreEqual("ALTER TABLE \"public\".\"t\" SET (fillfactor=80);", string.Join("\n", statements));
    }

    [TestMethod]
    public void ARemovedOptionIsReset()
    {
        var statements = Statements(Build("CREATE TABLE public.t (a integer) WITH (fillfactor=70);"), Build("CREATE TABLE public.t (a integer);"));

        Assert.AreEqual("ALTER TABLE \"public\".\"t\" RESET (fillfactor);", string.Join("\n", statements));
    }

    [TestMethod]
    public void AddedChangedAndRemovedOptionsEachGetTheirOwnStatement()
    {
        var statements = Statements(
            Build("CREATE TABLE public.t (a integer) WITH (fillfactor=70, autovacuum_enabled=false);"),
            Build("CREATE TABLE public.t (a integer) WITH (fillfactor=80, parallel_workers=4);"));

        var script = string.Join("\n", statements);
        StringAssert.Contains(script, "SET (fillfactor=80, parallel_workers=4);");
        StringAssert.Contains(script, "RESET (autovacuum_enabled);");
    }

    [TestMethod]
    public void AStorageOptionWrittenInADifferentOrderDeploysNothing()
    {
        Assert.IsEmpty(Statements(
            Build("CREATE TABLE public.t (a integer) WITH (fillfactor=70, autovacuum_enabled=false);"),
            Build("CREATE TABLE public.t (a integer) WITH (autovacuum_enabled=false, fillfactor=70);")));
    }

    private static List<string> Statements(DatabaseModel oldModel, DatabaseModel newModel)
    {
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));
        return batch.Operations
            .Where(static operation => operation is not SchemaCreateOperation)
            .Select(static operation => operation.GenerateSql().Trim().Replace("\r\n", "\n", StringComparison.Ordinal))
            .ToList();
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
