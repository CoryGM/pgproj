using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-204: a range type's <c>MULTIRANGE_TYPE_NAME</c> is modeled. A multirange is no type of the model's own, but one the project's range makes
/// is known: it is not a missing type, and a reference to it is a reference to its range.
/// </summary>
[TestClass]
public sealed class MultirangeTypesTests
{
    [TestMethod]
    [DataRow("floatrange", "floatmultirange")]
    [DataRow("range_of_x", "multirange_of_x")]
    [DataRow("spans", "spans_multirange")]
    [DataRow("Spans", "Spans_multirange")]
    public void TheDefaultNameIsPostgreSqls(string range, string multirange)
    {
        Assert.AreEqual(multirange, RangeTypeOptionsDefinition.DefaultMultirangeName(range));
    }

    [TestMethod]
    public void AStatedNameIsKeptAsWritten()
    {
        var model = Build("CREATE SCHEMA s; CREATE TYPE s.\"Spans\" AS RANGE (subtype = int4, multirange_type_name = s.\"Many Spans\");");

        Assert.AreEqual("s.\"Many Spans\"", model.Types.Values.Single().RangeOptions!.MultirangeTypeName);
    }

    [TestMethod]
    [DataRow("s.many spans", "s", true)]
    [DataRow("many spans", "s", true)]
    [DataRow("public.floatmulti", null, true)]
    [DataRow("floatmulti", "public", true)]
    [DataRow("fr_multirange", null, true)]
    [DataRow("s.floatmulti", null, false)]
    [DataRow("datemultirange", null, false)]
    public void AReferenceToAProjectRangesMultirangeIsFound(string reference, string? schema, bool found)
    {
        var model = Build("""
            CREATE SCHEMA s;
            CREATE TYPE s."Spans" AS RANGE (subtype = int4, multirange_type_name = s."Many Spans");
            CREATE TYPE floatrange AS RANGE (subtype = float8, multirange_type_name = floatmulti);
            CREATE TYPE fr AS RANGE (subtype = float8);
            """);

        Assert.AreEqual(found, MultirangeTypes.FindRange(model, schema, reference) is not null);
    }

    [TestMethod]
    public void AColumnOfAMultirangeIsNoMissingType()
    {
        var model = Build("CREATE TYPE fr AS RANGE (subtype = float8, multirange_type_name = frm); CREATE TABLE t (a frm, b fr_multirange_x);");

        var messages = model.Diagnostics.Select(static diagnostic => diagnostic.Message).ToArray();
        Assert.IsFalse(messages.Any(static message => message.Contains("'public.frm'", StringComparison.Ordinal) || message.Contains("'frm'", StringComparison.Ordinal)), string.Join("; ", messages));
        Assert.IsTrue(messages.Any(static message => message.Contains("fr_multirange_x", StringComparison.Ordinal)), "A type that is no multirange is still reported.");
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
