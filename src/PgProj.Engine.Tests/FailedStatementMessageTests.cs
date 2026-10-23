using Npgsql;

namespace PgProj.Engine.Tests;

/// <summary>
/// G-279: a statement that fails in <c>publish --execute</c> was reported as the server's message alone (<c>23502: column ... contains null
/// values</c>), leaving the user to search a script of thousands of statements. The error now also gives the server's detail and hint and the
/// statement, as one line with any password masked.
/// </summary>
[TestClass]
public sealed class FailedStatementMessageTests
{
    [TestMethod]
    public void TheMessageStartsAsBeforeAndNamesTheStatementAndTheHint()
    {
        var exception = new PostgresException("out of shared memory", "ERROR", "ERROR", "53200", hint: "You might need to increase \"max_locks_per_transaction\".");

        var message = ScriptExecution.DescribeFailedStatement(exception, "ALTER TABLE \"public\".\"t\"\n    ADD CONSTRAINT \"fk\" FOREIGN KEY (\"a\") REFERENCES \"public\".\"p\" (\"id\");");

        var lines = message.Split(Environment.NewLine);
        Assert.AreEqual("53200: out of shared memory", lines[0]);
        Assert.AreEqual("  Hint: You might need to increase \"max_locks_per_transaction\".", lines[1]);
        Assert.AreEqual("  The statement that failed: ALTER TABLE \"public\".\"t\" ADD CONSTRAINT \"fk\" FOREIGN KEY (\"a\") REFERENCES \"public\".\"p\" (\"id\");", lines[2]);
        Assert.AreEqual("  Nothing was applied: the script was rolled back.", lines[3]);
    }

    [TestMethod]
    public void ADetailIsShownUnlessNpgsqlRedactedIt()
    {
        var shown = ScriptExecution.DescribeFailedStatement(
            new PostgresException("could not create unique index \"u\"", "ERROR", "ERROR", "23505", detail: "Key (a)=(1) is duplicated."),
            "CREATE UNIQUE INDEX u ON public.t (a);");
        var redacted = ScriptExecution.DescribeFailedStatement(
            new PostgresException("could not create unique index \"u\"", "ERROR", "ERROR", "23505", detail: "Detail redacted as it may contain sensitive data. Specify 'Include Error Detail' in the connection string to include this information."),
            "CREATE UNIQUE INDEX u ON public.t (a);");

        StringAssert.Contains(shown, "  Detail: Key (a)=(1) is duplicated.");
        Assert.DoesNotContain("Detail", redacted.Replace("unique index", string.Empty, StringComparison.Ordinal));
    }

    [TestMethod]
    public void APasswordInTheStatementIsMaskedAndALongStatementIsShortened()
    {
        var message = ScriptExecution.DescribeFailedStatement(
            new PostgresException("syntax error", "ERROR", "ERROR", "42601"),
            "CREATE ROLE app LOGIN PASSWORD 'hunter2secret' " + new string('x', 300) + ";");

        Assert.DoesNotContain("hunter2secret", message);
        StringAssert.Contains(message, "*****");
        StringAssert.Contains(message, "...");
    }
}
