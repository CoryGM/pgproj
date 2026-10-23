using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-130: an aggregate is a model object of its own, keyed like an overloaded routine; its functions and user-defined types are dependencies; a call to it in a view or an
/// expression is a known function; and its options have a comparison form that leaves out what PostgreSQL defaults.
/// </summary>
[TestClass]
public sealed class AggregateSemanticsTests
{
    private const string Head = "CREATE TYPE public.mood AS ENUM ('a', 'b'); CREATE FUNCTION public.step(s integer, x integer) RETURNS integer LANGUAGE sql AS $$ SELECT s $$; "
        + "CREATE FUNCTION public.fin(s integer) RETURNS bigint LANGUAGE sql AS $$ SELECT s::bigint $$; ";

    [TestMethod]
    public void AnAggregateIsInTheModelKeyedLikeARoutine()
    {
        var model = Build(Head + "CREATE AGGREGATE public.mysum (integer) (SFUNC = public.step, STYPE = integer, FINALFUNC = public.fin);");

        var aggregate = model.Aggregates.Values.Single();
        Assert.AreEqual(DatabaseModel.CreateRoutineKey("public", "mysum", "integer"), aggregate.Key);
        Assert.AreEqual("mysum", aggregate.Name);
        CollectionAssert.AreEquivalent(new[] { "public.step", "public.fin" }, aggregate.ReferencedFunctions.ToArray());
        Assert.AreEqual(0, model.Diagnostics.Count, string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
    }

    [TestMethod]
    public void TheUserDefinedTypesTheAggregateUsesAreDependencies()
    {
        var model = Build(Head + "CREATE FUNCTION public.ms(s public.mood[], x public.mood) RETURNS public.mood[] LANGUAGE sql AS $$ SELECT s $$; CREATE AGGREGATE public.mymood (public.mood) (SFUNC = public.ms, STYPE = public.mood[]);");

        CollectionAssert.AreEqual(new[] { "public.mood" }, model.Aggregates.Values.Single().ReferencedTypes.ToArray());
    }

    [TestMethod]
    public void TheSameArgumentWrittenTwoWaysIsOneKeyAndOverloadsAreDifferentAggregates()
    {
        var first = Build(Head + "CREATE AGGREGATE public.a (integer) (SFUNC = public.step, STYPE = integer);").Aggregates.Keys.Single();
        var second = Build(Head + "CREATE AGGREGATE a (pg_catalog.int4) (SFUNC = public.step, STYPE = integer);").Aggregates.Keys.Single();
        var overloads = Build(Head + "CREATE AGGREGATE public.a (integer) (SFUNC = public.step, STYPE = integer); CREATE AGGREGATE public.a (text) (SFUNC = public.step, STYPE = integer);");

        Assert.AreEqual(first, second);
        Assert.AreEqual(2, overloads.Aggregates.Count);
    }

    [TestMethod]
    public void TwoAggregatesWithTheSameNameAndArgumentsAreAnError()
    {
        var model = Build(Head + "CREATE AGGREGATE public.a (integer) (SFUNC = public.step, STYPE = integer); CREATE AGGREGATE public.a (int4) (SFUNC = public.step, STYPE = integer, INITCOND = '1');");

        Assert.AreEqual(1, model.Diagnostics.Count(static d => d.Severity == SemanticDiagnosticSeverity.Error && d.Message.Contains("Duplicate Aggregate", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ACallToAnAggregateInAViewIsAKnownFunction()
    {
        var model = Build(Head + "CREATE AGGREGATE public.mysum (integer) (SFUNC = public.step, STYPE = integer); CREATE TABLE public.t (n integer); CREATE VIEW public.v AS SELECT public.mysum(n) AS total FROM public.t;");

        Assert.IsFalse(model.Diagnostics.Any(static d => d.Message.Contains("missing function", StringComparison.Ordinal)), string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
    }

    [TestMethod]
    public void ANonAggregateNameIsStillAMissingFunction()
    {
        var model = Build(Head + "CREATE TABLE public.t (n integer); CREATE VIEW public.v AS SELECT public.nothing(n) AS total FROM public.t;");

        Assert.IsTrue(model.Diagnostics.Any(static d => d.Message.Contains("missing function 'public.nothing'", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ThePostgreSqlDefaultsAreLeftOutOfTheComparisonForm()
    {
        var stated = Build(Head + "CREATE AGGREGATE public.a (integer) (SFUNC = public.step, STYPE = integer, PARALLEL = UNSAFE, FINALFUNC_MODIFY = READ_ONLY, SSPACE = 0);").Aggregates.Values.Single();
        var omitted = Build(Head + "CREATE AGGREGATE public.a (integer) (SFUNC = public.step, STYPE = integer);").Aggregates.Values.Single();
        var safe = Build(Head + "CREATE AGGREGATE public.a (integer) (SFUNC = public.step, STYPE = integer, PARALLEL = SAFE);").Aggregates.Values.Single();

        CollectionAssert.AreEqual(omitted.NormalizedOptions.ToArray(), stated.NormalizedOptions.ToArray());
        Assert.AreEqual("safe", safe.NormalizedOptions["parallel"]);
    }

    [TestMethod]
    public void AnOrderedSetAggregatesDefaultModifyIsReadWrite()
    {
        var options = AggregateOptions.Normalize(1, new Dictionary<string, string> { ["finalfunc_modify"] = "READ_WRITE", ["sfunc"] = "f" });

        Assert.IsFalse(options.ContainsKey("finalfunc_modify"));
        Assert.IsTrue(AggregateOptions.Normalize(-1, new Dictionary<string, string> { ["finalfunc_modify"] = "READ_WRITE" }).ContainsKey("finalfunc_modify"));
    }

    [TestMethod]
    public void AnAggregateIsAModelNodeSoTheGraphSeesIt()
    {
        var model = Build(Head + "CREATE AGGREGATE public.a (integer) (SFUNC = public.step, STYPE = integer);");

        Assert.IsTrue(model.GetAllNodes().Any(static node => node is AggregateDefinition));
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
