using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-281 (approved by the user 2026-10-13): a column type change that keeps every value is not destructive, so <c>publish --execute</c> runs it
/// without <c>--allow-destructive</c>, which would also have let real data loss through. Any other type change still is.
/// </summary>
[TestClass]
public sealed class ColumnTypeWideningTests
{
    [TestMethod]
    [DataRow("varchar(50)", "varchar(100)")]
    [DataRow("varchar(50)", "varchar")]
    [DataRow("varchar(50)", "text")]
    [DataRow("character varying(10)", "VARCHAR(10)")]
    [DataRow("char(5)", "char(10)")]
    [DataRow("char(5)", "varchar(5)")]
    [DataRow("char(5)", "text")]
    [DataRow("numeric(10,2)", "numeric(12,2)")]
    [DataRow("numeric(10,2)", "numeric(11,3)")]
    [DataRow("numeric(10,2)", "numeric")]
    [DataRow("numeric(5)", "numeric(7,2)")]
    [DataRow("smallint", "integer")]
    [DataRow("smallint", "bigint")]
    [DataRow("int", "bigint")]
    [DataRow("real", "double precision")]
    [DataRow("varbit(4)", "varbit(8)")]
    [DataRow("timestamp(3)", "timestamp(6)")]
    [DataRow("timestamp(3)", "timestamp")]
    [DataRow("timestamptz(0)", "timestamptz(3)")]
    [DataRow("time(2)", "time")]
    [DataRow("int[]", "bigint[]")]
    public void AWideningKeepsEveryValueAndIsNotDestructive(string oldType, string newType)
    {
        Assert.IsTrue(ColumnTypeWidening.KeepsEveryValue(Column(oldType), Column(newType)), $"{oldType} -> {newType}");
        Assert.IsEmpty(Destructive(oldType, newType), $"{oldType} -> {newType}");
    }

    [TestMethod]
    [DataRow("varchar(100)", "varchar(50)")]
    [DataRow("text", "varchar(100)")]
    [DataRow("varchar(10)", "char(10)")]
    [DataRow("char(10)", "varchar(5)")]
    [DataRow("numeric(12,2)", "numeric(10,2)")]
    [DataRow("numeric(10,2)", "numeric(10,3)")]
    [DataRow("numeric(10,2)", "numeric(12,1)")]
    [DataRow("numeric", "numeric(30,10)")]
    [DataRow("bigint", "integer")]
    [DataRow("integer", "smallint")]
    [DataRow("integer", "real")]
    [DataRow("double precision", "real")]
    [DataRow("timestamp", "timestamp(3)")]
    [DataRow("timestamptz", "timestamp")]
    [DataRow("text", "integer")]
    [DataRow("int", "int[]")]
    [DataRow("varchar(5)", "text[]")]
    public void AnyOtherTypeChangeIsStillDestructive(string oldType, string newType)
    {
        Assert.IsFalse(ColumnTypeWidening.KeepsEveryValue(Column(oldType), Column(newType)), $"{oldType} -> {newType}");
        var reasons = Destructive(oldType, newType);
        Assert.HasCount(1, reasons, $"{oldType} -> {newType}");
        StringAssert.Contains(reasons[0], "type changed");
    }

    [TestMethod]
    public void TheReasonIsAsciiSoARedirectedConsoleKeepsIt()
    {
        // G-285: the reason used "→", which a Windows console writes as byte 0x1A to a file or a CI log.
        var reason = Destructive("varchar(50)", "varchar(10)").Single();

        Assert.AreEqual("column \"c\" type changed (varchar(50) -> varchar(10))", reason);
    }

    private static ColumnDefinition Column(string type)
        => Build($"CREATE TABLE public.t (id int PRIMARY KEY, c {type});").Tables["public.t"].Columns.Single(static column => column.Name == "c");

    private static IReadOnlyList<string> Destructive(string oldType, string newType)
    {
        var live = Build($"CREATE TABLE public.t (id int PRIMARY KEY, c {oldType});");
        var project = Build($"CREATE TABLE public.t (id int PRIMARY KEY, c {newType});");
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(live, project), new DependencyAnalyzer().Analyze(live, project));
        return batch.GetDestructiveOperations().SelectMany(static operation => operation.Reasons).ToArray();
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
