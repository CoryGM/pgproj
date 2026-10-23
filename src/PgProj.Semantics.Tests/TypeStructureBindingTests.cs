using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-90 part 4: a type's structure in the model (composite fields, enum labels, range options) comes from what the parser read, so a comment
/// or a comma inside the body no longer drops a label or splits a field. And an expression whose tree names nothing (<c>ARRAY[]::int[]</c>)
/// is not read again as text for names.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class TypeStructureBindingTests
{
    [TestMethod]
    public void ACommentInsideTheBodyChangesNothing()
    {
        var model = Build("""
            CREATE TYPE public.pt AS (
                x integer, -- the x, y pair
                y integer /* second, too */
            );
            CREATE TYPE public.m AS ENUM ('a', -- first
                'b, c', 'd''e');
            """);

        var point = model.Types["public.pt"];
        CollectionAssert.AreEqual(new[] { "x integer", "y integer" }, point.CompositeFields.Select(static field => $"{field.Name} {field.RawType}").ToArray());
        CollectionAssert.AreEqual(new[] { "a", "b, c", "d'e" }, model.Types["public.m"].EnumLabels.ToArray());
        AssertNoDiagnostics(model);
    }

    [TestMethod]
    public void ACompositeFieldReferencesItsType()
    {
        var model = Build("CREATE TYPE public.mood AS ENUM ('ok'); CREATE TYPE public.pt AS (m public.mood, n varchar(10));");

        CollectionAssert.Contains(model.Types["public.pt"].ReferencedTypes.ToList(), "public.mood");
        AssertNoDiagnostics(model);
    }

    [TestMethod]
    public void RangeOptionsAreMapped()
    {
        var model = Build("CREATE TYPE public.r AS RANGE (subtype = float8, -- comment\n subtype_diff = float8mi);");

        var options = model.Types["public.r"].RangeOptions!;
        Assert.AreEqual("float8", options.Subtype);
        Assert.AreEqual("float8mi", options.SubtypeDiffFunction);
    }

    [TestMethod]
    public void AnExpressionThatNamesNothingIsNotReadAsText()
        => AssertNoDiagnostics(Build("CREATE TABLE public.t (a int[] DEFAULT ARRAY[]::int[], b int CHECK (b IS NOT NULL));"));

    private static void AssertNoDiagnostics(DatabaseModel model)
        => Assert.IsEmpty(model.Diagnostics, string.Join(" | ", model.Diagnostics.Select(static diagnostic => diagnostic.Message)));

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
