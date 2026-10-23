namespace PgProj.Parser.Tests;

/// <summary>G-115: a range type's <c>SUBTYPE</c> is read from pg_query's tree the way a column's type is, so it can be compared as one.</summary>
[TestClass]
public sealed class RangeSubtypeParsingTests
{
    [TestMethod]
    [DataRow("float8", "float8", 0)]
    [DataRow("double precision", "pg_catalog.float8", 0)]
    [DataRow("public.money_amount", "public.money_amount", 0)]
    [DataRow("int4[]", "int4", 1)]
    public void TheSubtypeIsReadFromTheTree(string written, string baseType, int arrayDimensions)
    {
        var subtype = Single($"CREATE TYPE public.r AS RANGE (subtype = {written}, subtype_diff = float8mi);").RangeSubtype;

        Assert.IsNotNull(subtype);
        Assert.AreEqual(baseType, subtype.BaseType);
        Assert.AreEqual(arrayDimensions, subtype.ArrayDimensions);
    }

    [TestMethod]
    public void AnotherKindOfTypeHasNoSubtype()
        => Assert.IsNull(Single("CREATE TYPE public.m AS ENUM ('a');").RangeSubtype);

    private static TypeNode Single(string sql) => new PgQueryParser().Parse(sql).Types.Single();
}
