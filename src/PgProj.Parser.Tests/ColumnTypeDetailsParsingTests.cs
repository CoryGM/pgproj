namespace PgProj.Parser.Tests;

/// <summary>
/// G-45: a column's type modifiers, array dimensions, and interval fields must be captured; the parser used to
/// keep only the base type name, so <c>numeric(10,2)</c> became <c>numeric</c> and <c>text[]</c> became <c>text</c>.
/// </summary>
[TestClass]
public sealed class ColumnTypeDetailsParsingTests
{
    [TestMethod]
    [DataRow("numeric(10,2)", "10,2", 0, null)]
    [DataRow("numeric(10,0)", "10,0", 0, null)]
    [DataRow("numeric(5)", "5", 0, null)]
    [DataRow("numeric", "", 0, null)]
    [DataRow("decimal(8,3)", "8,3", 0, null)]
    [DataRow("varchar(20)", "20", 0, null)]
    [DataRow("character varying(30)", "30", 0, null)]
    [DataRow("varchar", "", 0, null)]
    [DataRow("char(3)", "3", 0, null)]
    [DataRow("char", "1", 0, null)]
    [DataRow("bit(8)", "8", 0, null)]
    [DataRow("bit varying(12)", "12", 0, null)]
    [DataRow("timestamp(3) with time zone", "3", 0, null)]
    [DataRow("timestamp", "", 0, null)]
    [DataRow("time(2) without time zone", "2", 0, null)]
    [DataRow("text[]", "", 1, null)]
    [DataRow("integer[3][]", "", 2, null)]
    [DataRow("text ARRAY", "", 1, null)]
    [DataRow("numeric(10,2)[]", "10,2", 1, null)]
    [DataRow("varchar(20)[]", "20", 1, null)]
    [DataRow("integer", "", 0, null)]
    [DataRow("interval", "", 0, null)]
    [DataRow("interval(3)", "3", 0, null)]
    [DataRow("interval year to month", "", 0, "year to month")]
    [DataRow("interval day to second(2)", "2", 0, "day to second")]
    [DataRow("interval second(3)", "3", 0, "second")]
    [DataRow("interval hour to minute", "", 0, "hour to minute")]
    [DataRow("interval day", "", 0, "day")]
    [DataRow("geometry(Point,4326)", "point,4326", 0, null)]
    public void ModifiersArraysAndIntervalFieldsAreCaptured(string type, string expectedModifiers, int expectedDimensions, string? expectedIntervalFields)
    {
        var column = Parse($"create table public.t (c {type});");

        Assert.AreEqual(expectedModifiers, string.Join(",", column.TypeModifiers), type);
        Assert.AreEqual(expectedDimensions, column.ArrayDimensions, type);
        Assert.AreEqual(expectedIntervalFields, column.IntervalFields, type);
    }

    [TestMethod]
    public void BaseTypeNameIsStillTheBareName()
    {
        Assert.AreEqual("pg_catalog.numeric", Parse("create table public.t (c numeric(10,2));").RawType);
        Assert.AreEqual("pg_catalog.varchar", Parse("create table public.t (c varchar(20)[]);").RawType);
        Assert.AreEqual("pg_catalog.interval", Parse("create table public.t (c interval day to second(2));").RawType);
    }

    [TestMethod]
    public void StringModifiersAreQuoted()
    {
        var column = Parse("create table public.t (c mytype('a''b', 3));");

        Assert.AreEqual("'a''b',3", string.Join(",", column.TypeModifiers));
    }

    private static ColumnNode Parse(string sql)
        => new PgQueryParser().Parse(sql).Columns.Single();
}
