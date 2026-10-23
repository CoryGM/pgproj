using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-221: a table written with <c>LIKE</c> was a table with none of its source's columns, constraints, or indexes, so a database PostgreSQL made from the
/// statement looked nothing like the project. The copies are now in the model, so the table PostgreSQL makes, written out as the database has it, is no change
/// either way, and a change to the source is a change to the copies too.
/// </summary>
[TestClass]
public sealed class TableLikeDiffTests
{
    private const string Source = """
        CREATE TABLE public.a (id int PRIMARY KEY, n text DEFAULT 'x' NOT NULL, CHECK (id > 0));
        CREATE INDEX a_n ON public.a (n);
        COMMENT ON COLUMN public.a.n IS 'n comment';
        """;

    private const string Like = Source + "CREATE TABLE public.b (LIKE public.a INCLUDING ALL, extra int);";

    // What PostgreSQL makes of the LIKE, as the database reports it: every constraint named.
    private const string AsTheDatabaseHasIt = """
        CREATE TABLE public.a (id int NOT NULL, n text DEFAULT 'x' NOT NULL);
        ALTER TABLE public.a ADD CONSTRAINT a_id_check CHECK (id > 0);
        ALTER TABLE public.a ADD CONSTRAINT a_pkey PRIMARY KEY (id);
        CREATE INDEX a_n ON public.a (n);
        COMMENT ON COLUMN public.a.n IS 'n comment';
        CREATE TABLE public.b (id int NOT NULL, n text DEFAULT 'x' NOT NULL, extra int);
        ALTER TABLE public.b ADD CONSTRAINT a_id_check CHECK (id > 0);
        ALTER TABLE public.b ADD CONSTRAINT b_pkey PRIMARY KEY (id);
        CREATE INDEX b_n_idx ON public.b (n);
        COMMENT ON COLUMN public.b.n IS 'n comment';
        """;

    [TestMethod]
    public void TheTablePostgreSqlMakesIsNoChange()
        => Assert.AreEqual(string.Empty, Describe(new DiffEngine().Diff(Build(AsTheDatabaseHasIt), Build(Like))));

    [TestMethod]
    public void TheProjectIsNoChangeFromTheDatabaseTheOtherWayEither()
        => Assert.AreEqual(string.Empty, Describe(new DiffEngine().Diff(Build(Like), Build(AsTheDatabaseHasIt))));

    [TestMethod]
    public void AColumnAddedToTheSourceIsAddedToTheCopyToo()
    {
        var result = new DiffEngine().Diff(Build(Like), Build(Like.Replace("NOT NULL,", "NOT NULL, added int,", StringComparison.Ordinal)));

        CollectionAssert.AreEquivalent(new[] { "public.a.added", "public.b.added" }, result.Added.Select(static change => change.NodeId).ToArray());
    }

    private static string Describe(ChangeSet result)
        => string.Join("\n", result.Added.Select(static change => "+ " + change.NodeId)
            .Concat(result.Removed.Select(static change => "- " + change.NodeId))
            .Concat(result.Modified.Select(static change => "~ " + change.NodeId + ": " + string.Join("; ", change.Details.Select(static detail => detail.ToString())))));

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
