using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-95: a routine with a SQL-standard body (<c>BEGIN ATOMIC ... END</c>, <c>RETURN expression</c>) is created with that body as written,
/// with no <c>AS</c> or quoting, where it used to be a TODO placeholder that <c>publish --execute</c> refused. The semicolons inside a
/// <c>BEGIN ATOMIC</c> body end the body's statements, not the script's, so the splitter keeps the routine whole.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SqlStandardRoutineBodyOperationTests
{
    private const string Project = """
        CREATE SCHEMA s;
        CREATE TABLE s.t (id int PRIMARY KEY, a int);
        CREATE FUNCTION s.total(p int) RETURNS bigint LANGUAGE sql STABLE BEGIN ATOMIC SELECT CASE WHEN p > 0 THEN sum(a) ELSE 0 END FROM s.t; SELECT count(*) FROM s.t; END;
        CREATE FUNCTION s.twice(x int) RETURNS int LANGUAGE sql IMMUTABLE RETURN x * 2;
        CREATE PROCEDURE s.clear() LANGUAGE sql BEGIN ATOMIC DELETE FROM s.t; END;
        """;

    [TestMethod]
    public void EachRoutineIsCreatedWithItsBodyAsWritten()
    {
        var statements = Statements();

        CollectionAssert.Contains(statements, "CREATE OR REPLACE FUNCTION \"s\".\"total\"(p int)\nRETURNS bigint\nLANGUAGE sql\nSTABLE\nBEGIN ATOMIC SELECT CASE WHEN p > 0 THEN sum(a) ELSE 0 END FROM s.t; SELECT count(*) FROM s.t; END;");
        CollectionAssert.Contains(statements, "CREATE OR REPLACE FUNCTION \"s\".\"twice\"(x int)\nRETURNS int\nLANGUAGE sql\nIMMUTABLE\nRETURN x * 2;");
        CollectionAssert.Contains(statements, "CREATE OR REPLACE PROCEDURE \"s\".\"clear\"()\nLANGUAGE sql\nBEGIN ATOMIC DELETE FROM s.t; END;");
        Assert.IsFalse(statements.Any(static statement => statement.Contains("TODO", StringComparison.Ordinal)), string.Join("\n", statements));
    }

    [TestMethod]
    public void TheSplitterKeepsEachRoutineWhole()
    {
        var script = string.Join("\n\n", Statements());

        var split = SqlStatementSplitter.Split(script);

        Assert.IsTrue(split.Any(static statement => statement.EndsWith("BEGIN ATOMIC SELECT CASE WHEN p > 0 THEN sum(a) ELSE 0 END FROM s.t; SELECT count(*) FROM s.t; END", StringComparison.Ordinal)), string.Join("\n---\n", split));
        Assert.IsTrue(split.Any(static statement => statement.EndsWith("BEGIN ATOMIC DELETE FROM s.t; END", StringComparison.Ordinal)), string.Join("\n---\n", split));
        Assert.IsFalse(split.Any(static statement => statement.StartsWith("SELECT", StringComparison.Ordinal) || statement == "END"), string.Join("\n---\n", split));
    }

    [TestMethod]
    [DataRow("-- s.f is recreated\nCREATE FUNCTION s.f() RETURNS int LANGUAGE sql BEGIN ATOMIC SELECT 1; SELECT 2; END; SELECT 3;", DisplayName = "after a reason comment")]
    [DataRow("create or replace procedure s.f() language sql begin atomic select case when true then 1 end; select 2; end; SELECT 3;", DisplayName = "lower case, with CASE ... END")]
    public void ABeginAtomicBodyEndsAtItsEnd(string script)
    {
        var split = SqlStatementSplitter.Split(script);

        Assert.HasCount(2, split, string.Join("\n---\n", split));
        Assert.AreEqual("SELECT 3", split[1]);
    }

    [TestMethod]
    [DataRow("BEGIN; SELECT 1; END;", 3, DisplayName = "a transaction block")]
    [DataRow("DO $$ BEGIN PERFORM 1; END $$; SELECT 2;", 2, DisplayName = "a DO block")]
    [DataRow("CREATE FUNCTION s.f() RETURNS int LANGUAGE plpgsql AS $$ BEGIN RETURN 1; END $$; SELECT 2;", 2, DisplayName = "a PL/pgSQL body")]
    [DataRow("SELECT 'begin atomic'; SELECT 2;", 2, DisplayName = "the words in a string")]
    public void BeginOutsideASqlStandardBodyStillSplitsAsBefore(string script, int count)
        => Assert.HasCount(count, SqlStatementSplitter.Split(script));

    private static List<string> Statements()
    {
        var live = Build(string.Empty);
        var project = Build(Project);
        return new ScriptGenerator().Generate(new DiffEngine().Diff(live, project), new DependencyAnalyzer().Analyze(live, project)).Operations
            .Select(static operation => operation.GenerateSql().Trim().Replace("\r\n", "\n", StringComparison.Ordinal))
            .ToList();
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
