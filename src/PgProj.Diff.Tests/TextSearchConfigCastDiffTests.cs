using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-186: PostgreSQL stores the text search configuration named in an index expression as <c>'english'::regconfig</c>. The project writes <c>'english'</c>,
/// which the function's <c>regconfig</c> parameter makes the same value, so the two are one index; another configuration, column, or function is a difference.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class TextSearchConfigCastDiffTests
{
    private static string Sql(string key) => $"CREATE TABLE public.w (id int, v text, t text); CREATE INDEX w_gin ON public.w USING gin ({key});";

    [TestMethod]
    [DataRow("to_tsvector('english', v)", "to_tsvector('english'::regconfig, v)")]
    [DataRow("to_tsvector('english', v)", "to_tsvector('english'::pg_catalog.regconfig, v)")]
    [DataRow("(to_tsvector('simple', v) || to_tsvector('simple', t))", "((to_tsvector('simple'::regconfig, v) || to_tsvector('simple'::regconfig, t)))")]
    public void TheConfigurationWithAndWithoutItsCastIsTheSameIndex(string written, string stored)
    {
        var project = Build(Sql(written));
        var live = Build(Sql(stored));

        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(live, project)));
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(project, live)));
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(project, project)));
    }

    [TestMethod]
    [DataRow("to_tsvector('english', v)", "to_tsvector('german'::regconfig, v)")]
    [DataRow("to_tsvector('english', v)", "to_tsvector('english'::regconfig, t)")]
    [DataRow("to_tsvector('english', v)", "to_tsvector('english'::regconfig, lower(v))")]
    public void AnotherConfigurationOrColumnIsADifference(string before, string after)
    {
        Assert.IsFalse(IsEmpty(new DiffEngine().Diff(Build(Sql(before)), Build(Sql(after)))));
    }

    private static bool IsEmpty(ChangeSet diff) => diff.Added.Count + diff.Removed.Count + diff.Modified.Count == 0;

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
