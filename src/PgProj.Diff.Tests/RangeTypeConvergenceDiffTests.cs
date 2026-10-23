using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-115: the live model reads every range option, each named with its schema (<c>pg_catalog.float8mi</c>, <c>pg_catalog."C"</c>) and the
/// subtype as <c>format_type</c> prints it. Against a project that writes them any other way PostgreSQL accepts, it must be no change; a real
/// change to any option must still be one.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RangeTypeConvergenceDiffTests
{
    [TestMethod]
    [DataRow("subtype = float8, subtype_diff = float8mi", "SUBTYPE = double precision, SUBTYPE_DIFF = pg_catalog.float8mi")]
    [DataRow("subtype = int4", "SUBTYPE = integer")]
    [DataRow("subtype = numeric(10,2)", "SUBTYPE = numeric")]
    [DataRow("subtype = int4[]", "SUBTYPE = integer[]")]
    [DataRow("subtype = text, collation = \"C\"", "SUBTYPE = text, COLLATION = pg_catalog.\"C\"")]
    [DataRow("subtype = text, subtype_opclass = text_pattern_ops", "SUBTYPE = text, SUBTYPE_OPCLASS = pg_catalog.text_pattern_ops")]
    [DataRow("subtype = float8, subtype_diff = public.fdiff", "SUBTYPE = double precision, SUBTYPE_DIFF = public.fdiff")]
    [DataRow("subtype = float8, subtype_diff = fdiff", "SUBTYPE = double precision, SUBTYPE_DIFF = public.fdiff")]
    public void TheLiveSpellingOfARangeIsNoChange(string project, string live)
    {
        var result = new DiffEngine().Execute(Build(live), Build(project));

        Assert.IsEmpty(result.ModifiedTypes, string.Join(" | ", result.ModifiedTypes.SelectMany(static change => change.Details).Select(static detail => $"{detail.OldValue} -> {detail.NewValue}")));
    }

    [TestMethod]
    [DataRow("subtype = float8", "SUBTYPE = double precision, SUBTYPE_DIFF = pg_catalog.float8mi")]
    [DataRow("subtype = int8", "SUBTYPE = integer")]
    [DataRow("subtype = text, collation = \"POSIX\"", "SUBTYPE = text, COLLATION = pg_catalog.\"C\"")]
    [DataRow("subtype = float8, subtype_diff = other.fdiff", "SUBTYPE = double precision, SUBTYPE_DIFF = public.fdiff")]
    public void ARealChangeIsStillOne(string project, string live)
        => Assert.HasCount(1, new DiffEngine().Execute(Build(live), Build(project)).ModifiedTypes);

    [TestMethod]
    public void AQuotedOptionIsKeptAsWrittenForDeployment()
        => Assert.AreEqual("\"C\"", Build("subtype = text, collation = \"C\"").Types["public.r"].RangeOptions!.CollationName);

    private static DatabaseModel Build(string options)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse($"CREATE TYPE public.r AS RANGE ({options});")]);
}
