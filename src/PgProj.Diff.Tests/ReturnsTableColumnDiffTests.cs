using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-198: a <c>RETURNS TABLE</c> function's columns are compared, so a column added, removed, renamed, or retyped is a difference in both
/// directions, and the project's spelling and PostgreSQL's (<c>pg_get_functiondef</c>) are not.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ReturnsTableColumnDiffTests
{
    private const string F = "CREATE FUNCTION public.f(p int) RETURNS TABLE ({0}) LANGUAGE sql STABLE AS $$ SELECT 1, 'a' $$;";

    [TestMethod]
    [DataRow("id int, name text", "id int, name varchar", DisplayName = "column retyped")]
    [DataRow("id int, name text", "id int, label text", DisplayName = "column renamed")]
    [DataRow("id int, name text", "id int, name text, extra int", DisplayName = "column added")]
    [DataRow("id int, name text", "name text, id int", DisplayName = "columns reordered")]
    public void AChangedColumnIsReportedBothWays(string before, string after)
    {
        Assert.AreEqual(1, ModifiedCount(string.Format(F, before), string.Format(F, after)));
        Assert.AreEqual(1, ModifiedCount(string.Format(F, after), string.Format(F, before)));
    }

    [TestMethod]
    public void TheProjectsAndPostgreSqlsSpellingsAreTheSame()
    {
        const string server = """
            CREATE OR REPLACE FUNCTION public.f(p integer)
             RETURNS TABLE(id integer, name character varying)
             LANGUAGE sql
             STABLE
            AS $function$ SELECT 1, 'a' $function$
            """;

        Assert.AreEqual(0, ModifiedCount(string.Format(F, "id INT, name varchar"), server));
        Assert.AreEqual(0, ModifiedCount(server, string.Format(F, "id INT, name varchar")));
    }

    [TestMethod]
    public void ATypeUsedOnlyInAColumnIsReferenced()
    {
        var model = Build("CREATE TYPE public.mood AS ENUM ('a'); " + string.Format(F, "x int, m public.mood"));

        CollectionAssert.Contains(model.Functions.Values.Single().ReferencedTypes.ToArray(), "public.mood");
    }

    private static int ModifiedCount(string oldSql, string newSql)
        => new DiffEngine().Diff(Build(oldSql), Build(newSql)).Modified.Count;

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
