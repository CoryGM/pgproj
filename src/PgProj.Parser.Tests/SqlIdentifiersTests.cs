using PgProj.Parser;

namespace PgProj.Parser.Tests;

/// <summary>
/// G-70: PostgreSQL keeps the case of a quoted identifier, so a name that reaches the model with a capital (or a space, or a reserved
/// word) was quoted and must be quoted again wherever pgproj writes it, and a quoted name in text pgproj reads must be read as PostgreSQL
/// reads it.
/// </summary>
[TestClass]
public sealed class SqlIdentifiersTests
{
    [TestMethod]
    [DataRow("\"MySchema\".\"Mood\"", new[] { "MySchema", "Mood" })]
    [DataRow("public.t", new[] { "public", "t" })]
    [DataRow("Public.T", new[] { "public", "t" })]
    [DataRow("\"Public\".t", new[] { "Public", "t" })]
    [DataRow("\"a.b\".c", new[] { "a.b", "c" })]
    [DataRow("\"say \"\"hi\"\"\"", new[] { "say \"hi\"" })]
    [DataRow("  \"A\" . \"B\"  ", new[] { "A", "B" })]
    [DataRow("MyTable", new[] { "mytable" })]
    [DataRow("\"\"", new[] { "" })]
    public void AQualifiedNameIsSplitAsPostgreSqlReadsIt(string text, string[] expected)
    {
        CollectionAssert.AreEqual(expected, SqlIdentifiers.SplitQualifiedName(text).ToArray());
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void NothingSplitsToNothing(string? text)
    {
        Assert.IsEmpty(SqlIdentifiers.SplitQualifiedName(text).ToArray());
    }

    [TestMethod]
    [DataRow("orders", true)]
    [DataRow("_x", true)]
    [DataRow("a1$", true)]
    [DataRow("Orders", false)]
    [DataRow("1a", false)]
    [DataRow("my table", false)]
    [DataRow("a-b", false)]
    [DataRow("say \"hi\"", false)]
    [DataRow("", false)]
    public void OnlyLowerCaseLettersDigitsUnderscoreAndDollarArePlain(string identifier, bool expected)
    {
        Assert.AreEqual(expected, SqlIdentifiers.IsPlain(identifier));
    }

    [TestMethod]
    public void QuoteAlwaysQuotesAndDoublesAnEmbeddedQuote()
    {
        Assert.AreEqual("\"orders\"", SqlIdentifiers.Quote("orders"));
        Assert.AreEqual("\"say \"\"hi\"\"\"", SqlIdentifiers.Quote("say \"hi\""));
    }

    [TestMethod]
    [DataRow("orders", "orders")]
    [DataRow("id", "id")]
    [DataRow("Mixed", "\"Mixed\"")]
    [DataRow("my col", "\"my col\"")]
    [DataRow("order", "\"order\"")]
    [DataRow("user", "\"user\"")]
    [DataRow("select", "\"select\"")]
    [DataRow("group", "\"group\"")]
    [DataRow("from", "\"from\"")]
    [DataRow("limit", "\"limit\"")]
    [DataRow("say \"hi\"", "\"say \"\"hi\"\"\"")]
    public void ANameIsQuotedOnlyWhenItMustBe(string identifier, string expected)
    {
        Assert.AreEqual(expected, SqlIdentifiers.QuoteName(identifier));
    }

    [TestMethod]
    [DataRow("lower")]
    [DataRow("left")]
    [DataRow("numeric")]
    [DataRow("varchar")]
    [DataRow("coalesce")]
    public void AFunctionOrTypeNameThatIsAKeywordIsNotQuotedForThat(string name)
    {
        Assert.AreEqual(name, SqlIdentifiers.QuotePlain(name));
        Assert.AreEqual("\"MyFunc\"", SqlIdentifiers.QuotePlain("MyFunc"));
    }

    [TestMethod]
    [DataRow("pg_catalog.int4", "pg_catalog.int4")]
    [DataRow("public.mood", "public.mood")]
    [DataRow("text", "text")]
    [DataRow("numeric", "numeric")]
    [DataRow("MySchema.Mood", "\"MySchema\".\"Mood\"")]
    [DataRow("myschema.Mood", "myschema.\"Mood\"")]
    [DataRow("MySchema.mood", "\"MySchema\".mood")]
    [DataRow("MySchema.Mood[]", "\"MySchema\".\"Mood\"[]")]
    [DataRow("MySchema.Mood[][]", "\"MySchema\".\"Mood\"[][]")]
    [DataRow("pg_catalog.int4[]", "pg_catalog.int4[]")]
    public void ATypeNameHasQuotesOnlyOnThePartsThatNeedThem(string typeName, string expected)
    {
        Assert.AreEqual(expected, SqlIdentifiers.QuoteTypeName(typeName));
    }

    [TestMethod]
    [DataRow("\"MySchema\".\"Mood\"")]
    [DataRow("double precision")]
    [DataRow("character varying(20)")]
    [DataRow("a.b c")]
    [DataRow("")]
    public void TextThatIsNotAPlainDottedNameIsLeftAlone(string typeName)
    {
        Assert.AreEqual(typeName, SqlIdentifiers.QuoteTypeName(typeName));
    }

    [TestMethod]
    public void QuotingAndSplittingRoundTrip()
    {
        foreach (var name in new[] { "MySchema", "say \"hi\"", "my col", "order", "plain", "A.B" })
        {
            var parts = SqlIdentifiers.SplitQualifiedName(SqlIdentifiers.Quote(name) + "." + SqlIdentifiers.Quote(name));

            CollectionAssert.AreEqual(new[] { name, name }, parts.ToArray(), name);
        }
    }
}
