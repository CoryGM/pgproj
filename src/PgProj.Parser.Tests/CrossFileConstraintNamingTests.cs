namespace PgProj.Parser.Tests;

/// <summary>
/// G-42 (cross-file): unnamed constraints avoid names used anywhere in the schema, and a warning is raised
/// whenever a numeric suffix depends on something other than an earlier generated name in the same file.
/// </summary>
[TestClass]
public sealed class CrossFileConstraintNamingTests
{
    [TestMethod]
    public void SecondFileGetsTheNumericSuffixAndAWarning()
    {
        var trees = Assign(
            "create table public.t (a integer check (a > 0));",
            "alter table public.t add check (a > 0);");

        CollectionAssert.AreEqual(new[] { "t_a_check" }, Names(trees[0]));
        CollectionAssert.AreEqual(new[] { "t_a_check1" }, Names(trees[1]));
        Assert.IsEmpty(NamingWarnings(trees[0]));
        Assert.HasCount(1, NamingWarnings(trees[1]));
        StringAssert.Contains(NamingWarnings(trees[1])[0].Message, "t_a_check1");
        StringAssert.Contains(NamingWarnings(trees[1])[0].Message, "explicit name");
        Assert.AreEqual(SqlDiagnosticSeverity.Warning, NamingWarnings(trees[1])[0].Severity);
    }

    [TestMethod]
    public void ExplicitNameInALaterFileIsReservedBeforeGeneratingNames()
    {
        var trees = Assign(
            "create table public.t (a integer check (a > 0));",
            "alter table public.t add constraint t_a_check check (a < 100);");

        CollectionAssert.AreEqual(new[] { "t_a_check1" }, Names(trees[0]));
        CollectionAssert.AreEqual(new[] { "t_a_check" }, Names(trees[1]));
        Assert.HasCount(1, NamingWarnings(trees[0]));
    }

    [TestMethod]
    public void IndexBackedConstraintsAvoidRelationNamesButChecksDoNot()
    {
        var trees = Assign(
            "create table public.t (a integer, id bigint); create index t_pkey on public.t (id); create table public.t_a_key (x integer);",
            "alter table public.t add primary key (id); alter table public.t add unique (a);",
            "create table public.t_a_check (x integer); alter table public.t add check (a > 0);");

        // Primary key and unique constraints share the relation namespace with indexes and tables.
        CollectionAssert.AreEquivalent(new[] { "t_pkey1", "t_a_key1" }, Names(trees[1]));
        // A CHECK only has to avoid other constraint names, so the table named t_a_check is no obstacle.
        CollectionAssert.AreEqual(new[] { "t_a_check" }, Names(trees[2]));
        Assert.HasCount(2, NamingWarnings(trees[1]));
        Assert.IsEmpty(NamingWarnings(trees[2]));
    }

    [TestMethod]
    public void SuffixCausedOnlyByAnEarlierGeneratedNameInTheSameFileIsNotReported()
    {
        var trees = Assign("create table public.t (a integer check (a > 0), check (a < 100), check (a <> 5));");

        CollectionAssert.AreEqual(new[] { "t_a_check", "t_a_check1", "t_a_check2" }, Names(trees[0]));
        Assert.IsEmpty(NamingWarnings(trees[0]));
    }

    [TestMethod]
    public void ExplicitNameClashInTheSameFileIsReported()
    {
        var trees = Assign("create table public.t (a integer check (a > 0), constraint t_a_check check (a < 100));");

        CollectionAssert.AreEquivalent(new[] { "t_a_check", "t_a_check1" }, Names(trees[0]));
        Assert.HasCount(1, NamingWarnings(trees[0]));
    }

    [TestMethod]
    public void DifferentSchemasDoNotClash()
    {
        var trees = Assign(
            "create table a.t (id bigint primary key);",
            "create table b.t (id bigint primary key);");

        CollectionAssert.AreEqual(new[] { "t_pkey" }, Names(trees[0]));
        CollectionAssert.AreEqual(new[] { "t_pkey" }, Names(trees[1]));
        Assert.IsEmpty(NamingWarnings(trees[1]));
    }

    [TestMethod]
    public void UnqualifiedNamesAreTreatedAsThePublicSchema()
    {
        var trees = Assign(
            "create table t (a integer check (a > 0));",
            "alter table public.t add check (a > 0);");

        CollectionAssert.AreEqual(new[] { "t_a_check1" }, Names(trees[1]));
    }

    [TestMethod]
    public void AssigningTwiceGivesTheSameResultWithoutDuplicateWarnings()
    {
        var once = Assign(
            "create table public.t (a integer check (a > 0));",
            "alter table public.t add check (a > 0);");
        var twice = ConstraintNameAssigner.Assign(once);

        CollectionAssert.AreEqual(Names(once[1]), Names(twice[1]));
        Assert.HasCount(1, NamingWarnings(twice[1]));
    }

    [TestMethod]
    public void ParserAloneLeavesUnnamedConstraintsUnnamed()
    {
        var tree = new PgQueryParser().Parse("create table public.t (a integer check (a > 0));");

        Assert.IsTrue(tree.Constraints.Where(static constraint => constraint.ConstraintType is not null).All(static constraint => constraint.Name is null));
    }

    private static IReadOnlyList<SqlSyntaxTree> Assign(params string[] files)
    {
        var parser = new PgQueryParser();
        return ConstraintNameAssigner.Assign(files.Select(parser.Parse).ToArray());
    }

    private static string[] Names(SqlSyntaxTree tree)
        => tree.Constraints.Where(static constraint => constraint.ConstraintType is not null).Select(static constraint => constraint.Name!).ToArray();

    private static IReadOnlyList<SqlDiagnostic> NamingWarnings(SqlSyntaxTree tree)
        => tree.Diagnostics.Where(static diagnostic => diagnostic.Source == ConstraintNameAssigner.DiagnosticSource).ToArray();
}
