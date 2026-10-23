using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-121 part 2: a temporal key is deployed with <c>WITHOUT OVERLAPS</c> after its last column, and a temporal foreign key with <c>PERIOD</c>
/// before the last column on each side, in their column order; a key that gains or loses it is dropped and added back.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class TemporalKeyDeployTests
{
    private const string Room = "CREATE TABLE public.room (id integer, during tsrange, CONSTRAINT k PRIMARY KEY (id{0}));";

    [TestMethod]
    public void ATemporalPrimaryKeyIsAddedWithoutOverlaps()
    {
        var statements = Statements(DatabaseModel.Empty, Build(string.Format(Room, ", during WITHOUT OVERLAPS")));

        Assert.IsTrue(statements.Any(static s => s.EndsWith("ADD CONSTRAINT \"k\"\nPRIMARY KEY (\"id\", \"during\" WITHOUT OVERLAPS);", StringComparison.Ordinal)), string.Join("\n---\n", statements));
    }

    [TestMethod]
    public void ATemporalUniqueIsAddedWithoutOverlapsAfterItsLastColumn()
    {
        var statements = Statements(
            DatabaseModel.Empty,
            Build("CREATE TABLE public.t (a integer, b integer, r tsrange, CONSTRAINT k UNIQUE (b, a, r WITHOUT OVERLAPS));"));

        Assert.IsTrue(statements.Any(static s => s.EndsWith("UNIQUE (\"b\", \"a\", \"r\" WITHOUT OVERLAPS);", StringComparison.Ordinal)), string.Join("\n---\n", statements));
    }

    [TestMethod]
    public void ATemporalForeignKeyIsAddedWithPeriodOnBothSides()
    {
        var statements = Statements(
            DatabaseModel.Empty,
            Build(string.Format(Room, ", during WITHOUT OVERLAPS") + " CREATE TABLE public.booking (room_id integer, during tsrange, CONSTRAINT bk FOREIGN KEY (room_id, PERIOD during) REFERENCES public.room (id, PERIOD during));"));

        var foreignKey = statements.Single(static s => s.Contains("FOREIGN KEY", StringComparison.Ordinal));
        StringAssert.Contains(foreignKey, "FOREIGN KEY (\"room_id\", PERIOD \"during\") REFERENCES \"public\".\"room\" (\"id\", PERIOD \"during\")");
    }

    [TestMethod]
    public void AnOrdinaryKeyHasNoTemporalClause()
    {
        var statements = Statements(
            DatabaseModel.Empty,
            Build("CREATE TABLE public.p (id integer, a integer, CONSTRAINT pk PRIMARY KEY (id), CONSTRAINT u UNIQUE (id, a)); CREATE TABLE public.c (pid integer, CONSTRAINT f FOREIGN KEY (pid) REFERENCES public.p (id));"));

        var script = string.Join("\n", statements);
        Assert.IsFalse(script.Contains("OVERLAPS", StringComparison.Ordinal) || script.Contains("PERIOD", StringComparison.Ordinal), script);
    }

    [TestMethod]
    [DataRow(", during", ", during WITHOUT OVERLAPS", "WITHOUT OVERLAPS);")]
    [DataRow(", during WITHOUT OVERLAPS", ", during", "\"during\");")]
    public void AKeyThatGainsOrLosesWithoutOverlapsIsDroppedAndAdded(string before, string after, string expectedEnding)
    {
        var statements = Statements(Build(string.Format(Room, before)), Build(string.Format(Room, after)));

        var script = string.Join("\n", statements);
        StringAssert.Contains(script, "DROP CONSTRAINT");
        StringAssert.Contains(script, "ADD CONSTRAINT");
        StringAssert.Contains(script, expectedEnding);
    }

    [TestMethod]
    public void TheSameTemporalKeyWrittenTwiceDeploysNothing()
    {
        Assert.IsEmpty(Statements(
            Build(string.Format(Room, ", during WITHOUT OVERLAPS")),
            Build(string.Format(Room, ",  during   WITHOUT OVERLAPS"))));
    }

    private static List<string> Statements(DatabaseModel oldModel, DatabaseModel newModel)
    {
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));
        return batch.Operations
            .Where(static operation => operation is not SchemaCreateOperation)
            .Select(static operation => operation.GenerateSql().Trim().Replace("\r\n", "\n", StringComparison.Ordinal))
            .ToList();
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
