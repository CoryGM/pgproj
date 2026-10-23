using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-218: a column whose type belongs to an extension the project moves to another schema is the same column; only its spelling changes. It was reported
/// as a retype, which <c>publish --execute</c> refuses without <c>--allow-destructive</c>.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class MovedExtensionTypeDiffTests
{
    private const string Live = "CREATE EXTENSION citext SCHEMA public; CREATE TABLE public.t (a citext, b citext[], c public.citext);";

    [TestMethod]
    public void AColumnOfAMovedExtensionsTypeIsNoChange()
    {
        var changes = new DiffEngine().Diff(Build(Live), Build("CREATE SCHEMA other; CREATE EXTENSION citext SCHEMA other; CREATE TABLE public.t (a other.citext, b other.citext[], c other.citext);"));

        Assert.IsFalse(changes.Modified.Any(static change => change.NodeId.StartsWith("public.t", StringComparison.Ordinal)), Describe(changes));
        Assert.AreEqual("extension:citext", changes.Modified.Single().NodeId);
    }

    [TestMethod]
    [DataRow("CREATE EXTENSION citext SCHEMA public; CREATE TABLE public.t (a other.citext, b citext[], c public.citext);")]
    [DataRow("CREATE SCHEMA other; CREATE EXTENSION citext SCHEMA other; CREATE TABLE public.t (a text, b other.citext[], c other.citext);")]
    public void AnythingElseIsStillAChange(string project)
    {
        // The extension stays where it is, or the column really is retyped.
        var changes = new DiffEngine().Diff(Build(Live), Build(project));

        Assert.IsTrue(changes.Modified.Any(static change => change.NodeId == "public.t.a"), Describe(changes));
    }

    [TestMethod]
    public void AProjectTypeOfTheSameNameIsNotTakenForTheExtensions()
    {
        var changes = new DiffEngine().Diff(
            Build(Live),
            Build("CREATE SCHEMA other; CREATE TYPE other.citext AS (x int); CREATE EXTENSION citext SCHEMA other; CREATE TABLE public.t (a other.citext, b other.citext[], c other.citext);"));

        Assert.IsTrue(changes.Modified.Any(static change => change.NodeId == "public.t.a"), Describe(changes));
    }

    private static string Describe(ChangeSet changes)
        => string.Join("; ", changes.Modified.Select(static change => change.NodeId + " [" + string.Join(", ", change.Details.Select(static detail => $"{detail.PropertyName}: {detail.OldValue} -> {detail.NewValue}")) + "]"));

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
