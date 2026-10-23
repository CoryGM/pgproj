namespace PgProj.Parser.Tests;

/// <summary>
/// G-195: the <c>WITH (...)</c> storage parameters of an index, and of a primary key's, unique constraint's, or exclusion constraint's index, are read as written,
/// as a table's are (G-24b). Before, they were not read at all, so they were never deployed or compared.
/// </summary>
[TestClass]
public sealed class IndexStorageOptionParsingTests
{
    [TestMethod]
    public void AnIndexsOptionsAreReadAsWritten()
    {
        var index = new PgQueryParser().Parse("CREATE INDEX t_a ON t (a) WITH (fillfactor = 70, deduplicate_items=off) WHERE a > 0;").Indexes.Single();

        CollectionAssert.AreEqual(new[] { "fillfactor = 70", "deduplicate_items=off" }, index.StorageOptions.Select(static option => option.RawText).ToArray());
        CollectionAssert.AreEqual(new[] { "fillfactor=70", "deduplicate_items=off" }, index.StorageOptions.Select(static option => option.UnquotedComparisonText).ToArray());
    }

    [TestMethod]
    public void TheDatabasesQuotedValuesCompareAsTheWrittenOnes()
    {
        var index = new PgQueryParser().Parse("CREATE INDEX t_a ON public.t USING btree (a) WITH (fillfactor='70', deduplicate_items='off')").Indexes.Single();

        CollectionAssert.AreEqual(new[] { "fillfactor=70", "deduplicate_items=off" }, index.StorageOptions.Select(static option => option.UnquotedComparisonText).ToArray());
    }

    [TestMethod]
    public void AnIndexWithoutOptionsHasNone()
    {
        Assert.IsEmpty(new PgQueryParser().Parse("CREATE INDEX t_a ON t (a);").Indexes.Single().StorageOptions);
    }

    [TestMethod]
    public void KeyAndExclusionConstraintsOptionsAreRead()
    {
        var result = new PgQueryParser().Parse("""
            CREATE TABLE t (id int, u int, r int4range,
                CONSTRAINT t_pk PRIMARY KEY (id) WITH (fillfactor = 80),
                CONSTRAINT t_u UNIQUE (u) WITH (fillfactor=60),
                CONSTRAINT t_x EXCLUDE USING gist (r WITH &&) WITH (fillfactor = 50));
            ALTER TABLE t ADD CONSTRAINT t_u2 UNIQUE (u, id) WITH (fillfactor='90');
            """);

        var options = result.Constraints.ToDictionary(static constraint => constraint.Name!, static constraint => string.Join(",", constraint.IndexStorageOptions.Select(static option => option.UnquotedComparisonText)));
        Assert.AreEqual("fillfactor=80", options["t_pk"]);
        Assert.AreEqual("fillfactor=60", options["t_u"]);
        Assert.AreEqual("fillfactor=50", options["t_x"]);
        Assert.AreEqual("fillfactor=90", options["t_u2"]);
    }
}
