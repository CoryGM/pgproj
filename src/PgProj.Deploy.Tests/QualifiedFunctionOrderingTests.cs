using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-54: a schema-qualified call to a project function had no dependency edge (the analyzer's name fallback compared the bare
/// declared name with the whole reference), so the table, index, or view could be created before the function it calls and
/// PostgreSQL rejects it. The names below sort so that key order alone puts the caller first.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class QualifiedFunctionOrderingTests
{
    private const string Func = "create function public.z_bump(x integer) returns integer language sql as $$ select x + 1 $$; ";

    [TestMethod]
    [DataRow("create table public.a_t (n integer default public.z_bump(1));", "public.a_t")]
    [DataRow("create table public.a_t (n integer default z_bump(1));", "public.a_t")]
    [DataRow("create table public.a_t (n integer, constraint n_ok check (public.z_bump(n) > 0));", "public.a_t.n_ok")]
    [DataRow("create table public.b_t (n integer); create index a_idx on public.b_t (public.z_bump(n));", "public.a_idx")]
    [DataRow("create table public.b_t (n integer); create view public.a_v as select public.z_bump(n) as m from public.b_t;", "public.a_v")]
    public void ObjectCallingAProjectFunctionIsCreatedAfterIt(string sql, string caller)
    {
        var order = CreateOrder(Func + sql);

        var functionKey = order.Keys.Single(static key => key.StartsWith("public.z_bump", StringComparison.OrdinalIgnoreCase));
        Assert.IsLessThan(order[caller], order[functionKey], $"The function must be created before '{caller}'. Order: " + string.Join(", ", order.OrderBy(static pair => pair.Value).Select(static pair => pair.Key)));
    }

    [TestMethod]
    public void RemovingACallerAndItsFunctionDropsTheCallerFirst()
    {
        var oldModel = BuildModel(Func + "create table public.a_t (n integer default public.z_bump(1));");

        var generator = new ScriptGenerator();
        var batch = generator.Generate(new DiffEngine().Diff(oldModel, DatabaseModel.Empty), new DependencyAnalyzer().Analyze(oldModel, DatabaseModel.Empty));
        var drops = batch.Operations
            .Select(static (operation, index) => (operation, index))
            .Where(static entry => entry.operation is DropOperation)
            .ToDictionary(static entry => entry.operation.NodeId, static entry => entry.index, StringComparer.OrdinalIgnoreCase);

        var functionKey = drops.Keys.Single(static key => key.StartsWith("public.z_bump", StringComparison.OrdinalIgnoreCase));
        Assert.IsLessThan(drops[functionKey], drops["public.a_t"], "The table must be dropped before the function its default calls.");
    }

    [TestMethod]
    public void ObjectThatDoesNotCallTheFunctionGainsNoEdgeToIt()
    {
        var order = CreateOrder(Func + "create table public.a_t (n integer default 1); create table public.b_t (n integer default now());");

        // Nothing calls z_bump and now() is a built-in, so key order stands: the tables come before the function.
        var functionKey = order.Keys.Single(static key => key.StartsWith("public.z_bump", StringComparison.OrdinalIgnoreCase));
        Assert.IsLessThan(order[functionKey], order["public.a_t"]);
        Assert.IsLessThan(order[functionKey], order["public.b_t"]);
    }

    private static IReadOnlyDictionary<string, int> CreateOrder(string sql)
    {
        var model = BuildModel(sql);
        Assert.IsFalse(
            model.Diagnostics.Any(static diagnostic => diagnostic.Message.Contains("missing function", StringComparison.OrdinalIgnoreCase)),
            string.Join(Environment.NewLine, model.Diagnostics.Select(static diagnostic => diagnostic.Message)));

        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(DatabaseModel.Empty, model), new DependencyAnalyzer().Analyze(DatabaseModel.Empty, model));
        return batch.Operations
            .Select(static (operation, index) => (operation, index))
            .Where(static entry => entry.operation is CreateOperation)
            .ToDictionary(static entry => entry.operation.NodeId, static entry => entry.index, StringComparer.OrdinalIgnoreCase);
    }

    private static DatabaseModel BuildModel(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
