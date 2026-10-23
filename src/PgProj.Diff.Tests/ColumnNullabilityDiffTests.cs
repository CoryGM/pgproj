using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-62: a column written with an explicit <c>NULL</c> (<c>legal_surname text NULL</c>) re-diffed forever. The project model
/// records it as nullable, the live snapshot renders a nullable column without <c>NOT NULL</c>, which reads back as
/// unspecified, and the two were compared as different. To PostgreSQL they are the same: a column allows null unless it is
/// <c>NOT NULL</c>. Both directions matter: spellings of nullable report nothing, and a real change of nullability is reported.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ColumnNullabilityDiffTests
{
    [TestMethod]
    [DataRow("text NULL", "text")]
    [DataRow("text", "text NULL")]
    [DataRow("text NULL", "text NULL")]
    [DataRow("text", "text")]
    [DataRow("text NOT NULL", "text NOT NULL")]
    [DataRow("text NULL DEFAULT 'x'", "text DEFAULT 'x'")]
    [DataRow("integer PRIMARY KEY", "integer NOT NULL PRIMARY KEY")]
    public void SpellingsOfTheSameNullabilityProduceNoColumnChange(string oldDefinition, string newDefinition)
    {
        var result = new DiffEngine().Execute(BuildModel(oldDefinition), BuildModel(newDefinition));

        Assert.IsEmpty(result.ModifiedColumns, $"{oldDefinition} vs {newDefinition}: " + Describe(result));
    }

    [TestMethod]
    [DataRow("text NULL", "text NOT NULL")]
    [DataRow("text", "text NOT NULL")]
    [DataRow("text NOT NULL", "text")]
    [DataRow("text NOT NULL", "text NULL")]
    public void GenuineNullabilityChangesAreReported(string oldDefinition, string newDefinition)
    {
        var result = new DiffEngine().Execute(BuildModel(oldDefinition), BuildModel(newDefinition));

        Assert.HasCount(1, result.ModifiedColumns, $"{oldDefinition} vs {newDefinition}: " + Describe(result));
        Assert.IsTrue(
            result.ModifiedColumns[0].Details.Any(static detail => detail.PropertyName == nameof(ColumnDefinition.IsNullable)),
            $"{oldDefinition} vs {newDefinition}: " + Describe(result));
    }

    [TestMethod]
    public void ReportedChangeShowsWhetherTheColumnAllowsNull()
    {
        var result = new DiffEngine().Execute(BuildModel("text"), BuildModel("text NOT NULL"));

        var detail = result.ModifiedColumns.Single().Details.Single(static detail => detail.PropertyName == nameof(ColumnDefinition.IsNullable));
        Assert.AreEqual("True", detail.OldValue);
        Assert.AreEqual("False", detail.NewValue);
    }

    [TestMethod]
    public void OnlyTheColumnWhoseNullabilityChangedIsReported()
    {
        var oldModel = Build("create table public.t (a text NULL, b text, c text NOT NULL);");
        var newModel = Build("create table public.t (a text, b text NULL, c text NULL);");

        var result = new DiffEngine().Execute(oldModel, newModel);

        Assert.HasCount(1, result.ModifiedColumns, Describe(result));
        Assert.AreEqual("public.t.c", result.ModifiedColumns[0].NodeId);
    }

    /// <summary>The project's explicit NULLs against the text the live loader renders for the same nullable columns.</summary>
    [TestMethod]
    public void ExplicitNullColumnsAgainstTheirLiveRenderingDiffEmpty()
    {
        var project = Build("create table s.person (id uuid NOT NULL, legal_surname text NULL, banned_utc timestamp without time zone NULL, user_ids jsonb NULL);");
        var live = Build("CREATE TABLE \"s\".\"person\" (\n\"id\" uuid NOT NULL,\n\"legal_surname\" text,\n\"banned_utc\" timestamp without time zone,\n\"user_ids\" jsonb\n);");

        var result = new DiffEngine().Diff(live, project);

        Assert.AreEqual(0, result.Added.Count + result.Removed.Count + result.Modified.Count, string.Join("; ", result.Modified.Select(static change => change.NodeId)));
    }

    private static string Describe(DiffResult result)
        => string.Join("; ", result.ModifiedColumns.Select(static change => change.NodeId + " [" + string.Join(", ", change.Details.Select(static detail => $"{detail.PropertyName}: {detail.OldValue} -> {detail.NewValue}")) + "]"));

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);

    private static DatabaseModel BuildModel(string columnDefinition)
        => Build($"create table public.t (c {columnDefinition});");
}
