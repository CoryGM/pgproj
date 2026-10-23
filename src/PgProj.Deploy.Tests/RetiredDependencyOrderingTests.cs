using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-52: an object that is removed while a surviving object stops using it (a column retyped away from a removed type, a
/// default no longer calling a removed function, a serial column turned into a plain one) was dropped in the drop phase, before
/// the alter that ends the dependency, so PostgreSQL rejected the drop (<c>2BP01</c>). The surviving object only keeps its new
/// dependencies in the graph, so the analyzer now records the ones it gave up, and the generator drops the removed object after
/// the surviving one has been altered.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RetiredDependencyOrderingTests
{
    private const string Mood = "create type public.mood as enum ('happy', 'sad'); ";

    [TestMethod]
    public void ColumnRetypedAwayFromARemovedEnumIsAlteredBeforeTheTypeIsDropped()
    {
        var statements = Statements(
            Mood + "create table public.t (id integer, m public.mood);",
            "create table public.t (id integer, m text);");

        AssertBefore(statements, "ALTER TABLE \"public\".\"t\"", "DROP TYPE \"public\".\"mood\"");
    }

    [TestMethod]
    public void ColumnRetypedAwayFromAExtensionTypeIsAlteredBeforeTheExtensionIsDropped()
    {
        var statements = Statements(
            "create extension citext; create table public.t (c citext);",
            "create table public.t (c text);");

        AssertBefore(statements, "ALTER TABLE \"public\".\"t\"", "DROP EXTENSION");
    }

    [TestMethod]
    public void ColumnMovedToANewTypeIsAlteredAfterTheNewTypeExistsAndBeforeTheOldOneIsDropped()
    {
        var statements = Statements(
            Mood + "create table public.t (m public.mood);",
            "create type public.feeling as enum ('glad', 'mad'); create table public.t (m public.feeling);");

        AssertBefore(statements, "CREATE TYPE \"public\".\"feeling\"", "ALTER TABLE \"public\".\"t\"");
        AssertBefore(statements, "ALTER TABLE \"public\".\"t\"", "DROP TYPE \"public\".\"mood\"");
    }

    [TestMethod]
    public void DefaultNoLongerCallingARemovedFunctionIsAlteredBeforeTheFunctionIsDropped()
    {
        var statements = Statements(
            "create function public.z_bump(x integer) returns integer language sql as $$ select x + 1 $$; create table public.a_t (n integer default public.z_bump(1));",
            "create table public.a_t (n integer);");

        AssertBefore(statements, "ALTER TABLE \"public\".\"a_t\" ALTER COLUMN \"n\" DROP DEFAULT", "DROP FUNCTION");
    }

    [TestMethod]
    public void SerialColumnMadePlainIsAlteredBeforeItsSequenceIsDropped()
    {
        var statements = Statements(
            "create table public.t (id serial primary key);",
            "create table public.t (id integer primary key);");

        AssertBefore(statements, "ALTER TABLE \"public\".\"t\" ALTER COLUMN \"id\" DROP DEFAULT", "DROP SEQUENCE");
    }

    [TestMethod]
    public void ViewReplacedWithoutARemovedFunctionIsReplacedBeforeTheFunctionIsDropped()
    {
        var statements = Statements(
            "create table public.t (n integer); create function public.z_bump(x integer) returns integer language sql as $$ select x + 1 $$; "
            + "create view public.v as select public.z_bump(n) as m from public.t;",
            "create table public.t (n integer); create view public.v as select n as m from public.t;");

        AssertBefore(statements, "CREATE OR REPLACE VIEW \"public\".\"v\"", "DROP FUNCTION");
    }

    /// <summary>A constraint that is dropped and recreated without the removed function: its drop half must also come first.</summary>
    [TestMethod]
    public void ConstraintRecreatedWithoutARemovedFunctionIsDroppedBeforeTheFunction()
    {
        var statements = Statements(
            "create function public.z_ok(x integer) returns boolean language sql as $$ select x > 0 $$; "
            + "create table public.a_t (n integer, constraint n_ok check (public.z_ok(n)));",
            "create table public.a_t (n integer, constraint n_ok check (n > 1));");

        AssertBefore(statements, "ALTER TABLE \"public\".\"a_t\" DROP CONSTRAINT", "DROP FUNCTION");
    }

    /// <summary>The deferred drop keeps the dependent-first order of what it depends on.</summary>
    [TestMethod]
    public void RemovedTypesThatDependOnEachOtherAreDroppedDependentFirstAfterTheAlter()
    {
        var statements = Statements(
            Mood + "create type public.pt as (x integer, m public.mood); create table public.t (p public.pt);",
            "create table public.t (p text);");

        AssertBefore(statements, "ALTER TABLE \"public\".\"t\"", "DROP TYPE \"public\".\"pt\"");
        AssertBefore(statements, "DROP TYPE \"public\".\"pt\"", "DROP TYPE \"public\".\"mood\"");
    }

    // ---- controls: nothing else about the ordering changes ----

    [TestMethod]
    public void RemovingATableAndTheTypeItUsesStillDropsTheTableFirst()
    {
        var statements = Statements(Mood + "create table public.t (m public.mood);", string.Empty);

        AssertBefore(statements, "DROP TABLE \"public\".\"t\"", "DROP TYPE \"public\".\"mood\"");
    }

    [TestMethod]
    public void RemovedObjectNoSurvivorUsedIsDroppedBeforeCreatesAsBefore()
    {
        var statements = Statements(
            "create table public.old_t (id integer); create table public.keep (id integer);",
            "create table public.keep (id integer); create table public.new_t (id integer);");

        AssertBefore(statements, "DROP TABLE \"public\".\"old_t\"", "CREATE TABLE \"public\".\"new_t\"");
    }

    [TestMethod]
    public void UnrelatedRemovedTypeIsNotDeferredPastCreates()
    {
        var statements = Statements(
            Mood + "create table public.t (id integer);",
            "create table public.t (id integer); create table public.other (id integer);");

        AssertBefore(statements, "DROP TYPE \"public\".\"mood\"", "CREATE TABLE \"public\".\"other\"");
    }

    [TestMethod]
    public void ColumnStillUsingAKeptTypeAddsNoDeferral()
    {
        var statements = Statements(
            Mood + "create type public.old_t as enum ('a'); create table public.t (m public.mood, x integer);",
            "create type public.mood as enum ('happy', 'sad'); create table public.t (m public.mood, x bigint);");

        AssertBefore(statements, "DROP TYPE \"public\".\"old_t\"", "ALTER TABLE \"public\".\"t\"");
    }

    private static void AssertBefore(IReadOnlyList<string> statements, string firstPrefix, string secondPrefix)
    {
        var first = statements.Select((statement, index) => (statement, index)).FirstOrDefault(entry => entry.statement.StartsWith(firstPrefix, StringComparison.Ordinal));
        var second = statements.Select((statement, index) => (statement, index)).FirstOrDefault(entry => entry.statement.StartsWith(secondPrefix, StringComparison.Ordinal));

        Assert.IsNotNull(first.statement, $"No statement starts with '{firstPrefix}'.\n{string.Join("\n", statements)}");
        Assert.IsNotNull(second.statement, $"No statement starts with '{secondPrefix}'.\n{string.Join("\n", statements)}");
        Assert.IsLessThan(second.index, first.index, $"'{firstPrefix}' must come before '{secondPrefix}'.\n{string.Join("\n", statements)}");
    }

    private static IReadOnlyList<string> Statements(string oldSql, string newSql)
    {
        var oldModel = Build(oldSql);
        var newModel = Build(newSql);
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));
        return batch.Operations
            .Where(static operation => operation is not SchemaCreateOperation)
            .Select(static operation => operation.GenerateSql().Trim())
            .ToArray();
    }

    private static DatabaseModel Build(string sql)
        => string.IsNullOrWhiteSpace(sql) ? DatabaseModel.Empty : new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
