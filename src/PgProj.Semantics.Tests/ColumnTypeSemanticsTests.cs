using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-45: column type details flow into the model, canonicalize for comparison, and every standard built-in type
/// name resolves (G-48).
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ColumnTypeSemanticsTests
{
    [TestMethod]
    public void DeclaredDetailsAreKeptAsWritten()
    {
        var model = BuildModel("create table public.t (a numeric(5), b varchar(20)[], c interval day to second(2));");

        var a = Column(model, "a");
        CollectionAssert.AreEqual(new[] { "5" }, a.TypeModifiers.ToArray());
        CollectionAssert.AreEqual(new[] { "5", "0" }, a.NormalizedTypeModifiers.ToArray());

        var b = Column(model, "b");
        CollectionAssert.AreEqual(new[] { "20" }, b.TypeModifiers.ToArray());
        Assert.AreEqual(1, b.ArrayDimensions);

        var c = Column(model, "c");
        Assert.AreEqual("day to second", c.IntervalFields);
        CollectionAssert.AreEqual(new[] { "2" }, c.TypeModifiers.ToArray());
    }

    [TestMethod]
    [DataRow("numeric(10,2)", "NUMERIC( 10 , 2 )")]
    [DataRow("numeric(5)", "numeric(5,0)")]
    [DataRow("numeric(5)", "decimal(5)")]
    [DataRow("varchar(20)", "character varying(20)")]
    [DataRow("char(3)", "character(3)")]
    [DataRow("char", "character(1)")]
    [DataRow("bit", "bit(1)")]
    [DataRow("integer[]", "int[]")]
    [DataRow("integer[]", "integer[3]")]
    [DataRow("integer[]", "integer[][]")]
    [DataRow("timestamptz(3)", "timestamp(3) with time zone")]
    [DataRow("interval day to second(2)", "INTERVAL DAY TO SECOND (2)")]
    public void EquivalentSpellingsHaveTheSameComparableSignature(string first, string second)
    {
        Assert.AreEqual(Signature(first), Signature(second), $"{first} vs {second}");
    }

    [TestMethod]
    [DataRow("varchar(10)", "varchar(20)")]
    [DataRow("varchar", "varchar(20)")]
    [DataRow("numeric(10,2)", "numeric(12,2)")]
    [DataRow("numeric(10,2)", "numeric(10,3)")]
    [DataRow("numeric", "numeric(10,2)")]
    [DataRow("numeric(5)", "numeric(5,2)")]
    [DataRow("integer", "integer[]")]
    [DataRow("text", "varchar")]
    [DataRow("timestamp(3)", "timestamp")]
    [DataRow("timestamp(3)", "timestamp(6)")]
    [DataRow("interval", "interval year to month")]
    [DataRow("interval day to second(2)", "interval day to second(3)")]
    [DataRow("interval day to second", "interval day to minute")]
    public void GenuinelyDifferentTypesHaveDifferentSignatures(string first, string second)
    {
        Assert.AreNotEqual(Signature(first), Signature(second), $"{first} vs {second}");
    }

    [TestMethod]
    [DataRow("smallint")]
    [DataRow("integer")]
    [DataRow("bigint")]
    [DataRow("real")]
    [DataRow("double precision")]
    [DataRow("float")]
    [DataRow("float(24)")]
    [DataRow("float(53)")]
    [DataRow("numeric(10,2)")]
    [DataRow("char(3)")]
    [DataRow("character(2)")]
    [DataRow("bpchar")]
    [DataRow("bit varying(3)")]
    [DataRow("varchar(5)")]
    [DataRow("timestamptz")]
    [DataRow("interval")]
    [DataRow("macaddr8")]
    [DataRow("circle")]
    [DataRow("int4multirange")]
    [DataRow("oid")]
    [DataRow("regclass")]
    [DataRow("name")]
    [DataRow("jsonpath")]
    [DataRow("xid8")]
    [DataRow("text[]")]
    public void StandardBuiltInTypesResolveWithoutErrors(string type)
    {
        var model = BuildModel($"create table public.t (c {type});");

        Assert.IsFalse(model.HasErrors, $"{type}: " + string.Join(" | ", model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    private static string Signature(string type)
        => Column(BuildModel($"create table public.t (c {type});"), "c").GetComparableTypeSignature();

    private static ColumnDefinition Column(DatabaseModel model, string name)
        => model.Tables["public.t"].Columns.Single(column => column.Name == name);

    private static DatabaseModel BuildModel(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
