using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// Regression coverage for G-41: constraints deploy as separate <c>ALTER TABLE ... ADD CONSTRAINT</c>
/// statements, so a foreign key must be created after the PRIMARY KEY / UNIQUE constraint (or unique index)
/// it references. PostgreSQL rejects the foreign key otherwise (42830). The tests use table names whose
/// sort order puts the referencing table first, so an ordering that only follows key order fails.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ForeignKeyOrderingTests
{
    [TestMethod]
    public void ForeignKeyIsCreatedAfterTheReferencedPrimaryKey()
    {
        var order = CreateOrder("""
            create table public.a (id bigint primary key, parent_id bigint references public.p (id));
            create table public.p (id bigint primary key);
            """);

        AssertBefore(order, "public.p.p_pkey", "public.a.a_parent_id_fkey");
    }

    [TestMethod]
    public void ForeignKeyWithoutColumnListTargetsThePrimaryKey()
    {
        var order = CreateOrder("""
            create table public.a (parent_id bigint references public.p);
            create table public.p (id bigint primary key, code text unique);
            """);

        AssertBefore(order, "public.p.p_pkey", "public.a.a_parent_id_fkey");
    }

    [TestMethod]
    public void ForeignKeyIsCreatedAfterTheReferencedUniqueConstraint()
    {
        var order = CreateOrder("""
            create table public.a (parent_code text references public.p (code));
            create table public.p (id bigint primary key, code text unique);
            """);

        AssertBefore(order, "public.p.p_code_key", "public.a.a_parent_code_fkey");
    }

    [TestMethod]
    public void CompositeForeignKeyMatchesTheKeyOverTheSameColumnsInAnyOrder()
    {
        var order = CreateOrder("""
            create table public.a (x integer, y integer, foreign key (x, y) references public.p (x, y));
            create table public.p (id bigint primary key, x integer, y integer, unique (y, x));
            """);

        AssertBefore(order, "public.p.p_y_x_key", "public.a.a_x_y_fkey");
    }

    [TestMethod]
    public void SelfReferencingForeignKeyIsCreatedAfterItsOwnPrimaryKey()
    {
        // The FK name ("t_parent_fkey") sorts before the PK name ("t_pkey"), so key order alone is wrong.
        var order = CreateOrder("create table public.t (id bigint primary key, parent bigint references public.t (id));");

        AssertBefore(order, "public.t.t_pkey", "public.t.t_parent_fkey");
    }

    [TestMethod]
    public void ForeignKeyIsCreatedAfterAReferencedUniqueIndex()
    {
        var order = CreateOrder("""
            create table public.a (parent_code text references public.p (code));
            create table public.p (id bigint primary key, code text);
            create unique index p_code_uidx on public.p (code);
            """);

        AssertBefore(order, "public.p_code_uidx", "public.a.a_parent_code_fkey");
    }

    [TestMethod]
    public void MutualForeignKeysDoNotFormACycle()
    {
        var order = CreateOrder("""
            create table public.a (id bigint primary key, b_id bigint);
            create table public.b (id bigint primary key, a_id bigint);
            alter table public.a add foreign key (b_id) references public.b (id);
            alter table public.b add foreign key (a_id) references public.a (id);
            """);

        AssertBefore(order, "public.b.b_pkey", "public.a.a_b_id_fkey");
        AssertBefore(order, "public.a.a_pkey", "public.b.b_a_id_fkey");
    }

    [TestMethod]
    public void RemovingAForeignKeyAndItsReferencedKeyDropsTheForeignKeyFirst()
    {
        var oldModel = BuildModel("""
            create table public.a (parent_id bigint references public.p (id));
            create table public.p (id bigint primary key);
            """);
        var newModel = BuildModel("""
            create table public.a (parent_id bigint);
            create table public.p (id bigint);
            """);

        var changes = new DiffEngine().Diff(oldModel, newModel);
        var graph = new DependencyAnalyzer().Analyze(oldModel, newModel);
        var batch = new ScriptGenerator().Generate(changes, graph);
        var drops = batch.Operations
            .Select(static (operation, index) => (operation, index))
            .Where(static entry => entry.operation is DropOperation)
            .ToDictionary(static entry => entry.operation.NodeId, static entry => entry.index, StringComparer.OrdinalIgnoreCase);

        Assert.IsTrue(drops.ContainsKey("public.a.a_parent_id_fkey"), "Expected the foreign key to be dropped.");
        Assert.IsTrue(drops.ContainsKey("public.p.p_pkey"), "Expected the primary key to be dropped.");
        Assert.IsLessThan(drops["public.p.p_pkey"], drops["public.a.a_parent_id_fkey"], "The foreign key must be dropped before the key it depends on.");
    }

    private static void AssertBefore(IReadOnlyDictionary<string, int> order, string first, string second)
    {
        Assert.IsTrue(order.ContainsKey(first), $"Expected an operation for '{first}'. Operations: {string.Join(", ", order.Keys)}");
        Assert.IsTrue(order.ContainsKey(second), $"Expected an operation for '{second}'. Operations: {string.Join(", ", order.Keys)}");
        Assert.IsLessThan(order[second], order[first], $"'{first}' must be created before '{second}'.");
    }

    private static IReadOnlyDictionary<string, int> CreateOrder(string sql)
    {
        var model = BuildModel(sql);
        var changes = new DiffEngine().Diff(DatabaseModel.Empty, model);
        var graph = new DependencyAnalyzer().Analyze(DatabaseModel.Empty, model);
        var batch = new ScriptGenerator().Generate(changes, graph);

        return batch.Operations
            .Select(static (operation, index) => (operation, index))
            .Where(static entry => entry.operation is CreateOperation)
            .ToDictionary(static entry => entry.operation.NodeId, static entry => entry.index, StringComparer.OrdinalIgnoreCase);
    }

    private static DatabaseModel BuildModel(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
