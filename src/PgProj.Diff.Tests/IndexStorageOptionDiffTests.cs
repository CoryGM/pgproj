using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-195: the storage parameters of an index and of a key or exclusion constraint's index are compared as an unordered list of <c>name=value</c>. The spacing
/// around <c>=</c>, the order, the case of the name, and the quotes the database prints around a value (<c>fillfactor='70'</c>) are not differences; a changed,
/// added, or removed option is. An option the database has and the project does not state is a difference too (the user's decision: it is reset).
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class IndexStorageOptionDiffTests
{
    private const string Table = "CREATE TABLE public.t (a int, b int);";

    [TestMethod]
    [DataRow("CREATE INDEX t_a ON public.t (a) WITH (fillfactor = 70, deduplicate_items = off);", "CREATE INDEX t_a ON public.t USING btree (a) WITH (fillfactor='70', deduplicate_items='off')")]
    [DataRow("CREATE INDEX t_a ON public.t (a) WITH (fillfactor = 70, deduplicate_items = off);", "CREATE INDEX t_a ON public.t (a) WITH (DEDUPLICATE_ITEMS=off, fillfactor=70)")]
    [DataRow("CREATE INDEX t_a ON public.t (a);", "CREATE INDEX t_a ON public.t USING btree (a)")]
    public void TheSameIndexOptionsAreNoChange(string project, string live)
    {
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(Build(Table + live), Build(Table + project))));
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(Build(Table + project), Build(Table + project))));
    }

    [TestMethod]
    [DataRow("CREATE INDEX t_a ON public.t (a) WITH (fillfactor = 70);", "CREATE INDEX t_a ON public.t (a) WITH (fillfactor = 80);")]
    [DataRow("CREATE INDEX t_a ON public.t (a);", "CREATE INDEX t_a ON public.t (a) WITH (fillfactor = 80);")]
    [DataRow("CREATE INDEX t_a ON public.t (a) WITH (fillfactor = 70);", "CREATE INDEX t_a ON public.t (a);")]
    public void AChangedIndexOptionIsAChange(string before, string after)
    {
        var diff = new DiffEngine().Diff(Build(Table + before), Build(Table + after));

        Assert.IsTrue(diff.Modified.Any(static change => change.NewNode is IndexDefinition && change.Details.Any(static detail => detail.PropertyName == nameof(IndexDefinition.StorageOptions))));
    }

    [TestMethod]
    [DataRow("ALTER TABLE public.t ADD CONSTRAINT t_pk PRIMARY KEY (a) WITH (fillfactor = 80);", "ALTER TABLE public.t ADD CONSTRAINT t_pk PRIMARY KEY (a) WITH (fillfactor='80');")]
    [DataRow("ALTER TABLE public.t ADD CONSTRAINT t_u UNIQUE (a, b) WITH (fillfactor=60);", "ALTER TABLE public.t ADD CONSTRAINT t_u UNIQUE (b, a) WITH (fillfactor = '60');")]
    public void TheSameConstraintOptionsAreNoChange(string project, string live)
    {
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(Build(Table + live), Build(Table + project))));
    }

    [TestMethod]
    [DataRow("ALTER TABLE public.t ADD CONSTRAINT t_pk PRIMARY KEY (a) WITH (fillfactor = 80);", "ALTER TABLE public.t ADD CONSTRAINT t_pk PRIMARY KEY (a) WITH (fillfactor = 90);")]
    [DataRow("ALTER TABLE public.t ADD CONSTRAINT t_pk PRIMARY KEY (a);", "ALTER TABLE public.t ADD CONSTRAINT t_pk PRIMARY KEY (a) WITH (fillfactor = 90);")]
    [DataRow("ALTER TABLE public.t ADD CONSTRAINT t_u UNIQUE (a) WITH (fillfactor = 80);", "ALTER TABLE public.t ADD CONSTRAINT t_u UNIQUE (a);")]
    public void AChangedConstraintOptionIsAChange(string before, string after)
    {
        var diff = new DiffEngine().Diff(Build(Table + before), Build(Table + after));

        Assert.IsTrue(diff.Modified.Any(static change => change.Details.Any(static detail => detail.PropertyName == nameof(ConstraintDefinition.IndexStorageOptions))));
    }

    private static bool IsEmpty(ChangeSet diff) => diff.Added.Count + diff.Removed.Count + diff.Modified.Count == 0;

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
