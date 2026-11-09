namespace PgProj.Parser.Tests;

/// <summary>
/// G-335 (the user's decisions of 2026-11-09): the statements of a deploy script that do not belong in the transaction pgproj publishes in. The refused list is
/// PostgreSQL's own, checked on 14 and 18 (each statement between BEGIN and ROLLBACK); the trees' fields were read from libpg_query's output.
/// </summary>
[TestClass]
public sealed class TransactionControlTests
{
    [TestMethod]
    [DataRow("COMMIT;", "COMMIT (or END)")]
    [DataRow("END;", "COMMIT (or END)")]
    [DataRow("ROLLBACK;", "ROLLBACK (or ABORT)")]
    [DataRow("ABORT;", "ROLLBACK (or ABORT)")]
    [DataRow("PREPARE TRANSACTION 'x';", "PREPARE TRANSACTION")]
    [DataRow("COMMIT PREPARED 'x';", "COMMIT PREPARED")]
    [DataRow("ROLLBACK PREPARED 'x';", "ROLLBACK PREPARED")]
    public void AStatementThatEndsTheTransactionIsAnError(string sql, string what)
    {
        var found = PgQueryParser.FindTransactionControl(sql).Single();

        Assert.AreEqual(SqlDiagnosticSeverity.Error, found.Severity);
        StringAssert.StartsWith(found.Message, $"{what} in a deploy script ends the transaction pgproj publishes in");
        Assert.AreEqual(PgQueryParser.TransactionControlSource, found.Source);
    }

    [TestMethod]
    [DataRow("BEGIN;")]
    [DataRow("START TRANSACTION;")]
    [DataRow("BEGIN ISOLATION LEVEL SERIALIZABLE;")]
    public void ABeginIsAWarning(string sql)
    {
        var found = PgQueryParser.FindTransactionControl(sql).Single();

        Assert.AreEqual(SqlDiagnosticSeverity.Warning, found.Severity);
        StringAssert.StartsWith(found.Message, "BEGIN (or START TRANSACTION) in a deploy script starts a transaction inside the one pgproj publishes in");
    }

    [TestMethod]
    [DataRow("VACUUM t;", "VACUUM")]
    [DataRow("VACUUM;", "VACUUM")]
    [DataRow("VACUUM (ANALYZE) t;", "VACUUM")]
    [DataRow("CREATE DATABASE d;", "CREATE DATABASE")]
    [DataRow("DROP DATABASE IF EXISTS d;", "DROP DATABASE")]
    [DataRow("CREATE TABLESPACE ts LOCATION '/tmp';", "CREATE TABLESPACE")]
    [DataRow("DROP TABLESPACE IF EXISTS ts;", "DROP TABLESPACE")]
    [DataRow("ALTER SYSTEM SET work_mem = '4MB';", "ALTER SYSTEM")]
    [DataRow("ALTER SYSTEM RESET work_mem;", "ALTER SYSTEM")]
    [DataRow("CREATE INDEX CONCURRENTLY i ON t (a);", "CREATE INDEX CONCURRENTLY")]
    [DataRow("CREATE UNIQUE INDEX CONCURRENTLY IF NOT EXISTS i ON t (a);", "CREATE INDEX CONCURRENTLY")]
    [DataRow("DROP INDEX CONCURRENTLY IF EXISTS i;", "DROP INDEX CONCURRENTLY")]
    [DataRow("REINDEX TABLE CONCURRENTLY t;", "REINDEX ... CONCURRENTLY")]
    [DataRow("REINDEX (CONCURRENTLY) INDEX i;", "REINDEX ... CONCURRENTLY")]
    [DataRow("REINDEX DATABASE d;", "REINDEX DATABASE")]
    [DataRow("REINDEX SYSTEM d;", "REINDEX SYSTEM")]
    [DataRow("CLUSTER;", "CLUSTER without a table")]
    [DataRow("DISCARD ALL;", "DISCARD ALL")]
    public void AStatementPostgreSqlRefusesInATransactionIsAnError(string sql, string what)
    {
        var found = PgQueryParser.FindTransactionControl(sql).Single();

        Assert.AreEqual(SqlDiagnosticSeverity.Error, found.Severity);
        StringAssert.StartsWith(found.Message, $"{what} cannot run inside a transaction block, and pgproj runs the deploy scripts in one");
    }

    [TestMethod]
    [DataRow("SAVEPOINT s; RELEASE s; ROLLBACK TO s; RELEASE SAVEPOINT s; ROLLBACK TO SAVEPOINT s;")]
    [DataRow("ANALYZE t; REINDEX TABLE t; REINDEX INDEX i; CLUSTER t USING i; DISCARD PLANS; DROP INDEX i; CREATE INDEX i ON t (a);")]
    [DataRow("ALTER TYPE e ADD VALUE 'b'; SET search_path TO app; SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;")]
    [DataRow("DO $$ BEGIN COMMIT; END $$; CALL p();")]
    [DataRow("INSERT INTO log VALUES ('COMMIT'); -- COMMIT;")]
    [DataRow("SELEC 1; COMMIT;")]
    public void WhatBelongsInTheTransactionIsNotReported(string sql)
        => Assert.IsEmpty(PgQueryParser.FindTransactionControl(sql).ToArray(), "a savepoint, a statement allowed in a transaction, a DO block, a string or comment, or a script that does not parse (its syntax error is reported elsewhere)");

    [TestMethod]
    public void EachIsReportedAtItsStatement()
    {
        const string Sql = "CREATE TABLE before_commit (id int);\nCOMMIT;\nINSERT INTO before_commit VALUES (1);\n  VACUUM before_commit;\n";

        var found = PgQueryParser.FindTransactionControl(Sql);

        Assert.HasCount(2, found);
        Assert.AreEqual(2, found[0].LineNumber);
        Assert.AreEqual(1, found[0].Column);
        StringAssert.EndsWith(found[0].Message, ": COMMIT");
        Assert.AreEqual(4, found[1].LineNumber);
        Assert.AreEqual(3, found[1].Column);
        StringAssert.EndsWith(found[1].Message, ": VACUUM before_commit");
    }
}
