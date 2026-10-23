using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Dependencies.Tests;

/// <summary>
/// G-90: PostgreSQL resolves a view's query, and a SQL-language routine body, when it is created, so everything they read must exist
/// first. A view over another view (G-91), and a <c>LANGUAGE sql</c> function calling another (G-94), are now ordered after it; before,
/// neither had an edge, and the two were created in whatever order their names gave.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class QueryBodyDependencyTests
{
    [TestMethod]
    public void AViewIsCreatedAfterTheViewItReads()
        => AssertBefore(
            "CREATE TABLE public.o (id int); CREATE VIEW public.a_top AS SELECT * FROM public.z_base; CREATE VIEW public.z_base AS SELECT id FROM public.o;",
            "public.z_base",
            "public.a_top");

    [TestMethod]
    public void AMaterializedViewIsCreatedAfterTheViewItReads()
        => AssertBefore(
            "CREATE TABLE public.o (id int); CREATE MATERIALIZED VIEW public.a_m AS SELECT id FROM public.z_base; CREATE VIEW public.z_base AS SELECT id FROM public.o;",
            "public.z_base",
            "public.a_m");

    [TestMethod]
    public void ASqlFunctionIsCreatedAfterTheFunctionItCalls()
        => AssertBefore(
            "CREATE SCHEMA s; CREATE FUNCTION s.a_top() RETURNS int LANGUAGE sql AS $$ SELECT s.z_leaf() + 1 $$; CREATE FUNCTION s.z_leaf() RETURNS int LANGUAGE sql AS $$ SELECT 1 $$;",
            "z_leaf",
            "a_top");

    [TestMethod]
    public void ASqlFunctionIsCreatedAfterTheViewItReads()
        => AssertBefore(
            "CREATE TABLE public.o (id int); CREATE FUNCTION public.a_n() RETURNS bigint LANGUAGE sql AS $$ SELECT count(*) FROM public.z_base $$; CREATE VIEW public.z_base AS SELECT id FROM public.o;",
            "public.z_base",
            "a_n");

    [TestMethod]
    public void AViewIsCreatedAfterTheFunctionItCallsWithASchema()
        => AssertBefore(
            "CREATE TABLE public.o (id int); CREATE VIEW public.a_v AS SELECT public.z_f(o.id) FROM public.o o; CREATE FUNCTION public.z_f(p int) RETURNS int LANGUAGE sql AS $$ SELECT p $$;",
            "z_f",
            "public.a_v");

    private static void AssertBefore(string sql, string first, string then)
    {
        var model = new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
        var order = new DependencyAnalyzer().Analyze(model).TopologicalSort().Select(static node => node.NodeId).ToList();

        var firstIndex = order.FindIndex(id => Matches(id, first));
        var thenIndex = order.FindIndex(id => Matches(id, then));
        Assert.IsGreaterThanOrEqualTo(0, firstIndex, string.Join(", ", order));
        Assert.IsGreaterThanOrEqualTo(0, thenIndex, string.Join(", ", order));
        Assert.IsLessThan(thenIndex, firstIndex, string.Join(", ", order));
    }

    // A routine's key carries its argument types, so a routine is matched by name.
    private static bool Matches(string id, string name)
        => string.Equals(id, name, StringComparison.OrdinalIgnoreCase)
            || id.EndsWith("." + name, StringComparison.OrdinalIgnoreCase)
            || id.Contains("." + name + "(", StringComparison.OrdinalIgnoreCase);
}
