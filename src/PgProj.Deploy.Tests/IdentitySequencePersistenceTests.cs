using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-294: an identity column's sequence follows its table's persistence, and <c>UNLOGGED</c> or <c>LOGGED</c> among the identity's options sets it the other way (PostgreSQL 15 and later).
/// The word was dropped, so the sequence came back as its table's.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class IdentitySequencePersistenceTests
{
    [TestMethod]
    public void TheWordIsReadAndTheTextIsDeployedAsWritten()
    {
        var model = Build("CREATE TABLE public.t (a int GENERATED ALWAYS AS IDENTITY (UNLOGGED START WITH 5));");

        var sequence = model.Sequences.Values.Single();
        Assert.IsTrue(sequence.IsUnlogged);
        Assert.IsTrue(sequence.IdentityStatedOptions!.Contains(nameof(SequenceDefinition.IsUnlogged)));
        StringAssert.Contains(Script(DatabaseModel.Empty, model), "GENERATED ALWAYS AS IDENTITY (UNLOGGED START WITH 5)");
    }

    [TestMethod]
    public void LoggedOnAnUnloggedTableIsALoggedSequenceAndNoWordFollowsTheTable()
    {
        var model = Build("""
            CREATE UNLOGGED TABLE public.t1 (a int GENERATED ALWAYS AS IDENTITY (LOGGED));
            CREATE UNLOGGED TABLE public.t2 (a int GENERATED ALWAYS AS IDENTITY);
            CREATE TABLE public.t3 (a int GENERATED ALWAYS AS IDENTITY);
            """);

        var persistence = model.Sequences.Values.OrderBy(static sequence => sequence.Name, StringComparer.Ordinal).Select(static sequence => sequence.IsUnlogged).ToArray();
        CollectionAssert.AreEqual(new[] { false, true, false }, persistence);
    }

    [TestMethod]
    public void AChangeIsComparedOnlyWhenTheColumnStatesOne()
    {
        var logged = Build("CREATE TABLE public.t (a int GENERATED ALWAYS AS IDENTITY);");
        var unlogged = Build("CREATE TABLE public.t (a int GENERATED ALWAYS AS IDENTITY (UNLOGGED));");
        var unloggedTable = Build("CREATE UNLOGGED TABLE public.t (a int GENERATED ALWAYS AS IDENTITY);");

        Assert.AreEqual(0, ChangeCount(new DiffEngine().Diff(unlogged, unlogged)));
        Assert.IsGreaterThan(0, ChangeCount(new DiffEngine().Diff(logged, unlogged)), "stating UNLOGGED on a logged table is a change");
        // Not stated: the sequence follows its table, as before, so it is not compared.
        var unstated = new DiffEngine().Diff(logged, unloggedTable);
        Assert.IsFalse(unstated.Modified.Any(static change => change.NewNode is SequenceDefinition), "an unstated persistence is not compared on the sequence");
    }

    [TestMethod]
    public void AChangedPersistenceIsAnAlterSequenceInPlace()
    {
        var logged = Build("CREATE TABLE public.t (a int GENERATED ALWAYS AS IDENTITY (LOGGED));");
        var unlogged = Build("CREATE TABLE public.t (a int GENERATED ALWAYS AS IDENTITY (UNLOGGED));");

        var script = Script(logged, unlogged);

        StringAssert.Contains(script, "ALTER SEQUENCE \"public\".\"t_a_seq\" SET UNLOGGED;");
        Assert.DoesNotContain("DROP", script);
    }

    private static int ChangeCount(ChangeSet diff) => diff.Added.Count + diff.Removed.Count + diff.Modified.Count;

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);

    private static string Script(DatabaseModel live, DatabaseModel project)
    {
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(live, project), new DependencyAnalyzer().Analyze(live, project));
        return string.Join("\n", batch.Operations.Where(static operation => operation is not SchemaCreateOperation).Select(static operation => operation.GenerateSql().Replace("\r\n", "\n", StringComparison.Ordinal))).Trim();
    }
}
