using PgProj.Parser;

namespace PgProj.Parser.Tests;

/// <summary>
/// G-70: the parser renders <c>CHECK</c> conditions, index expressions, and routine signatures back to SQL text, and pg_query gives it
/// identifiers without their quotes. <c>CHECK ("Amount" &gt; 0)</c> was rendered <c>CHECK (Amount &gt; 0)</c>, which names another column
/// (or none), and a parameter <c>"Arg"</c> was written <c>Arg</c>. A name that needs quotes must have them, and a plain one must not
/// change (so every text that was right stays the same).
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class QuotedIdentifierRenderingTests
{
    [TestMethod]
    [DataRow("(\"Amount\" > 0)", "\"Amount\" > 0")]
    [DataRow("(quantity > 0)", "quantity > 0")]
    [DataRow("(\"order\" > 0)", "\"order\" > 0")]
    [DataRow("(\"my col\" IS NOT NULL)", "\"my col\" IS NOT NULL")]
    [DataRow("(\"A\" > 0 AND \"B\" < 9)", "\"A\" > 0 AND \"B\" < 9")]
    [DataRow("(lower(\"Name\") = 'x')", "lower(\"Name\") = 'x'")]
    [DataRow("(\"MyFunc\"(\"Id\") > 0)", "\"MyFunc\"(\"Id\") > 0")]
    [DataRow("(\"t\".\"Amount\" > 0)", "\"t\".\"Amount\" > 0")]
    [DataRow("(\"Id\"::bigint > 0)", "\"Id\"::bigint > 0")]
    public void ACheckConditionKeepsTheQuotesOfANameThatNeedsThem(string check, string expected)
    {
        var tree = new PgQueryParser().Parse($"CREATE TABLE t (\"Id\" integer, CHECK {check});");

        Assert.AreEqual(expected, tree.Constraints.Single().RawCheckExpression);
    }

    [TestMethod]
    public void ACastToAMixedCaseTypeKeepsItsQuotes()
    {
        var tree = new PgQueryParser().Parse("CREATE TABLE t (\"State\" text, CHECK (\"State\"::\"MySchema\".\"Mood\" IS NOT NULL));");

        Assert.AreEqual("\"State\"::\"MySchema\".\"Mood\" IS NOT NULL", tree.Constraints.Single().RawCheckExpression);
    }

    [TestMethod]
    public void AnIndexExpressionAndPredicateKeepTheQuotesOfTheirNames()
    {
        var index = new PgQueryParser().Parse("CREATE INDEX \"IxA\" ON \"S\".\"T\" (lower(\"Col Name\"), \"Amount\") WHERE \"Order\" > 0 AND \"select\" > 0;")
            .Indexes.Single();

        Assert.AreEqual("IxA", index.Name);
        Assert.AreEqual("S", index.SchemaName);
        Assert.AreEqual("T", index.TableName);
        Assert.AreEqual("lower(\"Col Name\")", index.KeyElements[0].RawExpression);
        Assert.AreEqual("Amount", index.KeyElements[1].Name);
        Assert.AreEqual("\"Order\" > 0 AND \"select\" > 0", index.RawPredicateExpression);
    }

    [TestMethod]
    public void AWholeRowReferenceKeepsItsStar()
    {
        var tree = new PgQueryParser().Parse("CREATE TABLE t (a integer, CHECK (t.* IS NOT NULL));");

        StringAssert.Contains(tree.Constraints.Single().RawCheckExpression, "t.*");
    }

    [TestMethod]
    public void ARoutineParameterAndItsTypeAreQuotedWhenTheyNeedIt()
    {
        var function = new PgQueryParser().Parse(
            "CREATE FUNCTION \"S\".\"F\"(\"Arg\" \"S\".\"Mood\", \"order\" integer, plain text, \"Many\" \"S\".\"Mood\"[]) RETURNS \"S\".\"Mood\" LANGUAGE sql AS $$ SELECT 1 $$;")
            .Functions.Single();

        Assert.AreEqual("F", function.Name);
        Assert.AreEqual("S", function.SchemaName);
        Assert.AreEqual("\"Arg\" \"S\".\"Mood\", \"order\" integer, plain text, \"Many\" \"S\".\"Mood\"[]", function.Signature!.RenderParameterList());
    }

    [TestMethod]
    public void TheArgumentTypesForDropAndCommentAreQuotedButTheIdentityFormIsNot()
    {
        var signature = new PgQueryParser().Parse(
            "CREATE FUNCTION \"S\".\"F\"(\"Arg\" \"S\".\"Mood\", n integer, OUT result text) RETURNS text LANGUAGE sql AS $$ SELECT 1 $$;")
            .Functions.Single().Signature!;

        Assert.AreEqual("\"S\".\"Mood\", integer", signature.RenderArgumentTypeSignatureSql());
        Assert.AreEqual("S.Mood, pg_catalog.int4", signature.RenderArgumentTypeSignature());
    }

    [TestMethod]
    public void PlainNamesAreRenderedExactlyAsBefore()
    {
        var function = new PgQueryParser().Parse("CREATE FUNCTION public.f(a integer, b text DEFAULT 'x') RETURNS integer LANGUAGE sql AS $$ SELECT 1 $$;")
            .Functions.Single();

        Assert.AreEqual("a integer, b text DEFAULT 'x'", function.Signature!.RenderParameterList());
        Assert.AreEqual("integer, text", function.Signature.RenderArgumentTypeSignatureSql());
    }

    [TestMethod]
    public void ATableConstraintAndTriggerKeepTheCaseOfTheirNames()
    {
        var tree = new PgQueryParser().Parse(
            "CREATE TABLE \"S\".\"T\" (\"Id\" integer, \"Ref\" integer, CONSTRAINT \"Pk\" PRIMARY KEY (\"Id\"), CONSTRAINT \"Fk\" FOREIGN KEY (\"Ref\") REFERENCES \"S\".\"U\" (\"Key\"), CONSTRAINT \"Uq\" UNIQUE (\"Ref\", \"Id\"));");

        var byName = tree.Constraints.ToDictionary(static constraint => constraint.Name!);
        CollectionAssert.AreEqual(new[] { "Id" }, byName["Pk"].KeyColumns.ToArray());
        CollectionAssert.AreEqual(new[] { "Ref" }, byName["Fk"].ForeignKeyColumns.ToArray());
        CollectionAssert.AreEqual(new[] { "Key" }, byName["Fk"].ForeignKeyReferencedColumns.ToArray());
        Assert.AreEqual("U", byName["Fk"].ForeignKeyReferencedTableName);
        CollectionAssert.AreEqual(new[] { "Ref", "Id" }, byName["Uq"].KeyColumns.ToArray());
    }
}
