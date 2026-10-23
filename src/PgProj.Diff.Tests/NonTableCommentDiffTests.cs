using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-34: a changed comment on a view, a materialized view, a function, a procedure, an index, a sequence, or a type is
/// reported as a modification, and an unchanged one reports nothing. <see cref="ObjectCommentDiffTests"/> covers table and column.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class NonTableCommentDiffTests
{
    [TestMethod]
    [DataRow("CREATE VIEW public.v AS SELECT 1 AS a;", "COMMENT ON VIEW public.v IS", "public.v")]
    [DataRow("CREATE MATERIALIZED VIEW public.mv AS SELECT 1 AS a;", "COMMENT ON MATERIALIZED VIEW public.mv IS", "public.mv")]
    [DataRow("CREATE FUNCTION public.f() RETURNS integer LANGUAGE sql AS $$ SELECT 1 $$;", "COMMENT ON FUNCTION public.f() IS", "public.f()")]
    [DataRow("CREATE SEQUENCE public.s;", "COMMENT ON SEQUENCE public.s IS", "public.s")]
    [DataRow("CREATE TYPE public.color AS ENUM ('red');", "COMMENT ON TYPE public.color IS", "public.color")]
    public void AChangedCommentIsReportedAsModified(string declaration, string commentStatement, string nodeIdSuffix)
    {
        var oldModel = Build($"{declaration} {commentStatement} 'old';");
        var newModel = Build($"{declaration} {commentStatement} 'new';");

        var change = new DiffEngine().Diff(oldModel, newModel).Modified.Single(candidate => candidate.NodeId.EndsWith(nodeIdSuffix, StringComparison.OrdinalIgnoreCase));
        var detail = change.Details.Single(static d => d.PropertyName == "Comment");
        Assert.AreEqual("old", detail.OldValue);
        Assert.AreEqual("new", detail.NewValue);
    }

    [TestMethod]
    public void AChangedIndexCommentIsReportedAsModified()
    {
        var oldModel = Build("CREATE TABLE public.t (a integer); CREATE INDEX i ON public.t (a); COMMENT ON INDEX public.i IS 'old';");
        var newModel = Build("CREATE TABLE public.t (a integer); CREATE INDEX i ON public.t (a); COMMENT ON INDEX public.i IS 'new';");

        var change = new DiffEngine().Diff(oldModel, newModel).Modified.Single(static c => c.NodeId.EndsWith(".i", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(change.Details.Any(static d => d.PropertyName == nameof(IndexDefinition.Comment)));
    }

    [TestMethod]
    [DataRow("CREATE VIEW public.v AS SELECT 1 AS a;", "COMMENT ON VIEW public.v IS")]
    [DataRow("CREATE MATERIALIZED VIEW public.mv AS SELECT 1 AS a;", "COMMENT ON MATERIALIZED VIEW public.mv IS")]
    [DataRow("CREATE FUNCTION public.f() RETURNS integer LANGUAGE sql AS $$ SELECT 1 $$;", "COMMENT ON FUNCTION public.f() IS")]
    [DataRow("CREATE SEQUENCE public.s;", "COMMENT ON SEQUENCE public.s IS")]
    [DataRow("CREATE TYPE public.color AS ENUM ('red');", "COMMENT ON TYPE public.color IS")]
    public void TheSameCommentReportsNothing(string declaration, string commentStatement)
    {
        var model1 = Build($"{declaration} {commentStatement} 'same';");
        var model2 = Build($"{declaration} {commentStatement} 'same';");

        var diff = new DiffEngine().Diff(model1, model2);
        Assert.AreEqual(0, diff.Added.Count + diff.Removed.Count + diff.Modified.Count);
    }

    [TestMethod]
    public void AnOverloadedFunctionsCommentChangeDoesNotAffectTheOtherOverload()
    {
        var oldModel = Build(
            "CREATE FUNCTION public.f(x integer) RETURNS integer LANGUAGE sql AS $$ SELECT x $$;"
            + " CREATE FUNCTION public.f(x text) RETURNS text LANGUAGE sql AS $$ SELECT x $$;"
            + " COMMENT ON FUNCTION public.f(text) IS 'old';");
        var newModel = Build(
            "CREATE FUNCTION public.f(x integer) RETURNS integer LANGUAGE sql AS $$ SELECT x $$;"
            + " CREATE FUNCTION public.f(x text) RETURNS text LANGUAGE sql AS $$ SELECT x $$;"
            + " COMMENT ON FUNCTION public.f(text) IS 'new';");

        var diff = new DiffEngine().Diff(oldModel, newModel);
        Assert.HasCount(1, diff.Modified);
        StringAssert.Contains(diff.Modified.Single().NodeId, "text");
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
