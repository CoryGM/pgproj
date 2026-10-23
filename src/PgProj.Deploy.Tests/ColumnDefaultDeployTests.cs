using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-56: a column DEFAULT was never captured, so it was dropped from every generated <c>CREATE TABLE</c> and a changed
/// default was never deployed. The deploy layer already emitted <c>RawDefaultExpression</c>; it just never had one.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ColumnDefaultDeployTests
{
    [TestMethod]
    public void CreateTableEmitsEveryDefaultAsWritten()
    {
        var script = Script(
            DatabaseModel.Empty,
            BuildModel("create table public.t (id integer, q integer not null default 0, c timestamptz default now(), s text default 'it''s, (fine)', u uuid default gen_random_uuid());"));

        StringAssert.Contains(script, "\"q\" integer NOT NULL DEFAULT 0", script);
        StringAssert.Contains(script, "\"c\" timestamptz DEFAULT now()", script);
        StringAssert.Contains(script, "\"s\" text DEFAULT 'it''s, (fine)'", script);
        StringAssert.Contains(script, "\"u\" uuid DEFAULT gen_random_uuid()", script);
    }

    [TestMethod]
    public void CreateTableKeepsTheDefaultExpressionExactlyAsWritten()
    {
        var script = Script(DatabaseModel.Empty, BuildModel("create table public.t (c text default coalesce(current_setting('app.x', true), 'none'), d integer[] default ARRAY[1,2], e integer default (1 + 2) * 3);"));

        StringAssert.Contains(script, "DEFAULT coalesce(current_setting('app.x', true), 'none')", script);
        StringAssert.Contains(script, "DEFAULT ARRAY[1,2]", script);
        StringAssert.Contains(script, "DEFAULT (1 + 2) * 3", script);
    }

    [TestMethod]
    public void AddColumnEmitsTheDefault()
    {
        var script = Script(
            BuildModel("create table public.t (id integer);"),
            BuildModel("create table public.t (id integer, created timestamptz not null default now());"));

        StringAssert.Contains(script, "ALTER TABLE \"public\".\"t\" ADD COLUMN \"created\" timestamptz NOT NULL DEFAULT now();", script);
    }

    [TestMethod]
    public void ChangedDefaultEmitsSetDefault()
    {
        var script = Script(
            BuildModel("create table public.t (q integer default 0);"),
            BuildModel("create table public.t (q integer default 5);"));

        StringAssert.Contains(script, "ALTER TABLE \"public\".\"t\" ALTER COLUMN \"q\" SET DEFAULT 5;", script);
    }

    [TestMethod]
    public void AddedDefaultEmitsSetDefault()
    {
        var script = Script(
            BuildModel("create table public.t (q integer);"),
            BuildModel("create table public.t (q integer default 7);"));

        StringAssert.Contains(script, "ALTER TABLE \"public\".\"t\" ALTER COLUMN \"q\" SET DEFAULT 7;", script);
    }

    [TestMethod]
    public void RemovedDefaultEmitsDropDefault()
    {
        var script = Script(
            BuildModel("create table public.t (q integer default 7);"),
            BuildModel("create table public.t (q integer);"));

        StringAssert.Contains(script, "ALTER TABLE \"public\".\"t\" ALTER COLUMN \"q\" DROP DEFAULT;", script);
    }

    [TestMethod]
    public void CosmeticDefaultChangeEmitsNothing()
    {
        var oldModel = BuildModel("create table public.t (a integer default 0, b text default 'x', c integer default -1);");
        var newModel = BuildModel("create table public.t (a integer default (0), b text default 'x'::text, c integer default '-1'::integer);");

        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));

        Assert.IsEmpty(batch.Operations, new ScriptGenerator().GenerateSqlScript(batch));
    }

    /// <summary>G-63: PostgreSQL reads a default back without the schema it is on the search path of, so this is not a change.</summary>
    [TestMethod]
    public void QualifiedFunctionDefaultVersusItsSearchPathRenderingEmitsNothing()
    {
        var oldModel = BuildModel("create table public.t (a timestamp default utc_now(), b timestamptz default now(), c uuid default uuid_generate_v7());");
        var newModel = BuildModel("create table public.t (a timestamp default public.utc_now(), b timestamptz default pg_catalog.now(), c uuid default public.uuid_generate_v7());");

        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));

        Assert.IsEmpty(batch.Operations, new ScriptGenerator().GenerateSqlScript(batch));
    }

    [TestMethod]
    public void DefaultMovedToAFunctionInAnotherSchemaStillEmitsSetDefault()
    {
        var script = Script(
            BuildModel("create table public.t (a integer default public.f());"),
            BuildModel("create table public.t (a integer default app.f());"));

        StringAssert.Contains(script, "ALTER TABLE \"public\".\"t\" ALTER COLUMN \"a\" SET DEFAULT app.f();", script);
    }

    /// <summary>The column changes for another reason; PostgreSQL's rendering of its unchanged default must not add a SET DEFAULT.</summary>
    [TestMethod]
    public void UnchangedDefaultIsNotRewrittenWhenAnotherPropertyOfTheColumnChanges()
    {
        var script = Script(
            BuildModel("create table public.t (q integer default 0);"),
            BuildModel("create table public.t (q bigint default '0'::bigint);"));

        StringAssert.Contains(script, "ALTER COLUMN \"q\" TYPE", script);
        Assert.IsFalse(script.Contains("SET DEFAULT", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void GeneratedAndIdentityColumnsEmitNoDefaultClause()
    {
        var script = Script(
            DatabaseModel.Empty,
            BuildModel("create table public.t (a integer, b integer generated always as (a * 2) stored, c integer generated by default as identity);"));

        Assert.IsFalse(script.Contains(" DEFAULT ", StringComparison.Ordinal) && !script.Contains("BY DEFAULT", StringComparison.Ordinal), script);
        StringAssert.Contains(script, "GENERATED BY DEFAULT AS IDENTITY", script);
    }

    /// <summary>The names sort so that a key-order tie-break would create the table first.</summary>
    [TestMethod]
    public void TableWithANextvalDefaultIsCreatedAfterTheSequence()
    {
        var order = CreateOrder("create sequence public.z_seq; create table public.a_t (id bigint default nextval('public.z_seq'));");

        Assert.IsLessThan(order["public.a_t"], order["public.z_seq"], "The sequence must exist before a default that calls nextval on it. Order: " + string.Join(", ", order.OrderBy(static pair => pair.Value).Select(static pair => pair.Key)));
    }

    [TestMethod]
    public void TableWithARegclassCastedSequenceDefaultIsCreatedAfterTheSequence()
    {
        var order = CreateOrder("create sequence public.z_seq; create table public.a_t (id bigint default nextval('public.z_seq'::regclass));");

        Assert.IsLessThan(order["public.a_t"], order["public.z_seq"]);
    }

    [TestMethod]
    public void TableWithAnUnqualifiedNextvalDefaultResolvesTheSequenceInItsSchema()
    {
        var order = CreateOrder("create schema app; create sequence app.z_seq; create table app.a_t (id bigint default nextval('z_seq'));");

        Assert.IsLessThan(order["app.a_t"], order["app.z_seq"]);
    }

    [TestMethod]
    public void RemovingATableAndItsSequenceDropsTheTableFirst()
    {
        var oldModel = BuildModel("create sequence public.a_seq; create table public.z_t (id bigint default nextval('public.a_seq'));");

        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, DatabaseModel.Empty), new DependencyAnalyzer().Analyze(oldModel, DatabaseModel.Empty));
        var drops = batch.Operations
            .Select(static (operation, index) => (operation, index))
            .Where(static entry => entry.operation is DropOperation)
            .ToDictionary(static entry => entry.operation.NodeId, static entry => entry.index, StringComparer.OrdinalIgnoreCase);

        Assert.IsLessThan(drops["public.a_seq"], drops["public.z_t"], "The table must be dropped before the sequence its default uses.");
    }

    [TestMethod]
    public void StringLiteralNamingASequenceInsideOtherTextIsNotADependency()
    {
        var order = CreateOrder("create sequence public.z_seq; create table public.a_t (note text default 'see public.z_seq');");

        // No edge: the literal is data, so key order (a_t before z_seq) is untouched.
        Assert.IsLessThan(order["public.z_seq"], order["public.a_t"]);
    }

    /// <summary>
    /// G-102: the column changes for another reason (it becomes NOT NULL). Its default is the same once both sides are
    /// PostgreSQL's rendering, though the canonical text form cannot tell (<c>(0)::numeric</c> against <c>0::numeric</c>), so
    /// no SET DEFAULT may be emitted.
    /// </summary>
    [TestMethod]
    public void ADefaultPostgresRewroteIsNotRewrittenWhenAnotherPropertyOfTheColumnChanges()
    {
        var script = Script(
            WithDefaultRendering(BuildModel("create table public.t (q numeric default (0)::numeric);"), "(0)::numeric"),
            WithDefaultRendering(BuildModel("create table public.t (q numeric not null default 0::numeric);"), "(0)::numeric"));

        StringAssert.Contains(script, "ALTER COLUMN \"q\" SET NOT NULL", script);
        Assert.IsFalse(script.Contains("SET DEFAULT", StringComparison.Ordinal), script);
    }

    private static DatabaseModel WithDefaultRendering(DatabaseModel model, string rendering)
    {
        model.Tables = model.Tables.ToDictionary(
            static entry => entry.Key,
            entry => entry.Value with { Columns = entry.Value.Columns.Select(column => column with { ServerRenderedDefault = rendering }).ToArray() },
            StringComparer.OrdinalIgnoreCase);
        return model;
    }

    private static string Script(DatabaseModel oldModel, DatabaseModel newModel)
    {
        var generator = new ScriptGenerator();
        return generator.GenerateSqlScript(generator.Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel)));
    }

    private static IReadOnlyDictionary<string, int> CreateOrder(string sql)
    {
        var model = BuildModel(sql);
        Assert.IsFalse(model.HasErrors, string.Join(Environment.NewLine, model.Diagnostics.Select(static diagnostic => diagnostic.Message)));

        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(DatabaseModel.Empty, model), new DependencyAnalyzer().Analyze(DatabaseModel.Empty, model));
        return batch.Operations
            .Select(static (operation, index) => (operation, index))
            .Where(static entry => entry.operation is CreateOperation)
            .ToDictionary(static entry => entry.operation.NodeId, static entry => entry.index, StringComparer.OrdinalIgnoreCase);
    }

    private static DatabaseModel BuildModel(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
