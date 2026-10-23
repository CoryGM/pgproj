namespace PgProj.Parser.Tests;

/// <summary>
/// G-130: <c>CREATE AGGREGATE</c> is read instead of being warned about and ignored (G-106): the schema and name, the argument types (with <c>VARIADIC</c>, <c>(*)</c>, and
/// <c>ORDER BY</c>), and every option by name. An aggregate is in a schema, so an unqualified one is in the default schema.
/// </summary>
[TestClass]
public sealed class AggregateParsingTests
{
    [TestMethod]
    public void AnAggregateIsReadWithItsArgumentsAndEveryOption()
    {
        // Act
        var tree = new PgQueryParser().Parse(
            "CREATE AGGREGATE app.myavg (integer, text[]) (SFUNC = app.sf, STYPE = int8[], SSPACE = 64, FINALFUNC = ff, FINALFUNC_EXTRA, FINALFUNC_MODIFY = READ_WRITE, COMBINEFUNC = cf, SERIALFUNC = sr, DESERIALFUNC = ds, "
            + "INITCOND = '{0,0}', MSFUNC = msf, MINVFUNC = mif, MSTYPE = int8, MSSPACE = 10, MFINALFUNC = mff, MFINALFUNC_EXTRA, MINITCOND = '0', SORTOP = <, PARALLEL = SAFE);");
        var node = tree.Aggregates.Single();

        // Assert
        Assert.AreEqual(0, tree.Diagnostics.Count);
        Assert.AreEqual("myavg", node.Name);
        Assert.AreEqual("app", node.SchemaName);
        CollectionAssert.AreEqual(new[] { "pg_catalog.int4", "text[]" }, node.Arguments.ToArray());
        Assert.AreEqual(-1, node.DirectArgumentCount);
        Assert.AreEqual("app.sf", node.Options["sfunc"]);
        Assert.AreEqual("int8[]", node.Options["stype"]);
        Assert.AreEqual("64", node.Options["sspace"]);
        Assert.AreEqual("true", node.Options["finalfunc_extra"]);
        Assert.AreEqual("read_write", node.Options["finalfunc_modify"]);
        Assert.AreEqual("{0,0}", node.Options["initcond"]);
        Assert.AreEqual("<", node.Options["sortop"]);
        Assert.AreEqual("safe", node.Options["parallel"]);
        Assert.AreEqual(19, node.Options.Count);
        Assert.AreEqual("10", node.Options["msspace"]);
    }

    [TestMethod]
    public void AnAggregateWithoutASchemaIsInTheDefaultSchema()
    {
        var node = new PgQueryParser().Parse("CREATE AGGREGATE agg (integer) (SFUNC = int4pl, STYPE = integer);").Aggregates.Single();

        Assert.AreEqual("public", node.SchemaName);
        Assert.IsTrue(node.IsSchemaDefaulted);
    }

    [TestMethod]
    public void AStarAggregateHasNoArguments()
    {
        var node = new PgQueryParser().Parse("CREATE AGGREGATE agg (*) (SFUNC = int8inc, STYPE = bigint);").Aggregates.Single();

        Assert.IsEmpty(node.Arguments);
    }

    [TestMethod]
    public void TheOldSyntaxGivesTheBaseTypeAsTheArgument()
    {
        var node = new PgQueryParser().Parse("CREATE AGGREGATE agg (BASETYPE = integer, SFUNC = int4pl, STYPE = integer);").Aggregates.Single();
        var any = new PgQueryParser().Parse("CREATE AGGREGATE agg2 (BASETYPE = ANY, SFUNC = int8inc, STYPE = bigint);").Aggregates.Single();

        CollectionAssert.AreEqual(new[] { "pg_catalog.int4" }, node.Arguments.ToArray());
        Assert.IsFalse(node.Options.ContainsKey("basetype"));
        Assert.IsEmpty(any.Arguments);
    }

    [TestMethod]
    public void AnOrderedSetAggregateKeepsItsDirectArgumentCount()
    {
        var node = new PgQueryParser().Parse("CREATE AGGREGATE agg (float8 ORDER BY float8) (SFUNC = array_append, STYPE = float8[], FINALFUNC = g);").Aggregates.Single();

        CollectionAssert.AreEqual(new[] { "float8", "float8" }, node.Arguments.ToArray());
        Assert.AreEqual(1, node.DirectArgumentCount);
    }

    [TestMethod]
    public void AVariadicArgumentIsMarked()
    {
        var node = new PgQueryParser().Parse("CREATE AGGREGATE agg (VARIADIC \"any\" ORDER BY VARIADIC \"any\") (SFUNC = f, STYPE = internal, HYPOTHETICAL);").Aggregates.Single();

        CollectionAssert.AreEqual(new[] { "VARIADIC any" }, node.Arguments.ToArray());
        Assert.AreEqual("true", node.Options["hypothetical"]);
    }

    [TestMethod]
    public void AnAggregateIsNoLongerReportedAsUnmodeled()
    {
        var tree = new PgQueryParser().Parse("CREATE AGGREGATE public.a (int) (SFUNC = int4pl, STYPE = int);");

        Assert.IsFalse(tree.Diagnostics.Any(static d => d.Message.Contains("CREATE AGGREGATE", StringComparison.Ordinal)));
    }
}
