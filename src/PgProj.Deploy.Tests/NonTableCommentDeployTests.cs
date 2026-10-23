using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-34: a comment on a view, a materialized view, a function, a procedure, an index, or a sequence is deployed, and removing
/// one is deployed too. Three bugs found while writing these: (1) a sequence comment-only change built a bare
/// <c>ALTER SEQUENCE name;</c> with no action, a syntax error PostgreSQL refuses; (2) removing a view's, a function's, or a
/// procedure's comment through <c>CREATE OR REPLACE</c> never cleared it live, since <c>CREATE OR REPLACE</c> does not touch a
/// comment it is not setting, so the old comment survived and re-diffed forever; (3) a comment-only change to an index or a
/// materialized view was redeployed by dropping and recreating it (wasteful for an index, and for a materialized view it also
/// discards what it computed), rather than the in-place <c>COMMENT ON</c> a comment-only change needs.
/// <see cref="ObjectCommentOperationTests"/> covers table and column.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class NonTableCommentDeployTests
{
    [TestMethod]
    public void ANewViewIsCreatedWithItsComment()
    {
        var statements = Statements(DatabaseModel.Empty, Build("CREATE VIEW public.v AS SELECT 1 AS a; COMMENT ON VIEW public.v IS 'a view';"));

        StringAssert.Contains(string.Join("\n", statements), "COMMENT ON VIEW \"public\".\"v\" IS 'a view';");
    }

    [TestMethod]
    public void AChangedViewCommentIsDeployedWithoutADrop()
    {
        var statements = Statements(
            Build("CREATE VIEW public.v AS SELECT 1 AS a; COMMENT ON VIEW public.v IS 'old';"),
            Build("CREATE VIEW public.v AS SELECT 1 AS a; COMMENT ON VIEW public.v IS 'new';"));

        Assert.IsFalse(statements.Any(static s => s.Contains("DROP", StringComparison.Ordinal)), string.Join("\n", statements));
        StringAssert.Contains(string.Join("\n", statements), "COMMENT ON VIEW \"public\".\"v\" IS 'new';");
    }

    [TestMethod]
    public void ARemovedViewCommentIsClearedLive()
    {
        var statements = Statements(
            Build("CREATE VIEW public.v AS SELECT 1 AS a; COMMENT ON VIEW public.v IS 'old';"),
            Build("CREATE VIEW public.v AS SELECT 1 AS a;"));

        StringAssert.Contains(string.Join("\n", statements), "COMMENT ON VIEW \"public\".\"v\" IS NULL;");
    }

    [TestMethod]
    public void AChangedMaterializedViewCommentIsJustCommentOn()
    {
        var statements = Statements(
            Build("CREATE MATERIALIZED VIEW public.mv AS SELECT 1 AS a; COMMENT ON MATERIALIZED VIEW public.mv IS 'old';"),
            Build("CREATE MATERIALIZED VIEW public.mv AS SELECT 1 AS a; COMMENT ON MATERIALIZED VIEW public.mv IS 'new';"));

        CollectionAssert.AreEqual(new[] { "COMMENT ON MATERIALIZED VIEW \"public\".\"mv\" IS 'new';" }, statements);
    }

    [TestMethod]
    public void ARemovedMaterializedViewCommentIsClearedLiveWithoutADrop()
    {
        var statements = Statements(
            Build("CREATE MATERIALIZED VIEW public.mv AS SELECT 1 AS a; COMMENT ON MATERIALIZED VIEW public.mv IS 'old';"),
            Build("CREATE MATERIALIZED VIEW public.mv AS SELECT 1 AS a;"));

        CollectionAssert.AreEqual(new[] { "COMMENT ON MATERIALIZED VIEW \"public\".\"mv\" IS NULL;" }, statements);
    }

    [TestMethod]
    public void AMaterializedViewThatIsActuallyChangedStillDropsAndRecreates()
    {
        var statements = Statements(
            Build("CREATE MATERIALIZED VIEW public.mv AS SELECT 1 AS a; COMMENT ON MATERIALIZED VIEW public.mv IS 'old';"),
            Build("CREATE MATERIALIZED VIEW public.mv AS SELECT 2 AS a; COMMENT ON MATERIALIZED VIEW public.mv IS 'old';"));

        var script = string.Join("\n", statements);
        StringAssert.Contains(script, "DROP MATERIALIZED VIEW");
        StringAssert.Contains(script, "COMMENT ON MATERIALIZED VIEW \"public\".\"mv\" IS 'old';");
    }

    [TestMethod]
    public void ARemovedFunctionCommentIsClearedLiveThroughCreateOrReplace()
    {
        var statements = Statements(
            Build("CREATE FUNCTION public.f(x integer) RETURNS integer LANGUAGE sql AS $$ SELECT x $$; COMMENT ON FUNCTION public.f(integer) IS 'old';"),
            Build("CREATE FUNCTION public.f(x integer) RETURNS integer LANGUAGE sql AS $$ SELECT x $$;"));

        var script = string.Join("\n", statements);
        StringAssert.Contains(script, "CREATE OR REPLACE FUNCTION");
        StringAssert.Contains(script, "COMMENT ON FUNCTION \"public\".\"f\"(integer) IS NULL;");
    }

    [TestMethod]
    public void ARemovedProcedureCommentIsClearedLiveThroughCreateOrReplace()
    {
        var statements = Statements(
            Build("CREATE PROCEDURE public.p(x integer) LANGUAGE sql AS $$ SELECT x $$; COMMENT ON PROCEDURE public.p(integer) IS 'old';"),
            Build("CREATE PROCEDURE public.p(x integer) LANGUAGE sql AS $$ SELECT x $$;"));

        var script = string.Join("\n", statements);
        StringAssert.Contains(script, "CREATE OR REPLACE PROCEDURE");
        StringAssert.Contains(script, "COMMENT ON PROCEDURE \"public\".\"p\"(integer) IS NULL;");
    }

    [TestMethod]
    public void ANewFunctionWithNoCommentEmitsNoCommentStatement()
    {
        var statements = Statements(DatabaseModel.Empty, Build("CREATE FUNCTION public.f() RETURNS integer LANGUAGE sql AS $$ SELECT 1 $$;"));

        Assert.IsFalse(statements.Any(static s => s.Contains("COMMENT", StringComparison.Ordinal)), string.Join("\n", statements));
    }

    [TestMethod]
    public void AChangedIndexCommentIsJustCommentOn()
    {
        var statements = Statements(
            Build("CREATE TABLE public.t (a integer); CREATE INDEX i ON public.t (a); COMMENT ON INDEX public.i IS 'old';"),
            Build("CREATE TABLE public.t (a integer); CREATE INDEX i ON public.t (a); COMMENT ON INDEX public.i IS 'new';"));

        var script = string.Join("\n", statements);
        Assert.IsFalse(script.Contains("DROP INDEX", StringComparison.Ordinal), script);
        Assert.IsFalse(script.Contains("CREATE INDEX", StringComparison.Ordinal), script);
        StringAssert.Contains(script, "COMMENT ON INDEX \"public\".\"i\" IS 'new';");
    }

    [TestMethod]
    public void ARemovedIndexCommentIsClearedWithoutADrop()
    {
        var statements = Statements(
            Build("CREATE TABLE public.t (a integer); CREATE INDEX i ON public.t (a); COMMENT ON INDEX public.i IS 'old';"),
            Build("CREATE TABLE public.t (a integer); CREATE INDEX i ON public.t (a);"));

        var script = string.Join("\n", statements);
        Assert.IsFalse(script.Contains("DROP INDEX", StringComparison.Ordinal), script);
        StringAssert.Contains(script, "COMMENT ON INDEX \"public\".\"i\" IS NULL;");
    }

    [TestMethod]
    public void AnIndexThatIsActuallyChangedStillDropsAndRecreatesAndKeepsItsComment()
    {
        var statements = Statements(
            Build("CREATE TABLE public.t (a integer, b integer); CREATE INDEX i ON public.t (a); COMMENT ON INDEX public.i IS 'old';"),
            Build("CREATE TABLE public.t (a integer, b integer); CREATE INDEX i ON public.t (a, b); COMMENT ON INDEX public.i IS 'old';"));

        var script = string.Join("\n", statements);
        StringAssert.Contains(script, "DROP INDEX");
        StringAssert.Contains(script, "COMMENT ON INDEX \"public\".\"i\" IS 'old';");
    }

    [TestMethod]
    public void AChangedSequenceCommentIsJustCommentOnWithNoBareAlterSequence()
    {
        var statements = Statements(
            Build("CREATE SEQUENCE public.s; COMMENT ON SEQUENCE public.s IS 'old';"),
            Build("CREATE SEQUENCE public.s; COMMENT ON SEQUENCE public.s IS 'new';"));

        CollectionAssert.AreEqual(new[] { "COMMENT ON SEQUENCE \"public\".\"s\" IS 'new';" }, statements);
    }

    [TestMethod]
    public void ARemovedSequenceCommentIsJustCommentOnWithNoBareAlterSequence()
    {
        var statements = Statements(
            Build("CREATE SEQUENCE public.s; COMMENT ON SEQUENCE public.s IS 'old';"),
            Build("CREATE SEQUENCE public.s;"));

        CollectionAssert.AreEqual(new[] { "COMMENT ON SEQUENCE \"public\".\"s\" IS NULL;" }, statements);
    }

    [TestMethod]
    public void ASequenceOptionAndCommentBothChangedEmitBothStatements()
    {
        var statements = Statements(
            Build("CREATE SEQUENCE public.s INCREMENT BY 1; COMMENT ON SEQUENCE public.s IS 'old';"),
            Build("CREATE SEQUENCE public.s INCREMENT BY 2; COMMENT ON SEQUENCE public.s IS 'new';"));

        var script = string.Join("\n", statements);
        StringAssert.Contains(script, "ALTER SEQUENCE \"public\".\"s\" INCREMENT BY 2;");
        StringAssert.Contains(script, "COMMENT ON SEQUENCE \"public\".\"s\" IS 'new';");
    }

    [TestMethod]
    public void AChangedOverloadedFunctionCommentTargetsOnlyThatOverload()
    {
        var statements = Statements(
            Build(
                "CREATE FUNCTION public.f(x integer) RETURNS integer LANGUAGE sql AS $$ SELECT x $$;"
                + " CREATE FUNCTION public.f(x text) RETURNS text LANGUAGE sql AS $$ SELECT x $$;"
                + " COMMENT ON FUNCTION public.f(text) IS 'old';"),
            Build(
                "CREATE FUNCTION public.f(x integer) RETURNS integer LANGUAGE sql AS $$ SELECT x $$;"
                + " CREATE FUNCTION public.f(x text) RETURNS text LANGUAGE sql AS $$ SELECT x $$;"
                + " COMMENT ON FUNCTION public.f(text) IS 'new';"));

        var script = string.Join("\n", statements);
        StringAssert.Contains(script, "COMMENT ON FUNCTION \"public\".\"f\"(text) IS 'new';");
        Assert.IsFalse(script.Contains("(pg_catalog.int4) IS", StringComparison.Ordinal), script);
    }

    private static List<string> Statements(DatabaseModel oldModel, DatabaseModel newModel)
    {
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));
        return batch.Operations
            .Where(static operation => operation is not SchemaCreateOperation)
            .Select(static operation => operation.GenerateSql().Trim().Replace("\r\n", "\n", StringComparison.Ordinal))
            .Where(static statement => !statement.StartsWith("-- No column-level changes", StringComparison.Ordinal))
            .ToList();
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
