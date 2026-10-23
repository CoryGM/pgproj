namespace PgProj.Parser.Tests;

/// <summary>
/// G-90 part 4: a <c>CREATE TYPE</c>'s structure is read by the parser: enum labels from pg_query's tree, and a composite field's type
/// and a range option's value as written, sliced with the tokenizer that skips comments and strings. The body used to be split on commas
/// character by character, so a comment or a comma inside a label or a quoted name broke it apart.
/// </summary>
[TestClass]
public sealed class TypeBodyParsingTests
{
    [TestMethod]
    public void EnumLabelsComeFromTheTree()
    {
        var type = Single("CREATE TYPE public.m AS ENUM ('a', -- first, really\n 'b, c', /* 'x' */ 'd''e');");

        Assert.AreEqual("enum", type.Form);
        CollectionAssert.AreEqual(new[] { "a", "b, c", "d'e" }, type.EnumLabels.ToArray());
    }

    [TestMethod]
    public void CompositeFieldTypesAreReadAsWrittenAroundComments()
    {
        var type = Single("""
            CREATE TYPE public.pt AS (
                x integer, -- the x, y pair
                y numeric(10, 2)[] /* second, too */,
                "Label, with comma" varchar(10),
                iv interval day to second
            );
            """);

        Assert.AreEqual("composite", type.Form);
        CollectionAssert.AreEqual(new[] { "x", "y", "Label, with comma", "iv" }, type.CompositeFieldTypes.Select(static field => field.Name).ToArray());
        CollectionAssert.AreEqual(
            new[] { "integer", "numeric(10, 2)[]", "varchar(10)", "interval day to second" },
            type.CompositeFieldTypes.Select(static field => field.RawType).ToArray());
    }

    [TestMethod]
    public void RangeOptionsAreReadAsWritten()
    {
        var type = Single("CREATE TYPE public.r AS RANGE (subtype = float8, -- the subtype, then its diff\n subtype_diff = float8mi);");

        Assert.AreEqual("range", type.Form);
        CollectionAssert.AreEqual(
            new[] { "subtype=float8", "subtype_diff=float8mi" },
            type.RangeOptions!.Select(static option => $"{option.Key}={option.Value}").ToArray());
    }

    [TestMethod]
    public void AShellOrBaseTypeIsNotAType()
    {
        // Replaces AShellTypeHasNoForm: a shell or base type is no longer a type with no form, it is not modeled (G-241).
        Assert.IsEmpty(new PgQueryParser().Parse("CREATE TYPE public.shell_type;").Types);
        Assert.IsEmpty(new PgQueryParser().Parse("CREATE TYPE public.b (INPUT = public.bin, OUTPUT = public.bout);").Types);
    }

    private static TypeNode Single(string sql) => new PgQueryParser().Parse(sql).Types.Single();
}
