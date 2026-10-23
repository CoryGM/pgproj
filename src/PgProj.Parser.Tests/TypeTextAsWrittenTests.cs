namespace PgProj.Parser.Tests;

/// <summary>
/// G-207 step 3: every type keeps the text the author wrote, which is what is deployed, beside its normalized name, which stays the identity
/// and comparison form. This covers column types, routine parameters and results, a domain's base type, and the types a cast, operator, or
/// aggregate names. It closes G-201: a routine type that needs quotes (<c>"char"</c>, <c>"any"</c>, <c>"My S"."My T"</c>) was written without them.
/// </summary>
[TestClass]
public sealed class TypeTextAsWrittenTests
{
    [TestMethod]
    public void AColumnKeepsItsTypeAsWrittenBesideItsNormalizedName()
    {
        var columns = new PgQueryParser().Parse("CREATE TABLE t (a INT, b VARCHAR(10), c \"char\", d serial, e \"My S\".\"My T\"[]);").Columns.ToArray();

        CollectionAssert.AreEqual(new[] { "INT", "VARCHAR(10)", "\"char\"", null, "\"My S\".\"My T\"[]" }, columns.Select(static column => column.WrittenType).ToArray());
        Assert.AreEqual("pg_catalog.int4", columns[0].DataType);
    }

    [TestMethod]
    public void ARoutineKeepsItsParameterAndResultTypesAsWritten()
    {
        var signature = new PgQueryParser().Parse("CREATE FUNCTION f(a \"char\", VARIADIC b \"any\", c \"My S\".\"My T\", d INT) RETURNS SETOF \"char\" LANGUAGE c AS 'm', 'f';")
            .Functions.Single().Signature!;

        CollectionAssert.AreEqual(new[] { "\"char\"", "\"any\"", "\"My S\".\"My T\"", "INT" }, signature.Parameters.Select(static parameter => parameter.WrittenDataType).ToArray());
        Assert.AreEqual("\"char\"", signature.WrittenReturnType);
        Assert.AreEqual("a \"char\", VARIADIC b \"any\", c \"My S\".\"My T\", d INT", signature.RenderParameterList());
        Assert.AreEqual("\"char\", \"any\", \"My S\".\"My T\", INT", signature.RenderArgumentTypeSignatureSql());
        Assert.AreEqual("char, any, My S.My T, pg_catalog.int4", signature.RenderArgumentTypeSignature());
    }

    [TestMethod]
    public void ADomainKeepsItsBaseTypeAsWritten()
    {
        var domain = new PgQueryParser().Parse("CREATE DOMAIN d AS VARCHAR(10) NOT NULL;").Nodes.OfType<DomainNode>().Single();

        Assert.AreEqual("VARCHAR(10)", domain.WrittenBaseType);
    }

    [TestMethod]
    public void ACastOperatorAndAggregateKeepTheirTypesAsWritten()
    {
        var tree = new PgQueryParser().Parse("""
            CREATE CAST ("My S"."My T" AS "char") WITH FUNCTION tocharf("My S"."My T");
            CREATE OPERATOR === (LEFTARG = "My S"."My T", RIGHTARG = INT, FUNCTION = eqf);
            CREATE AGGREGATE agg("char") (SFUNC = accf, STYPE = INTEGER);
            """);

        var cast = tree.Nodes.OfType<CastNode>().Single();
        Assert.AreEqual("\"My S\".\"My T\"", cast.WrittenTypes[cast.SourceType!]);
        Assert.AreEqual("\"char\"", cast.WrittenTypes[cast.TargetType!]);
        var @operator = tree.Nodes.OfType<OperatorNode>().Single();
        Assert.AreEqual("INT", @operator.WrittenTypes[@operator.RightType!]);
        var aggregate = tree.Nodes.OfType<AggregateNode>().Single();
        Assert.AreEqual("\"char\"", aggregate.WrittenTypes["char"]);
        Assert.AreEqual("INTEGER", aggregate.WrittenTypes["pg_catalog.int4"]);
    }
}
