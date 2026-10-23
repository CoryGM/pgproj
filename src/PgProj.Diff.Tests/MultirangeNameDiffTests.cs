using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-204: a range's multirange is compared by its effective name: the one stated, or PostgreSQL's default. Stating the default is no change, and
/// the live database's schema-qualified form is the same name; a different name is a change, both ways.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class MultirangeNameDiffTests
{
    private const string R = "CREATE TYPE floatrange AS RANGE (subtype = float8{0});";

    [TestMethod]
    [DataRow("", ", multirange_type_name = floatmultirange", DisplayName = "the default stated")]
    [DataRow(", multirange_type_name = floatmulti", ", MULTIRANGE_TYPE_NAME = public.floatmulti", DisplayName = "PostgreSQL's qualified form")]
    public void TheSameMultirangeIsNoChange(string one, string other)
    {
        Assert.AreEqual(0, ModifiedCount(string.Format(R, one), string.Format(R, other)));
        Assert.AreEqual(0, ModifiedCount(string.Format(R, other), string.Format(R, one)));
    }

    [TestMethod]
    [DataRow("", ", multirange_type_name = floatmulti")]
    [DataRow(", multirange_type_name = floatmulti", ", multirange_type_name = floatmany")]
    public void ADifferentMultirangeIsAChangeBothWays(string one, string other)
    {
        Assert.AreEqual(1, ModifiedCount(string.Format(R, one), string.Format(R, other)));
        Assert.AreEqual(1, ModifiedCount(string.Format(R, other), string.Format(R, one)));
    }

    private static int ModifiedCount(string oldSql, string newSql)
        => new DiffEngine().Diff(Build(oldSql), Build(newSql)).Modified.Count;

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
