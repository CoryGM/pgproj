using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-99: a composite type's fields are compared on their parsed, canonical type, as a column's type is, so a field written
/// <c>varchar(10)</c> and read back from the server as <c>character varying(10)</c> is one type, while a genuine change still is
/// one. The live side here is written the way <c>format_type</c> spells each type.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class CompositeFieldTypeDiffTests
{
    [TestMethod]
    [DataRow("zip varchar(10)", "zip character varying(10)")]
    [DataRow("code char(3)", "code character(3)")]
    [DataRow("n int", "n integer")]
    [DataRow("ts timestamptz", "ts timestamp with time zone")]
    [DataRow("arr int[]", "arr integer[]")]
    [DataRow("vc varchar", "vc character varying")]
    [DataRow("amt numeric(10,2)", "amt numeric(10,2)")]
    [DataRow("n numeric(5)", "n numeric(5,0)")]
    [DataRow("c public.citext", "c citext")]
    public void AFieldSpelledAsPostgresPrintsItIsNoChange(string projectField, string liveField)
    {
        var result = new DiffEngine().Execute(Build(liveField), Build(projectField));

        Assert.IsEmpty(result.ModifiedTypes, Describe(result));
    }

    [TestMethod]
    [DataRow("zip varchar(10)", "zip character varying(20)")]
    [DataRow("n int", "n bigint")]
    [DataRow("amt numeric(10,2)", "amt numeric(12,2)")]
    [DataRow("arr int[]", "arr integer")]
    [DataRow("street text", "road text")]
    [DataRow("e s.st", "e text")]
    public void ARealFieldChangeIsStillReported(string projectField, string liveField)
    {
        var result = new DiffEngine().Execute(Build(liveField), Build(projectField));

        Assert.HasCount(1, result.ModifiedTypes);
        Assert.IsTrue(result.ModifiedTypes[0].Details.Any(static detail => detail.PropertyName == nameof(TypeDefinition.CompositeFields)), Describe(result));
    }

    [TestMethod]
    public void AnAddedFieldIsStillReported()
    {
        var result = new DiffEngine().Execute(Build("zip character varying(10)"), Build("zip varchar(10), extra text"));

        Assert.HasCount(1, result.ModifiedTypes);
    }

    private static string Describe(DiffResult result)
        => string.Join(Environment.NewLine, result.ModifiedTypes.SelectMany(change =>
            change.Details.Select(detail => $"{change.NodeId}.{detail.PropertyName}: '{detail.OldValue}' -> '{detail.NewValue}'")));

    private static DatabaseModel Build(string field)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(
            $"CREATE SCHEMA s; CREATE TYPE s.st AS ENUM ('a'); CREATE EXTENSION IF NOT EXISTS citext; CREATE TYPE s.addr AS (x text, {field});")]);
}
