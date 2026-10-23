using System.CommandLine;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Cli.Tests;

/// <summary>
/// G-282: <c>diff</c> exited 0 whether or not it found differences, so a CI drift check had to read <c>Added: n</c> from the output. <c>--exit-code</c> (the user's
/// choice, option (a)) exits 2 with differences and 0 without; an error is 1; without the option nothing changes.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class DiffExitCodeTests
{
    [TestMethod]
    public void TheOptionIsAFlagAndOptional()
    {
        var root = new RootCommand("pgproj") { DiffCommand.Create(new CliContext()) };

        Assert.IsEmpty(root.Parse(["diff", "--exit-code"]).Errors);
        Assert.IsEmpty(root.Parse(["diff", "--exit-code", "--format", "json"]).Errors);
        Assert.IsEmpty(root.Parse(["diff"]).Errors);
    }

    [TestMethod]
    public void TheHelpGivesTheCodesAndAnExample()
    {
        var command = DiffCommand.Create(new CliContext());

        StringAssert.Contains(command.Description, "With --exit-code: 0 when there are no differences, 2 when there are, 1 on an error");
        StringAssert.Contains(command.Description, "pgproj diff --target-connection \"Host=localhost;Database=app\" --exit-code");
        StringAssert.Contains(command.Options.Single(static option => option.Name == "--exit-code").Description, "Exit with 2 when there are differences");
    }

    [TestMethod]
    public void AddedRemovedAndModifiedObjectsAreDifferences()
    {
        var changeSet = new DiffEngine().Diff(
            Build("CREATE TABLE public.a (id int); CREATE TABLE public.b (id int);"),
            Build("CREATE TABLE public.a (id bigint); CREATE TABLE public.c (id int);"));

        Assert.AreEqual(changeSet.Added.Count + changeSet.Removed.Count + changeSet.Modified.Count, DiffCommand.DifferenceCount(changeSet));
        Assert.IsGreaterThan(2, DiffCommand.DifferenceCount(changeSet));
        Assert.AreEqual(0, DiffCommand.DifferenceCount(new DiffEngine().Diff(Build("CREATE TABLE public.a (id int);"), Build("CREATE TABLE public.a (id int);"))));
        Assert.AreEqual(2, DiffCommand.DifferencesExitCode);
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
