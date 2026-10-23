using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-283: <c>ALTER SEQUENCE s OWNED BY t.c</c> written with an unqualified table means the table the search path finds, and a database reads the owning table back
/// qualified. It was compared as written, so the sequence showed as modified after every publish.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SequenceOwnedByResolutionTests
{
    private const string Unqualified = """
        CREATE TABLE instance_map (instance_id int NOT NULL);
        CREATE SEQUENCE instance_map_instance_id_seq;
        ALTER SEQUENCE instance_map_instance_id_seq OWNED BY instance_map.instance_id;
        """;

    private const string Qualified = """
        CREATE TABLE public.instance_map (instance_id int NOT NULL);
        CREATE SEQUENCE public.instance_map_instance_id_seq;
        ALTER SEQUENCE public.instance_map_instance_id_seq OWNED BY public.instance_map.instance_id;
        """;

    [TestMethod]
    public void AnUnqualifiedOwningTableIsTheSameAsTheQualifiedOne()
    {
        var unqualified = Build(Unqualified);
        var qualified = Build(Qualified);

        Assert.AreEqual(0, ChangeCount(new DiffEngine().Diff(qualified, unqualified)));
        Assert.AreEqual(0, ChangeCount(new DiffEngine().Diff(unqualified, qualified)));
        Assert.AreEqual(0, ChangeCount(new DiffEngine().Diff(unqualified, unqualified)));
    }

    [TestMethod]
    public void TheTableIsDeployedAsWritten()
    {
        var sequence = Build(Unqualified).Sequences.Values.Single();

        Assert.AreEqual("instance_map", sequence.OwnedByTable);
        Assert.AreEqual("public.instance_map", sequence.ResolvedOwnedByTable);
        Assert.IsNull(Build(Qualified).Sequences.Values.Single().ResolvedOwnedByTable);
    }

    [TestMethod]
    public void ADifferentOwningTableOrColumnIsStillAChange()
    {
        var other = Build("""
            CREATE TABLE other_map (instance_id int NOT NULL, x int);
            CREATE SEQUENCE instance_map_instance_id_seq;
            ALTER SEQUENCE instance_map_instance_id_seq OWNED BY other_map.instance_id;
            """);
        var otherColumn = Build("""
            CREATE TABLE instance_map (instance_id int NOT NULL, x int);
            CREATE SEQUENCE instance_map_instance_id_seq;
            ALTER SEQUENCE instance_map_instance_id_seq OWNED BY instance_map.x;
            """);
        var unqualified = Build(Unqualified);

        Assert.IsGreaterThan(0, ChangeCount(new DiffEngine().Diff(unqualified, other)));
        Assert.IsGreaterThan(0, ChangeCount(new DiffEngine().Diff(unqualified, otherColumn)));
    }

    [TestMethod]
    public void TheSearchPathDecidesWhichSchemaAnUnqualifiedTableIsIn()
    {
        var trees = new[] { new PgQueryParser("app").Parse("""
            CREATE SCHEMA app;
            CREATE TABLE instance_map (instance_id int NOT NULL);
            CREATE SEQUENCE instance_map_instance_id_seq;
            ALTER SEQUENCE instance_map_instance_id_seq OWNED BY instance_map.instance_id;
            """) };

        var sequence = new SemanticPipeline().Execute(trees, searchPath: ["app", "public"]).Sequences.Values.Single();

        Assert.AreEqual("app.instance_map", sequence.ResolvedOwnedByTable);
    }

    private static int ChangeCount(ChangeSet diff) => diff.Added.Count + diff.Removed.Count + diff.Modified.Count;

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
