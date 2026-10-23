using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-264 (PostgreSQL 18): a partition made first with its own NOT NULL constraint names and attached later keeps them. <c>CREATE TABLE child PARTITION OF parent</c> would give it the parent's
/// names, so such a partition is created with its columns and then attached.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class PartitionOwnNotNullNameTests
{
    private const string Parent = "CREATE TABLE public.parent (partid integer NOT NULL, b integer) PARTITION BY RANGE (partid);\n";

    [TestMethod]
    public void APartitionThatNamesItsOwnNotNullIsCreatedWithItsColumnsAndAttached()
    {
        var script = Script(Build(Parent + """
            CREATE TABLE public.child (partid integer CONSTRAINT child_partid_not_null NOT NULL, b integer);
            ALTER TABLE public.parent ATTACH PARTITION public.child FOR VALUES FROM (1) TO (10);
            """));

        StringAssert.Contains(script, "\"partid\" integer CONSTRAINT \"child_partid_not_null\" NOT NULL");
        StringAssert.Contains(script, "ALTER TABLE \"public\".\"parent\" ATTACH PARTITION \"public\".\"child\" FOR VALUES FROM (1) TO (10);");
        Assert.IsFalse(script.Contains("PARTITION OF", StringComparison.Ordinal), script);
        Assert.IsTrue(script.IndexOf("CREATE TABLE \"public\".\"child\"", StringComparison.Ordinal) < script.IndexOf("ATTACH PARTITION", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void APartitionThatNamesTheParentsNotNullIsStillPartitionOf()
    {
        var script = Script(Build(Parent + """
            CREATE TABLE public.child (partid integer CONSTRAINT parent_partid_not_null NOT NULL, b integer);
            ALTER TABLE public.parent ATTACH PARTITION public.child FOR VALUES FROM (1) TO (10);
            """));

        StringAssert.Contains(script, "CREATE TABLE \"public\".\"child\" PARTITION OF \"public\".\"parent\" FOR VALUES FROM (1) TO (10)");
    }

    [TestMethod]
    public void APartitionThatNamesNoNotNullIsStillPartitionOf()
    {
        var script = Script(Build(Parent + """
            CREATE TABLE public.child (partid integer NOT NULL, b integer);
            ALTER TABLE public.parent ATTACH PARTITION public.child FOR VALUES FROM (1) TO (10);
            CREATE TABLE public.child2 PARTITION OF public.parent FOR VALUES FROM (10) TO (20);
            """));

        StringAssert.Contains(script, "CREATE TABLE \"public\".\"child\" PARTITION OF");
        StringAssert.Contains(script, "CREATE TABLE \"public\".\"child2\" PARTITION OF");
        Assert.IsFalse(script.Contains("ATTACH PARTITION", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void AnAttachedPartitionKeepsItsOwnColumnsDefaultsAndGeneratedColumnsButNotAnIdentity()
    {
        var script = Script(Build("""
            CREATE TABLE public.p (id integer GENERATED ALWAYS AS IDENTITY NOT NULL, d integer DEFAULT 5, g integer GENERATED ALWAYS AS (id * 2) STORED) PARTITION BY RANGE (id);
            CREATE TABLE public.c (id integer CONSTRAINT mine NOT NULL, d integer DEFAULT 5, g integer GENERATED ALWAYS AS (id * 2) STORED);
            ALTER TABLE public.p ATTACH PARTITION public.c FOR VALUES FROM (1) TO (10);
            """));

        StringAssert.Contains(script, "\"id\" integer CONSTRAINT \"mine\" NOT NULL");
        StringAssert.Contains(script, "\"d\" integer DEFAULT 5");
        StringAssert.Contains(script, "GENERATED ALWAYS AS (id * 2) STORED");
        Assert.AreEqual(1, script.Split("AS IDENTITY").Length - 1, "only the parent's identity: " + script);
    }

    [TestMethod]
    public void ASubPartitionedPartitionWithItsOwnNameKeepsItsPartitionByClause()
    {
        var script = Script(Build(Parent + """
            CREATE TABLE public.mid (partid integer CONSTRAINT mid_nn NOT NULL, b integer) PARTITION BY RANGE (b);
            ALTER TABLE public.parent ATTACH PARTITION public.mid FOR VALUES FROM (1) TO (10);
            """));

        StringAssert.Contains(script, "PARTITION BY RANGE (");
        StringAssert.Contains(script, "ATTACH PARTITION \"public\".\"mid\"");
    }

    [TestMethod]
    public void TheNameOfAPartitionsNotNullIsComparedAgainstTheDatabase()
    {
        var project = Build(Parent + """
            CREATE TABLE public.child (partid integer CONSTRAINT child_partid_not_null NOT NULL, b integer);
            ALTER TABLE public.parent ATTACH PARTITION public.child FOR VALUES FROM (1) TO (10);
            """);
        var inherited = Build(Parent.Replace("partid integer NOT NULL", "partid integer CONSTRAINT parent_partid_not_null NOT NULL", StringComparison.Ordinal) + "CREATE TABLE public.child PARTITION OF public.parent FOR VALUES FROM (1) TO (10);");

        var difference = new DiffEngine().Diff(inherited, project);

        Assert.IsTrue(difference.Modified.Any(static change => change.NewNode is ColumnDefinition { Name: "partid", TableName: "child" }), "the changed name is a difference");
    }

    private static string Script(DatabaseModel newModel)
    {
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(DatabaseModel.Empty, newModel), new DependencyAnalyzer().Analyze(DatabaseModel.Empty, newModel));
        return string.Join("\n", batch.Operations.Where(static operation => operation is not SchemaCreateOperation).Select(static operation => operation.GenerateSql().Replace("\r\n", "\n", StringComparison.Ordinal))).Trim();
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
