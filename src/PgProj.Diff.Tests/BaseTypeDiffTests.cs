using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-267: a base type's options compared in their comparison form: the same type written another way is no change (the defaults stated or not, a type or function
/// spelled another way), every option that differs is reported under the one property, and <c>LIKE</c> against the four values it copies is a difference between two
/// projects (the server resolves it against a database).
/// </summary>
[TestClass]
public sealed class BaseTypeDiffTests
{
    private const string Functions = """
        CREATE FUNCTION public.t_in(cstring) RETURNS public.t LANGUAGE internal IMMUTABLE STRICT AS $$int4in$$;
        CREATE FUNCTION public.t_out(public.t) RETURNS cstring LANGUAGE internal IMMUTABLE STRICT AS $$int4out$$;

        """;

    private const string Plain = "CREATE TYPE public.t (INPUT = public.t_in, OUTPUT = public.t_out, INTERNALLENGTH = 4, PASSEDBYVALUE, ALIGNMENT = int4);";

    [TestMethod]
    [DataRow(Plain)]
    [DataRow("CREATE TYPE public.t (INPUT = t_in, OUTPUT = t_out, LIKE = integer);")]
    public void ATypeAgainstItselfIsNoChange(string type)
    {
        Assert.IsEmpty(new DiffEngine().Diff(Build(type), Build(type)).Modified);
    }

    [TestMethod]
    [DataRow("CREATE TYPE public.t (output = t_out, input = t_in, passedbyvalue = true, alignment = int4, internallength = 4, storage = plain);")]
    [DataRow("CREATE TYPE public.t (INPUT = public.t_in, OUTPUT = public.t_out, INTERNALLENGTH = 4, PASSEDBYVALUE, ALIGNMENT = int4, CATEGORY = 'U', PREFERRED = false, DELIMITER = ',', COLLATABLE = false);")]
    public void ACosmeticDifferenceIsNoChange(string other)
    {
        Assert.IsEmpty(new DiffEngine().Diff(Build(Plain), Build(other)).Modified);
        Assert.IsEmpty(new DiffEngine().Diff(Build(other), Build(Plain)).Modified);
    }

    [TestMethod]
    [DataRow("INTERNALLENGTH = 4", "INTERNALLENGTH = 8", "INTERNALLENGTH = 8")]
    [DataRow("PASSEDBYVALUE", "PASSEDBYVALUE = false", "PASSEDBYVALUE = false")]
    [DataRow("ALIGNMENT = int4", "ALIGNMENT = double", "ALIGNMENT = double")]
    [DataRow("ALIGNMENT = int4", "ALIGNMENT = int4, STORAGE = main", "STORAGE = main")]
    [DataRow("ALIGNMENT = int4", "ALIGNMENT = int4, DEFAULT = '1'", "DEFAULT = '1'")]
    [DataRow("ALIGNMENT = int4", "ALIGNMENT = int4, COLLATABLE = true", "COLLATABLE = true")]
    [DataRow("OUTPUT = public.t_out", "OUTPUT = other.t_out", "OUTPUT = other.t_out")]
    public void AGenuineDifferenceIsReported(string find, string replace, string reported)
    {
        var changed = Plain.Replace(find, replace, StringComparison.Ordinal);
        var detail = new DiffEngine().Diff(Build(Plain), Build(changed)).Modified.Single().Details.Single();

        Assert.AreEqual(nameof(TypeDefinition.BaseTypeOptions), detail.PropertyName);
        StringAssert.Contains(detail.NewValue, reported);
    }

    [TestMethod]
    public void LikeAndTheValuesItCopiesDifferBetweenTwoProjects()
    {
        // Against a database, ServerBaseTypeLikeResolver turns LIKE into the values; between two projects it is compared as written.
        Assert.HasCount(1, new DiffEngine().Diff(Build(Plain), Build("CREATE TYPE public.t (INPUT = t_in, OUTPUT = t_out, LIKE = integer);")).Modified);
    }

    private static DatabaseModel Build(string type) => new SemanticPipeline().Execute([new PgQueryParser().Parse(Functions + type)]);
}
