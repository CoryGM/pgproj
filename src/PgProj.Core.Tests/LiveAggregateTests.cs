using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core.Tests;

/// <summary>
/// G-130: a live database's user-defined aggregates are written to the snapshot and to <c>init</c>'s scaffold as <c>CREATE AGGREGATE</c>, in a file of their own under the
/// schema's <c>AGGREGATE</c> folder, and read back as aggregates that compare with a project's.
/// </summary>
[TestClass]
public sealed class LiveAggregateTests
{
    private static readonly LiveFunctionSnapshot Step = new(
        "public",
        "step",
        "CREATE OR REPLACE FUNCTION public.step(s integer, x integer) RETURNS integer LANGUAGE sql AS $$ SELECT s $$",
        IdentityArguments: "s integer, x integer");

    private static LiveAggregateSnapshot Sum() => new(
        "public", "mysum", "integer", "n", -1, "public.step", "integer", 0, null, false, "r", null, null, null, "0", null, null, null, 0, null, false, "r", null, null, "u");

    [TestMethod]
    public void AnOrdinaryAggregateIsWrittenWithOnlyWhatIsNotADefault()
    {
        Assert.AreEqual(
            "CREATE AGGREGATE public.mysum (integer) (SFUNC = public.step, STYPE = integer, INITCOND = '0');",
            LiveDatabaseModelLoader.BuildCreateAggregateStatement(Sum()));
    }

    [TestMethod]
    public void EveryOptionIsWritten()
    {
        var sql = LiveDatabaseModelLoader.BuildCreateAggregateStatement(Sum() with
        {
            StateSpace = 64,
            FinalFunction = "public.fin",
            FinalExtra = true,
            FinalModify = "w",
            CombineFunction = "public.cf",
            SerialFunction = "public.sr",
            DeserialFunction = "public.ds",
            MovingStateFunction = "public.msf",
            MovingInverseFunction = "public.mif",
            MovingStateType = "integer",
            MovingStateSpace = 10,
            MovingFinalFunction = "public.mff",
            MovingFinalExtra = true,
            MovingFinalModify = "s",
            MovingInitialValue = "0",
            SortOperator = "pg_catalog.>",
            Parallel = "s",
        });

        foreach (var expected in new[]
        {
            "SSPACE = 64", "FINALFUNC = public.fin", "FINALFUNC_EXTRA", "FINALFUNC_MODIFY = READ_WRITE", "COMBINEFUNC = public.cf", "SERIALFUNC = public.sr", "DESERIALFUNC = public.ds",
            "MSFUNC = public.msf", "MINVFUNC = public.mif", "MSTYPE = integer", "MSSPACE = 10", "MFINALFUNC = public.mff", "MFINALFUNC_EXTRA", "MFINALFUNC_MODIFY = SHAREABLE",
            "MINITCOND = '0'", "SORTOP = OPERATOR(pg_catalog.>)", "PARALLEL = SAFE",
        })
        {
            StringAssert.Contains(sql, expected);
        }
    }

    [TestMethod]
    public void AStarAnOrderedSetAndAHypotheticalAggregateAreWritten()
    {
        var star = LiveDatabaseModelLoader.BuildCreateAggregateStatement(Sum() with { Name = "c", Arguments = string.Empty, InitialValue = null });
        var ordered = LiveDatabaseModelLoader.BuildCreateAggregateStatement(Sum() with { Name = "o", Arguments = "double precision ORDER BY double precision", Kind = "o", DirectArguments = 1, FinalModify = "w", InitialValue = null });
        var hypothetical = LiveDatabaseModelLoader.BuildCreateAggregateStatement(Sum() with { Name = "h", Arguments = "VARIADIC \"any\" ORDER BY VARIADIC \"any\"", Kind = "h", DirectArguments = 1, FinalModify = "w", InitialValue = null });

        StringAssert.Contains(star, "CREATE AGGREGATE public.c (*)");
        StringAssert.Contains(ordered, "(double precision ORDER BY double precision)");
        Assert.IsFalse(ordered.Contains("FINALFUNC_MODIFY", StringComparison.Ordinal), "READ_WRITE is what an ordered-set aggregate gets.");
        StringAssert.Contains(hypothetical, "HYPOTHETICAL");
    }

    [TestMethod]
    public void AnAggregateIsReadBackAndComparesWithTheProjectsOwn()
    {
        var live = Load(Sum());
        var project = new SemanticPipeline().Execute([new PgQueryParser().Parse(
            "CREATE FUNCTION public.step(s integer, x integer) RETURNS integer LANGUAGE sql AS $$ SELECT s $$; CREATE AGGREGATE mysum (int4) (STYPE = integer, SFUNC = step, INITCOND = '0', PARALLEL = UNSAFE);")]);

        var aggregate = live.Aggregates.Values.Single();
        Assert.AreEqual("mysum", aggregate.Name);
        Assert.AreEqual(project.Aggregates.Keys.Single(), aggregate.Key);
        var expected = project.Aggregates.Values.Single().NormalizedOptions;
        Assert.AreEqual(expected["stype"], aggregate.NormalizedOptions["stype"]);
        Assert.AreEqual(expected["initcond"], aggregate.NormalizedOptions["initcond"]);
        Assert.AreEqual(expected.Count, aggregate.NormalizedOptions.Count, "PostgreSQL's defaults are left out of both.");
        Assert.AreEqual(0, live.Diagnostics.Count, string.Join("; ", live.Diagnostics.Select(static d => d.Message)));
    }

    [TestMethod]
    public void TheScaffoldPutsEachAggregateInAFileOfItsOwnUnderItsSchemaAndOverloadsGetANumber()
    {
        var files = LiveDatabaseModelLoader.BuildScaffoldFiles(
            [new LiveSchemaSnapshot("public")], [], [], [], [], [Step], [], [], [], [], [],
            aggregates: [Sum(), Sum() with { Arguments = "text" }]);

        var paths = files.Select(static f => f.RelativePath).Where(static path => path.Contains("Aggregate", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();
        Assert.AreEqual(2, paths.Length, string.Join(", ", paths));
        StringAssert.StartsWith(paths[0], "Schema/public/Aggregate/mysum");
        var file = files.First(static f => f.RelativePath == "Schema/public/Aggregate/mysum.sql");
        Assert.IsEmpty(new LiveDatabaseModelLoader(new PgQueryParser(), new SemanticBuilder()).FindUnparseableFiles([file]));
    }

    [TestMethod]
    public void ADatabaseWithoutAggregatesScaffoldsNoAggregateFile()
    {
        var files = LiveDatabaseModelLoader.BuildScaffoldFiles([new LiveSchemaSnapshot("public")], [], [], [], [], [Step], [], [], [], [], []);

        Assert.IsFalse(files.Any(static f => f.RelativePath.Contains("Aggregate", StringComparison.Ordinal)));
    }

    private static DatabaseModel Load(LiveAggregateSnapshot aggregate)
    {
        var sql = LiveDatabaseModelLoader.GenerateSnapshotSql([new LiveSchemaSnapshot("public")], [], [], [], functions: [Step], aggregates: [aggregate]);
        return new LiveDatabaseModelLoader(new PgQueryParser(), new SemanticBuilder()).BuildModelFromSnapshotSql(sql);
    }
}
