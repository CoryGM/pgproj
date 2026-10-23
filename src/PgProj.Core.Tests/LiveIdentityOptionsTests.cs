namespace PgProj.Core.Tests;

/// <summary>
/// G-214: a live identity's sequence is written as its column's options, only those that are not PostgreSQL's defaults, and not as a sequence of its
/// own; <c>init</c> wrote a <c>CREATE SEQUENCE ... OWNED BY</c> beside the identity column and lost its options.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class LiveIdentityOptionsTests
{
    private static LiveSequenceSnapshot Identity(string dataType, string start, string minimum, string maximum, string increment, bool cycle = false, string cache = "1")
        => new("public", "t_id_seq", dataType, start, minimum, maximum, increment, cycle, cache, "public", "t", "id") { IsIdentity = true };

    [TestMethod]
    public void DefaultOptionsAreNotWritten()
    {
        Assert.IsNull(Identity("integer", "1", "1", "2147483647", "1").IdentityOptionsText);
        Assert.IsNull(Identity("bigint", "1", "1", "9223372036854775807", "1").IdentityOptionsText);
    }

    [TestMethod]
    public void ASequenceThatIsNotLikeItsTableIsSaidToBeLoggedOrUnlogged()
    {
        var unlogged = Identity("integer", "1", "1", "2147483647", "1") with { IsUnlogged = true };

        Assert.AreEqual("UNLOGGED", unlogged.IdentityOptionsTextFor(tableIsUnlogged: false));
        Assert.IsNull(unlogged.IdentityOptionsTextFor(tableIsUnlogged: true), "an identity follows its table");
        Assert.AreEqual("LOGGED", Identity("integer", "1", "1", "2147483647", "1").IdentityOptionsTextFor(tableIsUnlogged: true));
        Assert.AreEqual("START WITH 5 UNLOGGED", (Identity("integer", "5", "1", "2147483647", "1") with { IsUnlogged = true }).IdentityOptionsTextFor(tableIsUnlogged: false));
    }

    [TestMethod]
    public void OptionsThatAreNotTheDefaultsAreWritten()
    {
        Assert.AreEqual(
            "START WITH 100 INCREMENT BY 5 MINVALUE 10 MAXVALUE 100000 CACHE 20 CYCLE",
            Identity("bigint", "100", "10", "100000", "5", cycle: true, cache: "20").IdentityOptionsText);
        Assert.AreEqual("INCREMENT BY -1", Identity("smallint", "-1", "-32768", "-1", "-1").IdentityOptionsText);
    }

    [TestMethod]
    public void AnIdentitySequenceIsWrittenOnItsColumnAndNotOnItsOwn()
    {
        var plain = new LiveSequenceSnapshot("public", "s", "bigint", "1", "1", "9223372036854775807", "1", false, "1", null, null, null);
        var (sequences, tables) = LiveDatabaseModelLoader.AttachIdentitySequences(
            [Identity("integer", "10", "1", "2147483647", "1"), plain],
            [new LiveTableSnapshot("public", "t", [new LiveColumnSnapshot("id", "integer", false, null, false, null, null, IdentityGeneration: "ALWAYS")])]);

        CollectionAssert.AreEqual(new[] { "s" }, sequences.Select(static sequence => sequence.SequenceName).ToArray());
        var sql = LiveDatabaseModelLoader.GenerateSnapshotSql([new LiveSchemaSnapshot("public")], tables, [], [], sequences: sequences);
        StringAssert.Contains(sql, "id integer GENERATED ALWAYS AS IDENTITY (START WITH 10)");
        Assert.IsFalse(sql.Contains("t_id_seq", StringComparison.Ordinal), sql);

        var files = LiveDatabaseModelLoader.BuildScaffoldFiles([new LiveSchemaSnapshot("public")], tables, [], [], [], [], [], [], sequences, [], []);
        Assert.IsFalse(files.Any(static file => file.Content.Contains("t_id_seq", StringComparison.Ordinal)), string.Join("\n", files.Select(static file => file.Content)));
        StringAssert.Contains(files.Single(static file => file.RelativePath.EndsWith("/t.sql", StringComparison.Ordinal)).Content, "GENERATED ALWAYS AS IDENTITY (START WITH 10)");
    }
}
