namespace PgProj.Parser.Tests;

/// <summary>
/// G-192: a composite field's <c>COLLATE</c> is read from pg_query's tree, its name as written, and is not part of the field's type text. Before, the clause stayed in the
/// type text, which the type-name normalizer lower-cased, so <c>COLLATE "C"</c> was deployed as <c>COLLATE "c"</c> and the publish failed.
/// </summary>
[TestClass]
public sealed class CompositeFieldCollationParsingTests
{
    [TestMethod]
    public void TheCollationIsReadApartFromTheType()
    {
        var type = Single("""CREATE TYPE public.pt AS (x float8, label text COLLATE "C", code varchar(10) collate "POSIX", tags text[] COLLATE pg_catalog."C");""");

        CollectionAssert.AreEqual(new[] { "float8", "text", "varchar(10)", "text[]" }, type.CompositeFieldTypes.Select(static field => field.RawType).ToArray());
        CollectionAssert.AreEqual(new string?[] { null, "C", "POSIX", "pg_catalog.C" }, type.CompositeFieldTypes.Select(static field => field.CollationName).ToArray());
    }

    [TestMethod]
    public void AnUnquotedOrSchemaQualifiedCollationKeepsItsName()
    {
        var type = Single("CREATE TYPE public.pt AS (a text COLLATE app.ci, b text COLLATE ci /* why */);");

        CollectionAssert.AreEqual(new[] { "text", "text" }, type.CompositeFieldTypes.Select(static field => field.RawType).ToArray());
        CollectionAssert.AreEqual(new[] { "app.ci", "ci" }, type.CompositeFieldTypes.Select(static field => field.CollationName).ToArray());
    }

    [TestMethod]
    public void TheDatabasesSpellingIsReadTheSameWay()
    {
        var type = Single("""CREATE TYPE public.pt AS ("label" text COLLATE "pg_catalog"."C", n integer)""");

        Assert.AreEqual("text", type.CompositeFieldTypes[0].RawType);
        Assert.AreEqual("pg_catalog.C", type.CompositeFieldTypes[0].CollationName);
        Assert.IsNull(type.CompositeFieldTypes[1].CollationName);
    }

    private static TypeNode Single(string sql) => new PgQueryParser().Parse(sql).Types.Single();
}
