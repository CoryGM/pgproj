namespace PgProj.Core.Tests;

/// <summary>
/// G-195: <c>pg_get_constraintdef</c> does not print a primary key's or unique constraint's <c>WITH (...)</c>, so the live loader adds its index's options where
/// the grammar puts them: after the columns and any <c>INCLUDE</c>, before <c>USING INDEX TABLESPACE</c> and the deferrability.
/// </summary>
[TestClass]
public sealed class ConstraintIndexOptionsLiveLoaderTests
{
    [TestMethod]
    [DataRow("PRIMARY KEY (id)", "fillfactor=80", "PRIMARY KEY (id) WITH (fillfactor=80)")]
    [DataRow("UNIQUE NULLS NOT DISTINCT (a) INCLUDE (b)", "fillfactor=60, deduplicate_items=off", "UNIQUE NULLS NOT DISTINCT (a) INCLUDE (b) WITH (fillfactor=60, deduplicate_items=off)")]
    [DataRow("UNIQUE (a) DEFERRABLE INITIALLY DEFERRED", "fillfactor=60", "UNIQUE (a) WITH (fillfactor=60) DEFERRABLE INITIALLY DEFERRED")]
    [DataRow("PRIMARY KEY (id) USING INDEX TABLESPACE fast DEFERRABLE", "fillfactor=90", "PRIMARY KEY (id) WITH (fillfactor=90) USING INDEX TABLESPACE fast DEFERRABLE")]
    public void TheOptionsGoWhereTheGrammarPutsThem(string definition, string options, string expected)
    {
        Assert.AreEqual(expected, LiveDatabaseModelLoader.WithIndexOptions(definition, options));
    }

    [TestMethod]
    public void ADefinitionWithoutOptionsIsUnchanged()
    {
        Assert.AreEqual("PRIMARY KEY (id)", LiveDatabaseModelLoader.WithIndexOptions("PRIMARY KEY (id)", null));
        Assert.AreEqual("PRIMARY KEY (id)", LiveDatabaseModelLoader.WithIndexOptions("PRIMARY KEY (id)", string.Empty));
    }
}
