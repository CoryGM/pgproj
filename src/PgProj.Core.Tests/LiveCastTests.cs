using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core.Tests;

/// <summary>
/// G-131: a live database's user-defined casts are written to the snapshot and to <c>init</c>'s scaffold as <c>CREATE CAST</c>, in a file of their own at
/// the top, and read back as casts.
/// </summary>
[TestClass]
public sealed class LiveCastTests
{
    private static readonly LiveTypeSnapshot Mood = new("public", "mood", "enum", "CREATE TYPE public.mood AS ENUM ('a', 'b');");

    private static readonly LiveFunctionSnapshot Function = new(
        "public",
        "text_to_mood",
        "CREATE OR REPLACE FUNCTION public.text_to_mood(text) RETURNS public.mood LANGUAGE sql AS $$ SELECT $1::public.mood $$",
        IdentityArguments: "text");

    [TestMethod]
    [DataRow("e", "f", "CREATE CAST (text AS mood) WITH FUNCTION public.text_to_mood(text);")]
    [DataRow("a", "f", "CREATE CAST (text AS mood) WITH FUNCTION public.text_to_mood(text) AS ASSIGNMENT;")]
    [DataRow("i", "f", "CREATE CAST (text AS mood) WITH FUNCTION public.text_to_mood(text) AS IMPLICIT;")]
    public void ACastWithAFunctionIsWrittenWithItsContext(string context, string method, string expected)
    {
        Assert.AreEqual(expected, LiveDatabaseModelLoader.BuildCreateCastStatement(new LiveCastSnapshot("text", "mood", "public", "text_to_mood", "text", context, method)));
    }

    [TestMethod]
    [DataRow("b", "CREATE CAST (a AS b) WITHOUT FUNCTION AS IMPLICIT;")]
    [DataRow("i", "CREATE CAST (a AS b) WITH INOUT AS IMPLICIT;")]
    public void WithoutFunctionAndWithInOutHaveNoFunction(string method, string expected)
    {
        Assert.AreEqual(expected, LiveDatabaseModelLoader.BuildCreateCastStatement(new LiveCastSnapshot("a", "b", null, null, string.Empty, "i", method)));
    }

    [TestMethod]
    public void ACastIsReadBackWithEveryPartItWasWrittenWith()
    {
        // Act
        var model = Load(new LiveCastSnapshot("text", "mood", "public", "text_to_mood", "text", "a", "f"));

        // Assert
        var cast = model.Casts["cast:(text AS mood)"];
        Assert.AreEqual("function", cast.Method);
        Assert.AreEqual("assignment", cast.Context);
        Assert.AreEqual("public.text_to_mood", cast.FunctionName);
        CollectionAssert.AreEqual(new[] { "text" }, cast.NormalizedFunctionArguments.ToArray());
        Assert.AreEqual(0, model.Diagnostics.Count, string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
    }

    [TestMethod]
    public void TheScaffoldPutsEachCastInAFileOfItsOwnAtTheTop()
    {
        // Act
        var files = LiveDatabaseModelLoader.BuildScaffoldFiles(
            [new LiveSchemaSnapshot("public")], [], [], [], [], [Function], [Mood], [], [], [], [],
            casts: [new LiveCastSnapshot("text", "mood", "public", "text_to_mood", "text", "a", "f")]);

        // Assert
        var file = files.Single(static f => f.RelativePath == "Cast/text_to_mood.sql");
        StringAssert.Contains(file.Content, "CREATE CAST (text AS mood) WITH FUNCTION public.text_to_mood(text) AS ASSIGNMENT;");
        Assert.IsEmpty(new LiveDatabaseModelLoader(new PgQueryParser(), new SemanticBuilder()).FindUnparseableFiles([file]));
    }

    [TestMethod]
    public void ADatabaseWithoutCastsScaffoldsNoCastFile()
    {
        // Act
        var files = LiveDatabaseModelLoader.BuildScaffoldFiles([new LiveSchemaSnapshot("public")], [], [], [], [], [Function], [Mood], [], [], [], []);

        // Assert
        Assert.IsFalse(files.Any(static f => f.RelativePath.StartsWith("Cast", StringComparison.Ordinal)));
    }

    private static DatabaseModel Load(LiveCastSnapshot cast)
    {
        var sql = LiveDatabaseModelLoader.GenerateSnapshotSql(
            [new LiveSchemaSnapshot("public")], [], [], [], functions: [Function], types: [Mood], casts: [cast]);
        return new LiveDatabaseModelLoader(new PgQueryParser(), new SemanticBuilder()).BuildModelFromSnapshotSql(sql);
    }
}
