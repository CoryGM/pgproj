using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-66: the diff reported nothing for a function's volatility, a procedure's body, <c>RETURNS SETOF</c>, or a parameter's name,
/// default, or mode, so a publish silently left the live routine stale. Both directions matter here: a real change to any of them
/// is reported, and the same routine written differently (types, whitespace, defaults, explicit <c>IN</c>, an unstated volatility)
/// is not. Parameters are order-sensitive (the order is the call order); nothing else about a routine is.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RoutineChangeDiffTests
{
    private const string F = "create function public.f(x integer) returns integer language sql {0} as $$ select x $$;";

    // ---- real changes are reported ----

    [TestMethod]
    [DataRow("immutable", "volatile", DisplayName = "volatility")]
    [DataRow("stable", "immutable", DisplayName = "volatility, stable to immutable")]
    [DataRow("volatile", "volatile strict", DisplayName = "strict added")]
    [DataRow("volatile", "volatile security definer", DisplayName = "security definer added")]
    public void ChangedFunctionAttributeIsReported(string before, string after)
    {
        Assert.AreEqual(1, ModifiedCount(string.Format(F, before), string.Format(F, after)), $"{before} -> {after}");
    }

    [TestMethod]
    [DataRow("returns integer", "returns setof integer", DisplayName = "becomes set-returning")]
    [DataRow("returns integer", "returns bigint", DisplayName = "return type")]
    public void ChangedResultIsReported(string before, string after)
    {
        Assert.AreEqual(1, ModifiedCount(
            $"create function public.f(x integer) {before} language sql as $$ select 1 $$;",
            $"create function public.f(x integer) {after} language sql as $$ select 1 $$;"));
    }

    [TestMethod]
    [DataRow("f(x integer)", "f(y integer)", DisplayName = "parameter renamed")]
    [DataRow("f(x integer default 1)", "f(x integer)", DisplayName = "default removed")]
    [DataRow("f(x integer)", "f(x integer default 1)", DisplayName = "default added")]
    [DataRow("f(x integer default 1)", "f(x integer default 2)", DisplayName = "default changed")]
    [DataRow("f(x integer)", "f(inout x integer)", DisplayName = "mode changed")]
    [DataRow("f(a integer, b integer)", "f(b integer, a integer)", DisplayName = "parameters reordered")]
    public void ChangedParameterIsReported(string before, string after)
    {
        Assert.AreEqual(1, ModifiedCount(
            $"create function public.{before} returns integer language sql as $$ select 1 $$;",
            $"create function public.{after} returns integer language sql as $$ select 1 $$;"));
    }

    [TestMethod]
    public void ChangedProcedureBodyIsReported()
    {
        Assert.AreEqual(1, ModifiedCount(
            "create procedure public.p(x integer) language sql as $$ select 1 $$;",
            "create procedure public.p(x integer) language sql as $$ select 2 $$;"));
    }

    [TestMethod]
    [DataRow("p(x integer)", "p(y integer)", DisplayName = "parameter renamed")]
    [DataRow("p(x integer)", "p(inout x integer)", DisplayName = "mode changed")]
    [DataRow("p(x integer default 1)", "p(x integer)", DisplayName = "default removed")]
    public void ChangedProcedureParameterIsReported(string before, string after)
    {
        Assert.AreEqual(1, ModifiedCount(
            $"create procedure public.{before} language sql as $$ select 1 $$;",
            $"create procedure public.{after} language sql as $$ select 1 $$;"));
    }

    [TestMethod]
    public void ChangedProcedureAttributeIsReported()
    {
        Assert.AreEqual(1, ModifiedCount(
            "create procedure public.p() language sql as $$ select 1 $$;",
            "create procedure public.p() language sql security definer as $$ select 1 $$;"));
    }

    // ---- the same routine written differently is not ----

    [TestMethod]
    public void SelfDiffOfEveryKindOfRoutineIsEmpty()
    {
        var model = Build(
            "create function public.f(a integer, b text default 'x', inout c numeric(10,2) default 1) returns setof integer language sql stable strict security definer as $$ select 1 $$;"
            + "create procedure public.p(a integer, inout b text default 'x') language sql security definer as $$ select 1 $$;");

        Assert.AreEqual(0, ChangeCount(model, model));
    }

    // Spellings that PostgreSQL treats as one type are one routine (G-67): integer and int4, bigint and int8, timestamptz written out.
    [TestMethod]
    [DataRow("f(x integer)", "f(x int4)", DisplayName = "integer and int4")]
    [DataRow("f(x bigint)", "f(x int8)", DisplayName = "bigint and int8")]
    [DataRow("f(x integer[])", "f(x int4[])", DisplayName = "array")]
    [DataRow("f(x timestamp with time zone)", "f(x timestamptz)", DisplayName = "timestamptz")]
    [DataRow("f(a integer, b numeric(10,2))", "f(a int4, b numeric)", DisplayName = "modifiers containing a comma")]
    [DataRow("f(x integer)", "f(x pg_catalog.int4)", DisplayName = "pg_catalog prefix")]
    [DataRow("f(x varchar(10))", "f(x varchar(20))", DisplayName = "modifiers are not kept for routine parameters")]
    [DataRow("f(x varchar)", "f(x character varying)", DisplayName = "varchar spelled out")]
    [DataRow("f(x integer)", "f(in x integer)", DisplayName = "explicit IN")]
    [DataRow("f(x integer default 1)", "f(x integer default  1 )", DisplayName = "whitespace in a default")]
    [DataRow("f(x text default 'a')", "f(x text default 'a'::text)", DisplayName = "default with a cast")]
    public void SameParametersSpelledDifferentlyAreNotReported(string before, string after)
    {
        Assert.AreEqual(0, ChangeCount(
            Build($"create function public.{before} returns integer language sql as $$ select 1 $$;"),
            Build($"create function public.{after} returns integer language sql as $$ select 1 $$;")));
    }

    [TestMethod]
    [DataRow("returns integer", "returns int4")]
    [DataRow("returns integer", "returns pg_catalog.int4")]
    [DataRow("returns varchar(10)", "returns character varying")]
    [DataRow("returns boolean", "returns bool")]
    public void SameResultSpelledDifferentlyIsNotReported(string before, string after)
    {
        Assert.AreEqual(0, ChangeCount(
            Build($"create function public.f(x integer) {before} language sql as $$ select null $$;"),
            Build($"create function public.f(x integer) {after} language sql as $$ select null $$;")));
    }

    /// <summary>G-68: what a project writes and what the live catalog reports must be one procedure, whatever the type names.</summary>
    [TestMethod]
    public void ProcedureWithShortAndLongTypeNamesIsTheSameProcedure()
    {
        Assert.AreEqual(0, ChangeCount(
            Build("create procedure public.p(x int2, y float8, z bool, w text) language sql as $$ select 1 $$;"),
            Build("create procedure public.p(x smallint, y double precision, z boolean, w text) language sql as $$ select 1 $$;")));
    }

    [TestMethod]
    public void UnstatedVolatilityIsVolatile()
    {
        Assert.AreEqual(0, ModifiedCount(string.Format(F, string.Empty), string.Format(F, "volatile")));
    }

    [TestMethod]
    public void ClausesInADifferentOrderAreNotReported()
    {
        Assert.AreEqual(0, ModifiedCount(
            "create function public.f(x integer) returns integer language sql strict immutable as $$ select x $$;",
            "create function public.f(x integer) returns integer immutable language sql strict as $$ select x $$;"));
    }

    [TestMethod]
    public void WhitespaceOnlyChangesToABodyAreNotReported()
    {
        Assert.AreEqual(0, ModifiedCount(
            "create procedure public.p(x integer) language sql as $$ select   1 $$;",
            "create procedure public.p(x integer) language sql as $$ select 1\n$$;"));
        Assert.AreEqual(0, ModifiedCount(
            "create function public.f(x integer) returns integer language sql as $$ select   x $$;",
            "create function public.f(x integer) returns integer language sql as $$ select x\n$$;"));
    }

    [TestMethod]
    public void ParameterNamesDifferingInCaseOnlyAreStillDifferentNames()
    {
        // PostgreSQL folds unquoted names to lower case and keeps quoted ones, so X and x written unquoted are one name.
        Assert.AreEqual(0, ModifiedCount(
            "create function public.f(x integer) returns integer language sql as $$ select 1 $$;",
            "create function public.f(X integer) returns integer language sql as $$ select 1 $$;"));
        Assert.AreEqual(1, ModifiedCount(
            "create function public.f(x integer) returns integer language sql as $$ select 1 $$;",
            "create function public.f(\"X\" integer) returns integer language sql as $$ select 1 $$;"));
    }

    // ---- what changes together is one modification, and what is not a change stays unreported ----

    [TestMethod]
    public void SeveralChangesToOneRoutineAreOneModificationWithEveryDetail()
    {
        var changes = new DiffEngine().Diff(
            Build("create function public.f(x integer) returns integer language sql immutable as $$ select 1 $$;"),
            Build("create function public.f(y integer default 1) returns integer language sql volatile as $$ select 2 $$;"));

        var modified = changes.Modified.Single();
        var properties = modified.Details.Select(static detail => detail.PropertyName).ToArray();
        CollectionAssert.IsSubsetOf(new[] { "Signature.Parameters", "Signature.Attributes", "BodyExpressionNode" }, properties);
    }

    private static int ModifiedCount(string oldSql, string newSql)
        => new DiffEngine().Diff(Build(oldSql), Build(newSql)).Modified.Count;

    private static int ChangeCount(DatabaseModel oldModel, DatabaseModel newModel)
    {
        var changes = new DiffEngine().Diff(oldModel, newModel);
        return changes.Added.Count + changes.Removed.Count + changes.Modified.Count;
    }

    private static int ChangeCount(string oldSql, string newSql) => ChangeCount(Build(oldSql), Build(newSql));

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
