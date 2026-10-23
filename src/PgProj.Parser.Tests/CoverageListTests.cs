namespace PgProj.Parser.Tests;

/// <summary>
/// G-300: the notes' list of statements that are not modeled names every statement <c>build</c> reports, so a statement added to the reader, or one that
/// becomes modeled, cannot leave the page stale.
/// </summary>
[TestClass]
public sealed class CoverageListTests
{
    /// <summary>Statements the reader names in its own cases rather than in its table.</summary>
    private static readonly string[] CaseKinds =
    [
        "CREATE TYPE (base type)",
        "CREATE TYPE (shell type)",
        "CREATE COLLATION ... FROM",
        "ALTER TEXT SEARCH CONFIGURATION ... REPLACE",
        "CREATE SCHEMA ... AUTHORIZATION",
        "SET search_path",
        "RESET search_path",
        "SELECT INTO",
        "CREATE TABLE AS",
    ];

    [TestMethod]
    public void TheNotesNameEveryStatementBuildReports()
    {
        var page = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "docs", "notes", "unmodeled-statements.md"));

        var missing = UnmodeledStatementReader.ReportedStatementKinds.Concat(CaseKinds)
            .Where(kind => !page.Contains($"`{kind}`", StringComparison.Ordinal))
            .ToArray();

        Assert.AreEqual(0, missing.Length, "Not in docs/notes/unmodeled-statements.md: " + string.Join(", ", missing));
    }

    [TestMethod]
    public void EachCaseKindIsReallyReported()
    {
        string[] statements =
        [
            "CREATE TYPE t (INPUT = f_in, OUTPUT = f_out);",
            "CREATE TYPE t;",
            "CREATE COLLATION c FROM \"C\";",
            "ALTER TEXT SEARCH CONFIGURATION c ALTER MAPPING REPLACE english_stem WITH simple;",
            "CREATE SCHEMA s AUTHORIZATION CURRENT_USER;",
            "SET search_path TO a;",
            "RESET search_path;",
            "SELECT 1 INTO t;",
            "CREATE TABLE t2 AS SELECT 1;",
        ];

        for (var index = 0; index < statements.Length; index++)
        {
            var messages = new PgQueryParser().Parse(statements[index]).Diagnostics.Select(static diagnostic => diagnostic.Message);
            Assert.IsTrue(messages.Any(message => message.StartsWith(CaseKinds[index] + " ", StringComparison.Ordinal)), $"{statements[index]}: {string.Join(" | ", messages)}");
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "pgproj.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("pgproj.slnx was not found above " + AppContext.BaseDirectory);
    }
}
