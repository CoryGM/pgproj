namespace PgProj.Parser.Tests;

/// <summary>G-200: a primary key's, unique constraint's, or exclusion constraint's <c>INCLUDE (...)</c> was not read, so it was not deployed.</summary>
[TestClass]
public sealed class ConstraintIncludeParsingTests
{
    [TestMethod]
    public void TheIncludeColumnsAreReadInOrderWithTheirCase()
    {
        var constraints = new PgQueryParser().Parse(
            "CREATE TABLE t (id int, a int, \"B\" int, r int4range, CONSTRAINT t_pk PRIMARY KEY (id) INCLUDE (a), CONSTRAINT t_u UNIQUE (a) INCLUDE (\"B\", id), CONSTRAINT t_x EXCLUDE USING gist (r WITH &&) INCLUDE (a));")
            .Nodes.OfType<ConstraintNode>().ToDictionary(static constraint => constraint.Name!);

        CollectionAssert.AreEqual(new[] { "a" }, constraints["t_pk"].IncludeColumns.ToArray());
        CollectionAssert.AreEqual(new[] { "B", "id" }, constraints["t_u"].IncludeColumns.ToArray());
        CollectionAssert.AreEqual(new[] { "a" }, constraints["t_x"].IncludeColumns.ToArray());
    }

    [TestMethod]
    public void AConstraintWithoutIncludeHasNone()
    {
        Assert.IsEmpty(new PgQueryParser().Parse("CREATE TABLE t (id int PRIMARY KEY);").Nodes.OfType<ConstraintNode>().Single().IncludeColumns);
    }
}
