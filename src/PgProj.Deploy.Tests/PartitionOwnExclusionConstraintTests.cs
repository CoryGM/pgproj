using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-296: an exclusion constraint made on a partition first, under a name of its own, is adopted by the parent's equivalent constraint (PostgreSQL 17 and later), as G-237 does for a
/// partition's index and key. It keeps its name, and is created before the parent's.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class PartitionOwnExclusionConstraintTests
{
    private const string Schema = """
        CREATE TABLE public.idxpart (a int, b int4range) PARTITION BY RANGE (a);
        CREATE TABLE public.idxpart_1 (a int, b int4range);
        ALTER TABLE ONLY public.idxpart_1 ADD CONSTRAINT idxpart_1_own_excl EXCLUDE USING gist (a WITH =, b WITH &&);
        ALTER TABLE ONLY public.idxpart ATTACH PARTITION public.idxpart_1 FOR VALUES FROM (1) TO (10);
        ALTER TABLE ONLY public.idxpart ADD CONSTRAINT idxpart_excl EXCLUDE USING gist (a WITH =, b WITH &&);
        ALTER INDEX public.idxpart_excl ATTACH PARTITION public.idxpart_1_own_excl;
        """;

    [TestMethod]
    public void ThePartitionsConstraintWithItsOwnNameIsKept()
    {
        var model = Build(Schema);

        CollectionAssert.AreEquivalent(new[] { "idxpart_1_own_excl", "idxpart_excl" }, model.Constraints.Values.Select(static constraint => constraint.Name).ToArray());
    }

    [TestMethod]
    public void ThePartitionsConstraintIsCreatedBeforeTheParents()
    {
        var model = Build(Schema);
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(DatabaseModel.Empty, model), new DependencyAnalyzer().Analyze(DatabaseModel.Empty, model));
        var script = string.Join("\n", batch.Operations.Select(static operation => operation.GenerateSql()));

        Assert.IsGreaterThan(-1, script.IndexOf("idxpart_1_own_excl", StringComparison.Ordinal));
        Assert.IsLessThan(script.IndexOf("\"idxpart_excl\"", StringComparison.Ordinal), script.IndexOf("\"idxpart_1_own_excl\"", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void ACloneWithTheGeneratedNameIsStillThePartitionsCopyOfTheParents()
    {
        var model = Build("""
            CREATE TABLE public.idxpart (a int, b int4range) PARTITION BY RANGE (a);
            CREATE TABLE public.idxpart_1 (a int, b int4range);
            ALTER TABLE ONLY public.idxpart_1 ADD CONSTRAINT idxpart_1_a_b_excl EXCLUDE USING gist (a WITH =, b WITH &&);
            ALTER TABLE ONLY public.idxpart ATTACH PARTITION public.idxpart_1 FOR VALUES FROM (1) TO (10);
            ALTER TABLE ONLY public.idxpart ADD CONSTRAINT idxpart_excl EXCLUDE USING gist (a WITH =, b WITH &&);
            ALTER INDEX public.idxpart_excl ATTACH PARTITION public.idxpart_1_a_b_excl;
            """);

        CollectionAssert.AreEqual(new[] { "idxpart_excl" }, model.Constraints.Values.Select(static constraint => constraint.Name).ToArray());
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
