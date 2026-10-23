using PgProj.Parser;

namespace PgProj.Parser.Tests;

/// <summary>
/// G-106: a top-level statement pgproj does not model was skipped without a trace, so <c>build</c> passed a project whose event
/// triggers, rules, and data statements were never deployed. Each is now one warning naming the statement, and nothing is read
/// from it.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class UnmodeledStatementParsingTests
{
    [TestMethod]
    [DataRow("CREATE FOREIGN DATA WRAPPER w;", "CREATE FOREIGN DATA WRAPPER")]
    [DataRow("CREATE COLLATION public.c FROM \"C\";", "CREATE COLLATION ... FROM")]
    [DataRow("CREATE ROLE r;", "CREATE ROLE")]
    [DataRow("ALTER STATISTICS public.st SET STATISTICS 100;", "ALTER STATISTICS")]
    [DataRow("CREATE TEXT SEARCH TEMPLATE public.ts (INIT = a, LEXIZE = b);", "CREATE TEXT SEARCH TEMPLATE")]
    [DataRow("CREATE TABLESPACE tbs LOCATION '/x';", "CREATE TABLESPACE")]
    [DataRow("ALTER SEQUENCE public.s RESTART WITH 10;", "ALTER SEQUENCE")]
    [DataRow("ALTER LANGUAGE plpgsql OWNER TO bob;", "ALTER ... OWNER TO")]
    [DataRow("ALTER TYPE public.e ADD VALUE 'x';", "ALTER TYPE ... VALUE")]
    [DataRow("ALTER FUNCTION public.f() SECURITY DEFINER;", "ALTER FUNCTION")]
    [DataRow("ALTER TABLE public.t RENAME TO u;", "ALTER ... RENAME")]
    [DataRow("COMMENT ON LANGUAGE plpgsql IS 'x';", "COMMENT ON LANGUAGE")]
    [DataRow("COMMENT ON RULE r ON public.t IS 'x';", "COMMENT ON RULE")]
    [DataRow("SECURITY LABEL ON TABLE public.t IS 'x';", "SECURITY LABEL")]
    public void AnUnmodeledStatementIsOneWarningNamingIt(string sql, string kind)
    {
        var tree = Parse(sql);

        var diagnostic = tree.Diagnostics.Single();
        Assert.AreEqual(SqlDiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.AreEqual("UnmodeledStatement", diagnostic.Source);
        Assert.AreEqual($"{kind} is not modeled; the statement is ignored: {sql.TrimEnd(';')}", diagnostic.Message);
        Assert.AreEqual(1, diagnostic.LineNumber);
        Assert.IsEmpty(tree.Nodes);
    }

    [TestMethod]
    [DataRow("INSERT INTO public.t VALUES (1);", "INSERT")]
    [DataRow("UPDATE public.t SET a = 1;", "UPDATE")]
    [DataRow("DELETE FROM public.t;", "DELETE")]
    [DataRow("TRUNCATE public.t;", "TRUNCATE")]
    [DataRow("CREATE TABLE public.ta AS SELECT 1 AS x;", "CREATE TABLE AS")]
    [DataRow("SELECT 1 AS n INTO public.made;", "SELECT INTO")]
    [DataRow("SELECT pg_catalog.set_config('search_path', '', false);", "SELECT")]
    [DataRow("DROP TABLE IF EXISTS public.t;", "DROP TABLE")]
    [DataRow("DO $$ BEGIN END $$;", "DO")]
    [DataRow("CALL public.p();", "CALL")]
    public void ADataOrCodeStatementPointsToTheDeployScripts(string sql, string kind)
    {
        var tree = Parse(sql);

        var diagnostic = tree.Diagnostics.Single();
        Assert.AreEqual(SqlDiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.AreEqual(
            $"{kind} is not modeled; the statement is ignored (to run it on every publish, put it in the predeploy or postdeploy script): {sql.TrimEnd(';')}",
            diagnostic.Message);
        Assert.IsEmpty(tree.Nodes);
    }

    [TestMethod]
    [DataRow("SET statement_timeout = 0;")]
    [DataRow("SET client_encoding = 'UTF8';")]
    [DataRow("RESET statement_timeout;")]
    [DataRow("SET ROLE postgres;")]
    [DataRow("BEGIN; COMMIT;")]
    [DataRow("CREATE SCHEMA s;")]
    [DataRow("CREATE SCHEMA IF NOT EXISTS s;")]
    public void SessionSettingsTransactionControlAndAPlainCreateSchemaAreSilent(string sql)
    {
        Assert.IsEmpty(Parse(sql).Diagnostics);
    }

    [TestMethod]
    [DataRow("SET search_path = app, public;", "SET search_path")]
    [DataRow("SET search_path TO app;", "SET search_path")]
    [DataRow("RESET search_path;", "RESET search_path")]
    public void SettingTheSearchPathWarnsThatTheProjectSearchPathIsUsed(string sql, string kind)
    {
        var diagnostic = Parse(sql).Diagnostics.Single();

        Assert.AreEqual(SqlDiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.AreEqual(
            $"{kind} is not modeled; the statement is ignored, and unqualified names follow the project's searchPath: {sql.TrimEnd(';')}",
            diagnostic.Message);
    }

    [TestMethod]
    // G-146 models a schema's owner when it is a named role; AUTHORIZATION CURRENT_USER names whoever runs the script, which cannot be compared.
    public void ASchemaCreatedForCurrentUserWarnsThatTheOwnerIsIgnoredAndItsObjectsAreStillRead()
    {
        var tree = Parse("CREATE SCHEMA app AUTHORIZATION CURRENT_USER CREATE TABLE t (a int);");

        var diagnostic = tree.Diagnostics.Single();
        Assert.AreEqual(
            "CREATE SCHEMA ... AUTHORIZATION is not modeled; the schema's owner is ignored: CREATE SCHEMA app AUTHORIZATION CURRENT_USER CREATE TABLE t (a int)",
            diagnostic.Message);
        Assert.AreEqual("t", tree.Nodes.OfType<TableNode>().Single().Name);
        Assert.IsEmpty(tree.Owners);
    }

    [TestMethod]
    [DataRow("CREATE TABLE public.t (a int);")]
    [DataRow("ALTER TABLE public.t ADD COLUMN b int;")]
    [DataRow("ALTER TABLE public.t ENABLE ROW LEVEL SECURITY;")]
    [DataRow("CREATE VIEW public.v AS SELECT 1 AS one;")]
    [DataRow("CREATE MATERIALIZED VIEW public.mv AS SELECT 1 AS one;")]
    [DataRow("CREATE FUNCTION public.f() RETURNS int LANGUAGE sql AS 'select 1';")]
    [DataRow("CREATE PROCEDURE public.p() LANGUAGE sql AS 'select 1';")]
    [DataRow("CREATE TRIGGER tg AFTER INSERT ON public.t FOR EACH ROW EXECUTE FUNCTION public.f();")]
    [DataRow("CREATE POLICY p ON public.t USING (true);")]
    [DataRow("CREATE INDEX i ON public.t (a);")]
    [DataRow("CREATE SEQUENCE public.s;")]
    [DataRow("CREATE TYPE public.e AS ENUM ('a');")]
    [DataRow("CREATE TYPE public.c AS (a int);")]
    [DataRow("CREATE TYPE public.r AS RANGE (subtype = int);")]
    [DataRow("CREATE DOMAIN public.d AS int;")]
    [DataRow("CREATE EXTENSION IF NOT EXISTS citext;")]
    [DataRow("COMMENT ON TABLE public.t IS 'x';")]
    [DataRow("COMMENT ON COLUMN public.t.a IS 'x';")]
    [DataRow("COMMENT ON FUNCTION public.f() IS 'x';")]
    [DataRow("GRANT SELECT ON public.t TO someone;")]
    [DataRow("GRANT someone TO other;")]
    [DataRow("ALTER DEFAULT PRIVILEGES GRANT SELECT ON TABLES TO someone;")]
    public void AModeledStatementHasNoSuchWarning(string sql)
    {
        var tree = Parse(sql);

        Assert.IsFalse(tree.Diagnostics.Any(static diagnostic => diagnostic.Source == "UnmodeledStatement"), string.Join(" | ", tree.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.IsNotEmpty(tree.Nodes);
    }

    /// <summary>
    /// G-241: a shell type and a base type are not modeled (the user's decision of 2026-10-13). This replaces the shell type's row in
    /// <see cref="AModeledStatementHasNoSuchWarning"/>. The statement is still read, as a marker that names the functions to leave out with it.
    /// </summary>
    [TestMethod]
    [DataRow("CREATE TYPE public.sh;", "CREATE TYPE (shell type) is not modeled; the statement is ignored: CREATE TYPE public.sh")]
    [DataRow(
        "CREATE TYPE public.b (INPUT = public.bin, OUTPUT = public.bout);",
        "CREATE TYPE (base type) is not modeled (a base type needs C or internal functions and a superuser); the statement and the functions it names are ignored: CREATE TYPE public.b (INPUT = public.bin, OUTPUT = public.bout)")]
    public void AShellOrBaseTypeIsAWarningAndOnlyAMarker(string sql, string message)
    {
        var tree = Parse(sql);

        var diagnostic = tree.Diagnostics.Single();
        Assert.AreEqual(SqlDiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.AreEqual("UnmodeledStatement", diagnostic.Source);
        Assert.AreEqual(message, diagnostic.Message);
        Assert.IsInstanceOfType<BaseTypeNode>(tree.Nodes.Single());
    }

    [TestMethod]
    public void TheWarningQuotesTheStatementFromItsFirstWordOnOneShortenedLineAndGivesItsLine()
    {
        const string sql = """
            CREATE TABLE public.t (a int);

            -- keeps the history table in step
            /* written by hand */
            CREATE TEXT SEARCH TEMPLATE public.audit_sync_history_template
                (INIT = dsimple_init, LEXIZE = dsimple_lexize);
            """;

        var diagnostic = Parse(sql).Diagnostics.Single();

        Assert.AreEqual(
            "CREATE TEXT SEARCH TEMPLATE is not modeled; the statement is ignored: CREATE TEXT SEARCH TEMPLATE public.audit_sync_history_template (INIT = dsimple_i...",
            diagnostic.Message);
        Assert.AreEqual(5, diagnostic.LineNumber);
    }

    [TestMethod]
    public void AnUnmodeledStatementLeavesTheStatementsAroundItAlone()
    {
        var tree = Parse("CREATE TABLE public.a (x int); CREATE FOREIGN DATA WRAPPER wrap; CREATE TABLE public.b (y int);");

        CollectionAssert.AreEqual(new[] { "a", "b" }, tree.Nodes.OfType<TableNode>().Select(static table => table.Name).ToArray());
        Assert.HasCount(1, tree.Diagnostics);
    }

    [TestMethod]
    public void AForeignTablesColumnsNoLongerLeakIntoTheModel()
    {
        var tree = Parse("CREATE FOREIGN TABLE public.ft (a int, b text) SERVER s;");

        Assert.IsEmpty(tree.Nodes);
        StringAssert.StartsWith(tree.Diagnostics.Single().Message, "CREATE FOREIGN TABLE is not modeled");
    }

    [TestMethod]
    public void AnAlterDomainConstraintNoLongerBecomesAConstraintWithNoTable()
    {
        // ADD CONSTRAINT ... CHECK is modeled since G-233: it is a check of the domain, never a table's constraint with no table.
        var tree = Parse("ALTER DOMAIN public.d ADD CONSTRAINT pos CHECK (VALUE > 0) NOT VALID;");

        var alter = (AlterDomainCheckNode)tree.Nodes.Single();
        Assert.AreEqual("d", alter.DomainName);
        Assert.AreEqual("pos", alter.Check.Name);
        Assert.IsTrue(alter.Check.IsNotValid);
        Assert.AreEqual("VALUE > 0", alter.Check.RawExpression);
        Assert.IsEmpty(tree.Diagnostics);
    }

    [TestMethod]
    [DataRow("ALTER DOMAIN public.d SET NOT NULL;")]
    [DataRow("ALTER DOMAIN public.d DROP CONSTRAINT pos;")]
    [DataRow("ALTER DOMAIN public.d SET DEFAULT 1;")]
    [DataRow("ALTER DOMAIN public.d VALIDATE CONSTRAINT pos;")]
    public void EveryOtherAlterDomainActionIsStillNotModeled(string sql)
    {
        var tree = Parse(sql);

        Assert.IsEmpty(tree.Nodes);
        StringAssert.StartsWith(tree.Diagnostics.Single().Message, "ALTER DOMAIN is not modeled");
    }

    [TestMethod]
    [DataRow("ALTER VIEW public.v SET (security_barrier = true);", "ALTER VIEW")]
    [DataRow("ALTER MATERIALIZED VIEW public.mv SET (fillfactor = 50);", "ALTER MATERIALIZED VIEW")]
    [DataRow("ALTER TYPE public.comp ADD ATTRIBUTE y int;", "ALTER TYPE")]
    [DataRow("ALTER FOREIGN TABLE public.ft ADD COLUMN b int;", "ALTER FOREIGN TABLE")]
    [DataRow("ALTER INDEX public.i SET (fillfactor = 50);", "ALTER INDEX")]
    public void AnAlterOfSomethingOtherThanATableIsNamedForWhatItIs(string sql, string kind)
    {
        var tree = Parse(sql);

        Assert.AreEqual($"{kind} is not modeled; the statement is ignored: {sql.TrimEnd(';')}", tree.Diagnostics.Single().Message);
        Assert.IsEmpty(tree.Nodes);
    }

    private static SqlSyntaxTree Parse(string sql) => new PgQueryParser().Parse(sql);
}
