using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-190: an enum type that loses or reorders a label has no in-place form, and PostgreSQL refuses to drop it while anything uses it. The script says so
/// (naming what uses it) and does not carry the change out, which makes <c>publish --execute</c> refuse it before anything runs. A type nothing uses, and a label
/// that is only inserted, are unchanged.
/// </summary>
[TestClass]
public sealed class EnumLabelRemovalInUseDeployTests
{
    private const string Use = " CREATE TABLE public.m (id int, cur public.mood);";

    [TestMethod]
    [DataRow("('sad', 'ok', 'happy')", "('sad', 'happy')")]
    [DataRow("('sad', 'ok', 'happy')", "('sad', 'happy', 'ok')")]
    [DataRow("('sad', 'happy')", "('sad', 'glad')")]
    public void ARemovedOrReorderedLabelOfATypeInUseIsBlockedWithAMessageNamingWhatUsesIt(string before, string after)
    {
        var batch = Generate(Build($"CREATE TYPE public.mood AS ENUM {before};{Use}"), Build($"CREATE TYPE public.mood AS ENUM {after};{Use}"));

        var blocked = batch.GetUnsupportedOperations().Single();
        StringAssert.Contains(blocked.GenerateSql(), "public.m");
        StringAssert.Contains(blocked.GenerateSql(), "mood");
        Assert.IsFalse(batch.Operations.Any(static operation => operation is DropOperation or CreateOperation));
    }

    [TestMethod]
    public void ARemovedLabelOfATypeNothingUsesIsStillDroppedAndCreated()
    {
        var batch = Generate(Build("CREATE TYPE public.mood AS ENUM ('sad', 'ok', 'happy');"), Build("CREATE TYPE public.mood AS ENUM ('sad', 'happy');"));

        Assert.IsEmpty(batch.GetUnsupportedOperations());
        Assert.IsTrue(batch.Operations.Any(static operation => operation is DropOperation));
        Assert.IsTrue(batch.Operations.Any(static operation => operation is CreateOperation));
    }

    [TestMethod]
    public void AnInsertedLabelOfATypeInUseIsAddedInPlace()
    {
        var batch = Generate(Build($"CREATE TYPE public.mood AS ENUM ('sad', 'happy');{Use}"), Build($"CREATE TYPE public.mood AS ENUM ('sad', 'ok', 'happy');{Use}"));

        Assert.IsEmpty(batch.GetUnsupportedOperations());
        Assert.AreEqual("ALTER TYPE \"public\".\"mood\" ADD VALUE 'ok' AFTER 'sad';", batch.Operations.Single().GenerateSql());
    }

    private static OperationBatch Generate(DatabaseModel oldModel, DatabaseModel newModel)
        => new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
