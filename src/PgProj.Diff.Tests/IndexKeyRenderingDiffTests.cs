using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-344: an index's expression keys are compared by PostgreSQL's rendering of each side when both have one, as a predicate is (G-100): an untyped literal
/// compared with an enum is stored with its cast, so the project's <c>c = 'r'</c> and the database's <c>c = 'r'::color</c> are the same key, while another
/// expression is still a change. Without renderings (two projects, or no server) the keys are compared as before.
/// </summary>
[TestClass]
public sealed class IndexKeyRenderingDiffTests
{
    private const string Types = "CREATE TYPE public.color AS ENUM ('r', 'g'); CREATE TABLE public.t (id int, c public.color);\n";

    [TestMethod]
    public void TheSameRenderingIsNoChangeWhateverTheTextSays()
    {
        var live = Rendered(Build(Types + "CREATE INDEX t_c_r ON public.t ((c = 'r'::public.color));"), "(c = 'r'::color)");
        var project = Rendered(Build(Types + "CREATE INDEX t_c_r ON public.t ((c = 'r'));"), "(c = 'r'::color)");

        Assert.IsEmpty(new DiffEngine().Diff(live, project).Modified);
    }

    [TestMethod]
    public void AnotherRenderingIsAChangedKey()
    {
        var live = Rendered(Build(Types + "CREATE INDEX t_c_r ON public.t ((c = 'r'));"), "(c = 'r'::color)");
        var project = Rendered(Build(Types + "CREATE INDEX t_c_r ON public.t ((c = 'g'));"), "(c = 'g'::color)");

        var detail = Index(new DiffEngine().Diff(live, project)).Single().Details.Single();
        Assert.AreEqual(nameof(IndexDefinition.KeyElements), detail.PropertyName);
    }

    [TestMethod]
    public void WithoutBothRenderingsTheTextIsCompared()
    {
        var typed = Build(Types + "CREATE INDEX t_c_r ON public.t ((c = 'r'::public.color));");
        var untyped = Build(Types + "CREATE INDEX t_c_r ON public.t ((c = 'r'));");

        Assert.HasCount(1, Index(new DiffEngine().Diff(typed, untyped)), "Two projects, no server: the text differs.");
        Assert.HasCount(1, Index(new DiffEngine().Diff(Rendered(typed, "(c = 'r'::color)"), untyped)), "Only one side rendered.");
    }

    [TestMethod]
    public void AColumnKeyBesideAnExpressionIsComparedAsBefore()
    {
        var live = Rendered(Build(Types + "CREATE INDEX t_c_r ON public.t (id, (c = 'r'));"), "id", "(c = 'r'::color)");
        var project = Rendered(Build(Types + "CREATE INDEX t_c_r ON public.t (id DESC, (c = 'r'));"), "id", "(c = 'r'::color)");

        Assert.HasCount(1, Index(new DiffEngine().Diff(live, project)), "The column key's order still counts.");
    }

    // A changed index is listed under its table too; these are the index's own changes.
    private static ModifiedChange[] Index(ChangeSet changes) => [.. changes.Modified.Where(static change => change.NodeKind == ChangeNodeKind.Index)];

    private static DatabaseModel Rendered(DatabaseModel model, params string?[] keys)
    {
        model.Indexes = model.Indexes.ToDictionary(static pair => pair.Key, pair => pair.Value with { ServerRenderedKeys = keys }, PgProj.Parser.SqlIdentifiers.NameComparer);
        return model;
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
