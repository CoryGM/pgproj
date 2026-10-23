using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-34: a standalone <c>COMMENT ON ...</c> is applied to a view, a materialized view, a function, a procedure, an index, a
/// sequence, and a type, not only a table or a column. <see cref="ObjectCommentSemanticsTests"/> covers table and column.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class NonTableCommentSemanticsTests
{
    [TestMethod]
    public void AViewCommentIsApplied()
    {
        var model = Build("CREATE VIEW public.v AS SELECT 1 AS a; COMMENT ON VIEW public.v IS 'a view';");

        Assert.AreEqual("a view", model.Views["public.v"].Comment);
    }

    [TestMethod]
    public void AMaterializedViewCommentIsApplied()
    {
        var model = Build("CREATE MATERIALIZED VIEW public.mv AS SELECT 1 AS a; COMMENT ON MATERIALIZED VIEW public.mv IS 'a materialized view';");

        Assert.AreEqual("a materialized view", model.Views["public.mv"].Comment);
    }

    [TestMethod]
    public void AFunctionCommentIsApplied()
    {
        var model = Build(
            "CREATE FUNCTION public.f(x integer) RETURNS integer LANGUAGE sql AS $$ SELECT x $$;"
            + " COMMENT ON FUNCTION public.f(integer) IS 'doubles nothing yet';");

        Assert.AreEqual("doubles nothing yet", model.Functions.Values.Single().Comment);
    }

    [TestMethod]
    public void AProcedureCommentIsApplied()
    {
        var model = Build(
            "CREATE PROCEDURE public.p(x integer) LANGUAGE sql AS $$ SELECT x $$;"
            + " COMMENT ON PROCEDURE public.p(integer) IS 'a procedure';");

        Assert.AreEqual("a procedure", model.Procedures.Values.Single().Comment);
    }

    [TestMethod]
    public void AnOverloadedFunctionCommentIsAppliedToTheMatchingOverloadOnly()
    {
        var model = Build(
            "CREATE FUNCTION public.f(x integer) RETURNS integer LANGUAGE sql AS $$ SELECT x $$;"
            + " CREATE FUNCTION public.f(x text) RETURNS text LANGUAGE sql AS $$ SELECT x $$;"
            + " COMMENT ON FUNCTION public.f(text) IS 'the text overload';");

        var textOverload = model.Functions.Values.Single(static f => f.Signature!.Parameters.Any(static p => p.DataType!.Contains("text", StringComparison.Ordinal)));
        var intOverload = model.Functions.Values.Single(static f => f.Signature!.Parameters.Any(static p => p.DataType!.Contains("int", StringComparison.Ordinal)));
        Assert.AreEqual("the text overload", textOverload.Comment);
        Assert.IsNull(intOverload.Comment);
    }

    [TestMethod]
    public void AnIndexCommentIsApplied()
    {
        var model = Build("CREATE TABLE public.t (a integer); CREATE INDEX i ON public.t (a); COMMENT ON INDEX public.i IS 'an index';");

        Assert.AreEqual("an index", model.Indexes.Values.Single().Comment);
    }

    [TestMethod]
    public void ASequenceCommentIsApplied()
    {
        var model = Build("CREATE SEQUENCE public.s; COMMENT ON SEQUENCE public.s IS 'a sequence';");

        Assert.AreEqual("a sequence", model.Sequences.Values.Single().Comment);
    }

    [TestMethod]
    public void ATypeCommentIsApplied()
    {
        var model = Build("CREATE TYPE public.color AS ENUM ('red', 'green'); COMMENT ON TYPE public.color IS 'a type';");

        Assert.AreEqual("a type", model.Types.Values.Single().Comment);
    }

    [TestMethod]
    [DataRow("CREATE VIEW public.v AS SELECT 1 AS a;")]
    [DataRow("CREATE MATERIALIZED VIEW public.mv AS SELECT 1 AS a;")]
    [DataRow("CREATE FUNCTION public.f() RETURNS integer LANGUAGE sql AS $$ SELECT 1 $$;")]
    [DataRow("CREATE TABLE public.t (a integer); CREATE INDEX i ON public.t (a);")]
    [DataRow("CREATE SEQUENCE public.s;")]
    [DataRow("CREATE TYPE public.color AS ENUM ('red');")]
    public void WithoutACommentEachKindHasNullComment(string sql)
    {
        var model = Build(sql);

        var comments = model.Views.Values.Select(static v => v.Comment)
            .Concat(model.Functions.Values.Select(static f => f.Comment))
            .Concat(model.Indexes.Values.Select(static i => i.Comment))
            .Concat(model.Sequences.Values.Select(static s => s.Comment))
            .Concat(model.Types.Values.Select(static t => t.Comment));
        Assert.IsTrue(comments.Any());
        Assert.IsTrue(comments.All(static c => c is null), string.Join(", ", comments));
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
