using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core.Tests;

/// <summary>
/// G-129: a live database's user-defined operators are written to the snapshot and to <c>init</c>'s scaffold as <c>CREATE OPERATOR</c>, in a file of their own
/// under the schema's <c>OPERATOR</c> folder, and read back as operators.
/// </summary>
[TestClass]
public sealed class LiveOperatorTests
{
    private static readonly LiveTypeSnapshot Mood = new("public", "mood", "enum", "CREATE TYPE public.mood AS ENUM ('a', 'b');");

    private static readonly LiveFunctionSnapshot Function = new(
        "public",
        "mood_eq",
        "CREATE OR REPLACE FUNCTION public.mood_eq(public.mood, public.mood) RETURNS boolean LANGUAGE sql AS $$ SELECT $1::text = $2::text $$",
        IdentityArguments: "mood, mood");

    private static readonly LiveOperatorSnapshot Equal = new(
        "public", "===", "mood", "mood", "public", "mood_eq", "public", "===", null, null, "eqsel", "eqjoinsel", true, false);

    [TestMethod]
    public void AnOperatorIsWrittenWithEveryOption()
    {
        var sql = LiveDatabaseModelLoader.BuildCreateOperatorStatement(Equal with { NegatorSchema = "pg_catalog", NegatorName = "<>", Merges = true });

        Assert.AreEqual(
            "CREATE OPERATOR public.=== (LEFTARG = mood, RIGHTARG = mood, FUNCTION = public.mood_eq, COMMUTATOR = OPERATOR(public.===), NEGATOR = OPERATOR(pg_catalog.<>), RESTRICT = eqsel, JOIN = eqjoinsel, HASHES, MERGES);",
            sql);
    }

    [TestMethod]
    public void APrefixOperatorHasNoLeftOperandAndNoOptionalParts()
    {
        var sql = LiveDatabaseModelLoader.BuildCreateOperatorStatement(new LiveOperatorSnapshot("app", "@@@", null, "text", "pg_catalog", "upper", null, null, null, null, null, null, false, false));

        Assert.AreEqual("CREATE OPERATOR app.@@@ (RIGHTARG = text, FUNCTION = pg_catalog.upper);", sql);
    }

    [TestMethod]
    public void AnOperatorIsReadBackWithEveryPartItWasWrittenWith()
    {
        // Act
        var model = Load(Equal);

        // Assert
        var definition = model.Operators["public.===(mood,mood)"];
        Assert.AreEqual("public.mood_eq", definition.FunctionName);
        Assert.AreEqual("public.===", definition.Commutator);
        Assert.AreEqual("eqsel", definition.RestrictFunction);
        Assert.AreEqual("eqjoinsel", definition.JoinFunction);
        Assert.IsTrue(definition.IsHashes);
        Assert.IsFalse(definition.IsMerges);
        Assert.AreEqual(0, model.Diagnostics.Count, string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
    }

    [TestMethod]
    public void TheScaffoldPutsEachOperatorInAFileOfItsOwnUnderItsSchema()
    {
        // Act
        var files = LiveDatabaseModelLoader.BuildScaffoldFiles(
            [new LiveSchemaSnapshot("public")], [], [], [], [], [Function], [Mood], [], [], [], [],
            operators: [Equal]);

        // Assert
        var file = files.Single(static f => f.RelativePath == "Schema/public/Operator/eq_eq_eq__mood__mood.sql");
        StringAssert.Contains(file.Content, "CREATE OPERATOR public.=== (");
        Assert.IsEmpty(new LiveDatabaseModelLoader(new PgQueryParser(), new SemanticBuilder()).FindUnparseableFiles([file]));
    }

    [TestMethod]
    public void ADatabaseWithoutOperatorsScaffoldsNoOperatorFile()
    {
        // Act
        var files = LiveDatabaseModelLoader.BuildScaffoldFiles([new LiveSchemaSnapshot("public")], [], [], [], [], [Function], [Mood], [], [], [], []);

        // Assert
        Assert.IsFalse(files.Any(static f => f.RelativePath.Contains("Operator", StringComparison.Ordinal)));
    }

    private static DatabaseModel Load(LiveOperatorSnapshot operatorSnapshot)
    {
        var sql = LiveDatabaseModelLoader.GenerateSnapshotSql(
            [new LiveSchemaSnapshot("public")], [], [], [], functions: [Function], types: [Mood], operators: [operatorSnapshot]);
        return new LiveDatabaseModelLoader(new PgQueryParser(), new SemanticBuilder()).BuildModelFromSnapshotSql(sql);
    }
}
