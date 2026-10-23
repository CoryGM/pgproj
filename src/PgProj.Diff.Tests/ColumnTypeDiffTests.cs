using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-45: the diff must see a change in a column's precision, scale, length, interval fields, or array-ness (it was
/// blind to them, a silent under-migration), and must not report two spellings of the same type as different.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ColumnTypeDiffTests
{
    [TestMethod]
    [DataRow("numeric(10,2)", "NUMERIC( 10 , 2 )")]
    [DataRow("numeric(5)", "numeric(5,0)")]
    [DataRow("numeric(5)", "decimal(5)")]
    [DataRow("varchar(20)", "character varying(20)")]
    [DataRow("char(3)", "character(3)")]
    [DataRow("char", "character(1)")]
    [DataRow("integer[]", "int[3]")]
    [DataRow("timestamptz(3)", "timestamp(3) with time zone")]
    [DataRow("interval day to second(2)", "INTERVAL DAY TO SECOND(2)")]
    // public is on the default search path, so a type declared there reads back from PostgreSQL unqualified (G-75).
    [DataRow("public.uuid", "uuid")]
    public void EquivalentSpellingsProduceNoColumnChange(string oldType, string newType)
    {
        var result = new DiffEngine().Execute(BuildModel(oldType), BuildModel(newType));

        Assert.IsEmpty(result.ModifiedColumns, $"{oldType} vs {newType}: " + Describe(result));
    }

    [TestMethod]
    public void SelfDiffIsEmpty()
    {
        var model = BuildModel("numeric(10,2)[]");

        Assert.IsEmpty(new DiffEngine().Execute(model, model).ModifiedColumns);
    }

    [TestMethod]
    [DataRow("varchar(10)", "varchar(20)", "NormalizedTypeModifiers")]
    [DataRow("varchar", "varchar(20)", "NormalizedTypeModifiers")]
    [DataRow("numeric(10,2)", "numeric(12,2)", "NormalizedTypeModifiers")]
    [DataRow("numeric(10,2)", "numeric(10,3)", "NormalizedTypeModifiers")]
    [DataRow("numeric", "numeric(10,2)", "NormalizedTypeModifiers")]
    [DataRow("timestamp(3)", "timestamp", "NormalizedTypeModifiers")]
    [DataRow("integer", "integer[]", "IsArray")]
    [DataRow("interval", "interval year to month", "IntervalFields")]
    [DataRow("interval day to second", "interval day to minute", "IntervalFields")]
    // Only public and pg_catalog are on the default search path (G-75); a type in any other schema stays significant.
    [DataRow("myschema.mytype", "public.mytype", "DataType")]
    public void GenuineTypeChangesAreReported(string oldType, string newType, string expectedProperty)
    {
        var result = new DiffEngine().Execute(BuildModel(oldType), BuildModel(newType));

        Assert.HasCount(1, result.ModifiedColumns, $"{oldType} vs {newType}");
        Assert.IsTrue(
            result.ModifiedColumns[0].Details.Any(detail => detail.PropertyName == expectedProperty),
            $"{oldType} vs {newType}: expected a {expectedProperty} difference. " + Describe(result));
    }

    [TestMethod]
    public void OnlyTheChangedColumnIsReported()
    {
        var oldModel = new SemanticPipeline().Execute([new PgQueryParser().Parse("create table public.t (a varchar(10), b numeric(10,2), c text);")]);
        var newModel = new SemanticPipeline().Execute([new PgQueryParser().Parse("create table public.t (a varchar(10), b numeric(12,2), c text);")]);

        var result = new DiffEngine().Execute(oldModel, newModel);

        Assert.HasCount(1, result.ModifiedColumns);
        Assert.AreEqual("public.t.b", result.ModifiedColumns[0].NodeId);
    }

    private static string Describe(DiffResult result)
        => string.Join("; ", result.ModifiedColumns.Select(static change => change.NodeId + " [" + string.Join(", ", change.Details.Select(static detail => $"{detail.PropertyName}: {detail.OldValue} -> {detail.NewValue}")) + "]"));

    private static DatabaseModel BuildModel(string type)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse($"create table public.t (c {type});")]);
}
