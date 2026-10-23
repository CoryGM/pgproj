using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-195: an index's <c>WITH (...)</c>, and that of a key or exclusion constraint, is written as written, before a <c>WHERE</c>. A change to only the options (and
/// perhaps the comment) is made in place with <c>ALTER INDEX ... SET/RESET</c>, which does not rebuild the index or drop the constraint; any other change still
/// drops and creates.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class IndexStorageOptionDeployTests
{
    private const string Table = "CREATE TABLE public.t (a int, b int, r int4range);";

    [TestMethod]
    public void AnIndexsOptionsAreWrittenBeforeItsPredicate()
    {
        var sql = Statements(Generate(Build(Table), Build(Table + "CREATE INDEX t_a ON public.t (a) INCLUDE (b) WITH (fillfactor = 70, deduplicate_items = off) WHERE a > 0;")));

        Assert.AreEqual("CREATE INDEX \"t_a\" ON \"public\".\"t\" USING btree (\"a\") INCLUDE (\"b\") WITH (fillfactor = 70, deduplicate_items = off) WHERE a > 0;", sql.Single());
    }

    [TestMethod]
    [DataRow("CONSTRAINT t_pk PRIMARY KEY (a) WITH (fillfactor = 80)", "PRIMARY KEY (\"a\") WITH (fillfactor = 80)")]
    [DataRow("CONSTRAINT t_u UNIQUE (a, b) WITH (fillfactor=60)", "UNIQUE (\"a\", \"b\") WITH (fillfactor=60)")]
    [DataRow("CONSTRAINT t_x EXCLUDE USING gist (r WITH &&) WITH (fillfactor = 50) WHERE (a > 0)", "EXCLUDE USING gist (\"r\" WITH &&) WITH (fillfactor = 50) WHERE (a > 0)")]
    public void AConstraintsOptionsAreWritten(string declared, string expected)
    {
        var sql = string.Join("\n", Statements(Generate(Build(string.Empty), Build($"CREATE TABLE public.t (a int, b int, r int4range, {declared});"))));

        StringAssert.Contains(sql, expected);
    }

    [TestMethod]
    public void AChangedIndexOptionIsSetInPlace()
    {
        var sql = Statements(Generate(
            Build(Table + "CREATE INDEX t_a ON public.t (a) WITH (fillfactor = 70, deduplicate_items = off);"),
            Build(Table + "CREATE INDEX t_a ON public.t (a) WITH (fillfactor = 50, deduplicate_items = off);")));

        Assert.AreEqual("ALTER INDEX \"public\".\"t_a\" SET (fillfactor = 50);", sql.Single());
    }

    [TestMethod]
    public void ARemovedIndexOptionIsReset_AndAnAddedOneSet()
    {
        var sql = Statements(Generate(
            Build(Table + "CREATE INDEX t_a ON public.t (a) WITH (fillfactor = 70);"),
            Build(Table + "CREATE INDEX t_a ON public.t (a) WITH (deduplicate_items = off);")));

        Assert.AreEqual("ALTER INDEX \"public\".\"t_a\" SET (deduplicate_items = off);\nALTER INDEX \"public\".\"t_a\" RESET (fillfactor);", sql.Single());
    }

    [TestMethod]
    public void AnOptionAndACommentChangeAreBothInPlace()
    {
        var sql = Statements(Generate(
            Build(Table + "CREATE INDEX t_a ON public.t (a);"),
            Build(Table + "CREATE INDEX t_a ON public.t (a) WITH (fillfactor = 60); COMMENT ON INDEX public.t_a IS 'hot';")));

        Assert.IsFalse(sql.Any(static statement => statement.StartsWith("DROP", StringComparison.Ordinal)), string.Join("\n", sql));
        StringAssert.Contains(string.Join("\n", sql), "ALTER INDEX \"public\".\"t_a\" SET (fillfactor = 60);");
        StringAssert.Contains(string.Join("\n", sql), "COMMENT ON INDEX");
    }

    [TestMethod]
    public void AnOptionWithAKeyChangeStillDropsAndCreates()
    {
        var sql = Statements(Generate(
            Build(Table + "CREATE INDEX t_a ON public.t (a);"),
            Build(Table + "CREATE INDEX t_a ON public.t (a, b) WITH (fillfactor = 60);")));

        Assert.IsTrue(sql.Any(static statement => statement.StartsWith("DROP INDEX", StringComparison.Ordinal)), string.Join("\n", sql));
        Assert.IsTrue(sql.Any(static statement => statement.Contains("WITH (fillfactor = 60)", StringComparison.Ordinal)), string.Join("\n", sql));
    }

    [TestMethod]
    public void AChangedConstraintOptionIsSetOnItsIndexInPlace()
    {
        var sql = Statements(Generate(
            Build(Table + "ALTER TABLE public.t ADD CONSTRAINT t_pk PRIMARY KEY (a) WITH (fillfactor = 80);"),
            Build(Table + "ALTER TABLE public.t ADD CONSTRAINT t_pk PRIMARY KEY (a) WITH (fillfactor = 90);")));

        Assert.AreEqual("ALTER INDEX \"public\".\"t_pk\" SET (fillfactor = 90);", sql.Single());
    }

    [TestMethod]
    public void AConstraintOptionTheProjectNoLongerStatesIsReset()
    {
        var sql = Statements(Generate(
            Build(Table + "ALTER TABLE public.t ADD CONSTRAINT t_u UNIQUE (a) WITH (fillfactor = 80);"),
            Build(Table + "ALTER TABLE public.t ADD CONSTRAINT t_u UNIQUE (a);")));

        Assert.AreEqual("ALTER INDEX \"public\".\"t_u\" RESET (fillfactor);", sql.Single());
    }

    private static List<string> Statements(OperationBatch batch)
        => batch.Operations
            .Where(static operation => operation is not SchemaCreateOperation)
            .Select(static operation => operation.GenerateSql().Trim())
            // A changed index also marks its table modified, which writes only this note (as for a comment-only index change).
            .Where(static sql => !sql.StartsWith("-- No column-level changes", StringComparison.Ordinal))
            .ToList();

    private static OperationBatch Generate(DatabaseModel oldModel, DatabaseModel newModel)
        => new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute(string.IsNullOrEmpty(sql) ? [] : [new PgQueryParser().Parse(sql)]);
}
