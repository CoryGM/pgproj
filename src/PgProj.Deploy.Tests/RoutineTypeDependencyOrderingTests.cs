using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// Regression coverage for G-50: a routine cannot be created before the types (or table/view row types) in its
/// parameter list and result, nor can they be dropped before it. The routine names sort before the type names,
/// so an ordering that only follows key order fails.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RoutineTypeDependencyOrderingTests
{
    private const string Mood = "create type public.mood as enum ('happy', 'sad');";

    [TestMethod]
    public void FunctionTakingAnEnumIsCreatedAfterTheEnum()
    {
        var order = CreateOrder(Mood + " create function public.a_f(m public.mood) returns integer language sql as $$ select 1 $$;");

        AssertBefore(order, "public.mood", "public.a_f(mood)");
    }

    [TestMethod]
    public void FunctionReturningAnEnumIsCreatedAfterTheEnum()
    {
        var order = CreateOrder(Mood + " create function public.a_f() returns public.mood language sql as $$ select 'happy'::public.mood $$;");

        AssertBefore(order, "public.mood", "public.a_f");
    }

    [TestMethod]
    public void FunctionTakingAnArrayOfAnEnumIsCreatedAfterTheEnum()
    {
        var order = CreateOrder(Mood + " create function public.a_f(m public.mood[]) returns integer language sql as $$ select 1 $$;");

        AssertBefore(order, "public.mood", "public.a_f(mood[])");
    }

    [TestMethod]
    public void ProcedureTakingAnEnumIsCreatedAfterTheEnum()
    {
        var order = CreateOrder(Mood + " create procedure public.a_p(m public.mood) language sql as $$ select 1 $$;");

        AssertBefore(order, "public.mood", "public.a_p(mood)");
    }

    [TestMethod]
    public void FunctionOverACompositeTypeIsCreatedAfterIt()
    {
        var order = CreateOrder("create type public.pt as (x integer); create function public.a_f(p public.pt) returns public.pt language sql as $$ select p $$;");

        AssertBefore(order, "public.pt", "public.a_f(pt)");
    }

    [TestMethod]
    public void FunctionReturningATableRowTypeIsCreatedAfterTheTable()
    {
        var order = CreateOrder("create table public.orders (id bigint primary key); create function public.a_f() returns setof public.orders language sql as $$ select * from public.orders $$;");

        AssertBefore(order, "public.orders", "public.a_f");
    }

    [TestMethod]
    public void FunctionTakingAViewRowTypeIsCreatedAfterTheView()
    {
        var order = CreateOrder("create table public.t (id bigint); create view public.v as select id from public.t; create function public.a_f(r public.v) returns integer language sql as $$ select 1 $$;");

        AssertBefore(order, "public.v", "public.a_f(v)");
    }

    [TestMethod]
    public void FunctionsOverBuiltInTypesGainNoExtraOrdering()
    {
        var order = CreateOrder("create function public.a_f(a integer, b text[]) returns numeric language sql as $$ select 1 $$;");

        Assert.HasCount(1, order);
    }

    [TestMethod]
    public void RemovingAFunctionAndItsEnumDropsTheFunctionFirst()
    {
        var oldModel = BuildModel(Mood + " create function public.a_f(m public.mood) returns integer language sql as $$ select 1 $$;");

        var generator = new ScriptGenerator();
        var batch = generator.Generate(new DiffEngine().Diff(oldModel, DatabaseModel.Empty), new DependencyAnalyzer().Analyze(oldModel, DatabaseModel.Empty));
        var drops = batch.Operations
            .Select(static (operation, index) => (operation, index))
            .Where(static entry => entry.operation is DropOperation)
            .ToDictionary(static entry => entry.operation.NodeId, static entry => entry.index, StringComparer.OrdinalIgnoreCase);

        Assert.IsLessThan(drops["public.mood"], drops["public.a_f(mood)"], "The function must be dropped before the type it uses.");
    }

    private static void AssertBefore(IReadOnlyDictionary<string, int> order, string first, string second)
    {
        var firstKey = order.Keys.Single(key => key.Equals(first, StringComparison.OrdinalIgnoreCase) || key.StartsWith(first + "(", StringComparison.OrdinalIgnoreCase));
        var secondKey = order.Keys.Single(key => key.StartsWith(second, StringComparison.OrdinalIgnoreCase));
        Assert.IsLessThan(order[secondKey], order[firstKey], $"'{first}' must be created before '{second}'. Order: {string.Join(", ", order.OrderBy(static pair => pair.Value).Select(static pair => pair.Key))}");
    }

    private static IReadOnlyDictionary<string, int> CreateOrder(string sql)
    {
        var model = BuildModel(sql);
        Assert.IsFalse(model.HasErrors, string.Join(Environment.NewLine, model.Diagnostics.Select(static diagnostic => diagnostic.Message)));

        var batch = new ScriptGenerator().Generate(
            new DiffEngine().Diff(DatabaseModel.Empty, model),
            new DependencyAnalyzer().Analyze(DatabaseModel.Empty, model));

        return batch.Operations
            .Select(static (operation, index) => (operation, index))
            .Where(static entry => entry.operation is CreateOperation)
            .ToDictionary(static entry => entry.operation.NodeId, static entry => entry.index, StringComparer.OrdinalIgnoreCase);
    }

    private static DatabaseModel BuildModel(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
