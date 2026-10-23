using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-207 step 2: expressions are kept as the author wrote them, so the project's text and PostgreSQL's rendering of the same expression
/// differ in spelling. Their comparison forms must not: an exclusion constraint's expression element (G-208) is compared by its tree, and a
/// routine parameter's typed-literal default (<c>interval '1 day'</c>, which PostgreSQL prints as <c>'1 day'::interval</c>) by its cast form.
/// A real change is still reported both ways.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ExpressionAsWrittenDiffTests
{
    private const string Exclusion = "CREATE TABLE t (a int, r int4range, EXCLUDE USING gist (({0}) WITH =, r WITH &&));";

    [TestMethod]
    public void AnExclusionExpressionAsWrittenAndAsPostgreSqlPrintsItIsTheSame()
    {
        var project = string.Format(Exclusion, "CASE WHEN a > 0 THEN 1 ELSE 2 END");
        var server = string.Format(Exclusion, "\nCASE\n    WHEN (a > 0) THEN 1\n    ELSE 2\nEND");

        Assert.AreEqual(0, ChangeCount(project, server));
        Assert.AreEqual(0, ChangeCount(server, project));
    }

    [TestMethod]
    public void AChangedExclusionExpressionIsReportedBothWays()
    {
        var before = string.Format(Exclusion, "CASE WHEN a > 0 THEN 1 ELSE 2 END");
        var after = string.Format(Exclusion, "CASE WHEN a > 0 THEN 1 ELSE 3 END");

        Assert.AreNotEqual(0, ChangeCount(before, after));
        Assert.AreNotEqual(0, ChangeCount(after, before));
    }

    [TestMethod]
    [DataRow("interval '1 day'", "'1 day'::interval")]
    [DataRow("date '2024-01-01'", "'2024-01-01'::date")]
    public void ATypedLiteralDefaultIsTheCastPostgreSqlPrints(string written, string printed)
    {
        const string f = "CREATE FUNCTION f(a {0} DEFAULT {1}) RETURNS int LANGUAGE sql AS $$ SELECT 1 $$;";
        var type = written.Split(' ')[0];

        Assert.AreEqual(0, ChangeCount(string.Format(f, type, written), string.Format(f, type, printed)));
        Assert.AreEqual(0, ChangeCount(string.Format(f, type, printed), string.Format(f, type, written)));
    }

    [TestMethod]
    public void AChangedTypedLiteralDefaultIsReported()
    {
        const string f = "CREATE FUNCTION f(a interval DEFAULT {0}) RETURNS int LANGUAGE sql AS $$ SELECT 1 $$;";

        Assert.AreEqual(1, ChangeCount(string.Format(f, "interval '1 day'"), string.Format(f, "'2 days'::interval")));
    }

    [TestMethod]
    public void AnArrayDefaultAsWrittenAndAsPostgreSqlPrintsItIsTheSame()
    {
        const string f = "CREATE FUNCTION f(c int[] DEFAULT {0}) RETURNS int LANGUAGE sql AS $$ SELECT 1 $$;";

        Assert.AreEqual(0, ChangeCount(string.Format(f, "'{}'"), string.Format(f, "'{}'::integer[]")));
        Assert.AreEqual(0, ChangeCount(string.Format(f, "'{}'::integer[]"), string.Format(f, "'{}'")));
    }

    private static int ChangeCount(string oldSql, string newSql)
    {
        var changes = new DiffEngine().Diff(Build(oldSql), Build(newSql));
        return changes.Added.Count + changes.Removed.Count + changes.Modified.Count;
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
