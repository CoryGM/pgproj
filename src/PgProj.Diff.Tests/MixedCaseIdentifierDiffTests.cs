using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-70: names now keep their spelling in the model, so the diff has to keep treating two spellings of one name as one object (identity
/// is case-insensitive and unchanged) while still seeing every real difference in a mixed-case object. Both directions are tested
/// (<c>copilot-diff.md</c>): the same schema written or read back differently reports nothing, and a real change is reported.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class MixedCaseIdentifierDiffTests
{
    private const string Project = """
        CREATE TYPE "MySchema"."Mood" AS ENUM ('sad', 'ok');
        CREATE TABLE "MySchema"."Parent" ("Id" integer PRIMARY KEY, "Code" text UNIQUE, "State" "MySchema"."Mood" DEFAULT 'ok');
        CREATE TABLE "MySchema"."Child" (
            "Id" integer,
            "ParentId" integer REFERENCES "MySchema"."Parent" ("Id"),
            "Amount" integer CHECK ("Amount" > 0),
            CONSTRAINT "Child_Pk" PRIMARY KEY ("Id"));
        CREATE INDEX "IxChild" ON "MySchema"."Child" ("Amount", lower("ParentId"::text)) WHERE "Amount" > 0;
        CREATE VIEW "MySchema"."ChildView" AS SELECT "Id", "Amount" FROM "MySchema"."Child";
        CREATE FUNCTION "MySchema"."DoIt"("Arg" integer) RETURNS integer LANGUAGE sql AS $$ SELECT "Arg" $$;
        """;

    [TestMethod]
    public void ASelfDiffIsEmpty()
    {
        AssertNoChanges(new DiffEngine().Diff(Build(Project), Build(Project)));
    }

    [TestMethod]
    public void TheSameProjectWrittenDifferentlyReportsNothing()
    {
        var reformatted = Project.Replace("CREATE TABLE", "create   table", StringComparison.Ordinal).Replace("INTEGER", "integer", StringComparison.Ordinal).Replace(" integer,", "   integer ,", StringComparison.Ordinal);

        AssertNoChanges(new DiffEngine().Diff(Build(Project), Build(reformatted)));
    }

    [TestMethod]
    public void AChangeOfCaseIsAChange()
    {
        // PostgreSQL compares names exactly (G-206): "MyTable" and mytable are two tables, so one is removed and the other added.
        var changes = new DiffEngine().Diff(
            Build("CREATE TABLE \"MyTable\" (\"Mixed\" integer, plain text);"),
            Build("CREATE TABLE mytable (mixed integer, plain text);"));

        Assert.IsTrue(changes.Removed.Any(static change => change.NodeId == "public.MyTable"), Describe(changes));
        Assert.IsTrue(changes.Added.Any(static change => change.NodeId == "public.mytable"), Describe(changes));
    }

    [TestMethod]
    public void ADefaultThatPostgreSqlPrintsWithItsTypeIsTheSameDefault()
    {
        AssertNoChanges(new DiffEngine().Diff(
            Build("CREATE TYPE \"S\".\"Mood\" AS ENUM ('ok'); CREATE TABLE \"S\".\"T\" (\"M\" \"S\".\"Mood\" DEFAULT 'ok'::\"S\".\"Mood\");"),
            Build("CREATE TYPE \"S\".\"Mood\" AS ENUM ('ok'); CREATE TABLE \"S\".\"T\" (\"M\" \"S\".\"Mood\" DEFAULT 'ok');")));
    }

    [TestMethod]
    public void AChangedColumnTypeInAMixedCaseTableIsReported()
    {
        var changes = new DiffEngine().Diff(Build(Project), Build(Project.Replace("\"Amount\" integer CHECK", "\"Amount\" bigint CHECK", StringComparison.Ordinal)));

        Assert.IsTrue(changes.Modified.Any(static change => string.Equals(change.NodeId, "MySchema.Child.Amount", StringComparison.OrdinalIgnoreCase)), Describe(changes));
    }

    [TestMethod]
    public void AChangedCheckOnAQuotedColumnIsReported()
    {
        var changes = new DiffEngine().Diff(Build(Project), Build(Project.Replace("CHECK (\"Amount\" > 0)", "CHECK (\"Amount\" > 5)", StringComparison.Ordinal)));

        Assert.IsTrue(changes.Modified.Any(static change => change.NodeKind == ChangeNodeKind.Constraint), Describe(changes));
    }

    [TestMethod]
    public void AChangedIndexExpressionOnAQuotedColumnIsReported()
    {
        var changes = new DiffEngine().Diff(Build(Project), Build(Project.Replace("lower(\"ParentId\"::text)", "upper(\"ParentId\"::text)", StringComparison.Ordinal)));

        Assert.IsTrue(changes.Modified.Any(static change => change.NodeKind == ChangeNodeKind.Index), Describe(changes));
    }

    [TestMethod]
    public void AddingAndRemovingAQuotedColumnAreReported()
    {
        var added = new DiffEngine().Diff(Build(Project), Build(Project.Replace("\"Amount\" integer CHECK (\"Amount\" > 0),", "\"Amount\" integer CHECK (\"Amount\" > 0), \"Extra Col\" text,", StringComparison.Ordinal)));
        var removed = new DiffEngine().Diff(Build(Project), Build(Project.Replace("\"ParentId\" integer REFERENCES \"MySchema\".\"Parent\" (\"Id\"),", string.Empty, StringComparison.Ordinal).Replace("\"ParentId\"::text", "\"Amount\"::text", StringComparison.Ordinal)));

        Assert.IsTrue(added.Added.Any(static change => string.Equals(change.NodeId, "MySchema.Child.Extra Col", StringComparison.OrdinalIgnoreCase)), Describe(added));
        Assert.IsTrue(removed.Removed.Any(static change => string.Equals(change.NodeId, "MySchema.Child.ParentId", StringComparison.OrdinalIgnoreCase)), Describe(removed));
    }

    [TestMethod]
    public void AChangedParameterNameOfAMixedCaseFunctionIsReported()
    {
        var changes = new DiffEngine().Diff(Build(Project), Build(Project.Replace("\"Arg\" integer) RETURNS integer LANGUAGE sql AS $$ SELECT \"Arg\" $$", "\"Other\" integer) RETURNS integer LANGUAGE sql AS $$ SELECT \"Other\" $$", StringComparison.Ordinal)));

        Assert.IsTrue(changes.Modified.Any(static change => change.NodeKind == ChangeNodeKind.Function), Describe(changes));
    }

    private static void AssertNoChanges(ChangeSet changes)
        => Assert.AreEqual(0, changes.Added.Count + changes.Removed.Count + changes.Modified.Count, Describe(changes));

    private static string Describe(ChangeSet changes)
        => "Added: " + string.Join(", ", changes.Added.Select(static change => change.NodeId))
            + " Removed: " + string.Join(", ", changes.Removed.Select(static change => change.NodeId))
            + " Modified: " + string.Join("; ", changes.Modified.Select(static change => change.NodeId + " [" + string.Join(", ", change.Details.Select(static detail => detail.PropertyName + ": " + detail.OldValue + " -> " + detail.NewValue)) + "]"));

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
