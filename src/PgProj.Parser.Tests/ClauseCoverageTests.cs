namespace PgProj.Parser.Tests;

/// <summary>
/// G-227: a clause of a modeled statement that no reader asks for is reported by <c>build</c> as a warning, instead of being dropped without a word.
/// </summary>
[TestClass]
public sealed class ClauseCoverageTests
{
    private static IReadOnlyList<SqlDiagnostic> Coverage(string sql)
        => new PgQueryParser().Parse(sql).Diagnostics.Where(static diagnostic => diagnostic.Source == ClauseCoverage.DiagnosticSource).ToArray();

    [TestMethod]
    public void AnUnreadClauseOfAModeledStatementWarns()
    {
        var warning = Coverage("CREATE TABLE t (a int) ON COMMIT DROP;").Single();

        Assert.AreEqual(SqlDiagnosticSeverity.Warning, warning.Severity);
        StringAssert.StartsWith(warning.Message, "CREATE TABLE: the ON COMMIT clause is not modeled; it is ignored.");
        StringAssert.Contains(warning.Message, "CREATE TABLE t (a int) ON COMMIT DROP");
        Assert.AreEqual(1, warning.LineNumber);
    }

    // A field no reader asks for is made up here, because every clause pgproj knows of is read (or listed as ignored).
    private static IReadOnlyList<string> CoverageOfTree(string statementJson)
    {
        using var document = System.Text.Json.JsonDocument.Parse(statementJson);
        return ClauseCoverage.Find(document.RootElement).ToArray();
    }

    [TestMethod]
    public void AFieldNoReaderAsksForOnAStatementIsReportedWithTheStatementName()
    {
        var messages = CoverageOfTree("""{ "IndexStmt": { "idxname": "i", "someNewClause": true } }""");

        Assert.HasCount(1, messages);
        Assert.AreEqual("CREATE INDEX: the 'someNewClause' (IndexStmt) clause is not modeled; it is ignored.", messages[0]);
    }

    [TestMethod]
    public void AFieldNoReaderAsksForInsideATableElementIsFound()
    {
        var messages = CoverageOfTree("""{ "CreateStmt": { "tableElts": [ { "ColumnDef": { "colname": "a", "someNewClause": 1 } } ] } }""");

        Assert.HasCount(1, messages);
        StringAssert.Contains(messages[0], "'someNewClause' (ColumnDef)");
    }

    [TestMethod]
    public void AClauseIsReportedOncePerStatementEvenWhenItRepeats()
    {
        var messages = CoverageOfTree("""{ "CreateStmt": { "tableElts": [ { "ColumnDef": { "colname": "a", "someNewClause": 1 } }, { "ColumnDef": { "colname": "b", "someNewClause": 2 } } ] } }""");

        Assert.HasCount(1, messages);
    }

    [TestMethod]
    public void ANodeTypeThatIsNotListedIsNotChecked()
    {
        Assert.IsEmpty(CoverageOfTree("""{ "SelectStmt": { "someNewClause": 1, "targetList": [ { "ResTarget": { "someNewClause": 2 } } ] } }"""));
    }

    [TestMethod]
    public void NullsNotDistinctIsReadSoItIsNotReported()
    {
        Assert.IsEmpty(Coverage("""
            CREATE TABLE t (a int, b int, c int UNIQUE NULLS NOT DISTINCT);
            CREATE UNIQUE INDEX t_a ON t (a) NULLS NOT DISTINCT;
            ALTER TABLE t ADD CONSTRAINT t_b UNIQUE NULLS NOT DISTINCT (b);
            """));
    }

    [TestMethod]
    public void TheClausesPgProjReadsDoNotWarn()
    {
        Assert.IsEmpty(Coverage("""
            CREATE TABLE IF NOT EXISTS public.t (
                id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                name text COLLATE "C" NOT NULL DEFAULT 'x' CHECK (name <> ''),
                n integer CONSTRAINT n_pos CHECK (n > 0) NOT VALID,
                ref integer REFERENCES public.t (id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED,
                CONSTRAINT t_u UNIQUE (name) INCLUDE (n) WITH (fillfactor = 70),
                EXCLUDE USING gist (n WITH =)
            ) WITH (fillfactor = 80) TABLESPACE pg_default;
            CREATE UNLOGGED TABLE p (a int, b int) PARTITION BY RANGE (a);
            CREATE TABLE p0 PARTITION OF p FOR VALUES FROM (0) TO (10);
            CREATE UNIQUE INDEX CONCURRENTLY IF NOT EXISTS i ON public.t USING btree (lower(name) DESC NULLS LAST, n) INCLUDE (id) WITH (fillfactor = 70) WHERE n > 1;
            CREATE OR REPLACE VIEW v WITH (security_barrier) AS SELECT 1 AS a WITH CASCADED CHECK OPTION;
            CREATE MATERIALIZED VIEW mv AS SELECT 1 AS a WITH NO DATA;
            CREATE SEQUENCE s AS bigint INCREMENT BY 2 START 5 MINVALUE 1 MAXVALUE 100 CACHE 3 CYCLE OWNED BY NONE;
            CREATE TYPE e AS ENUM ('a', 'b');
            CREATE TYPE c AS (x int, y text);
            CREATE DOMAIN d AS integer DEFAULT 1 NOT NULL CHECK (VALUE > 0);
            CREATE SCHEMA IF NOT EXISTS app;
            CREATE FUNCTION f(a integer, OUT b integer) RETURNS integer LANGUAGE sql STABLE STRICT PARALLEL SAFE COST 5 AS 'SELECT 1';
            CREATE TRIGGER trg AFTER INSERT OR UPDATE OF a ON t REFERENCING NEW TABLE AS nt FOR EACH STATEMENT EXECUTE FUNCTION f();
            CREATE POLICY pol ON t AS RESTRICTIVE FOR SELECT TO PUBLIC USING (true) ;
            CREATE RULE r AS ON INSERT TO t WHERE (NEW.a > 1) DO ALSO NOTHING;
            CREATE STATISTICS st (ndistinct) ON a, b FROM t;
            CREATE EXTENSION IF NOT EXISTS hstore;
            """));
    }

    [TestMethod]
    public void AFieldThatIsOnlyIgnoredWhileItHasItsDefaultValueWarnsOnceItDoesNot()
    {
        Assert.IsEmpty(Coverage("CREATE TABLE t (a int);"));
        Assert.HasCount(1, Coverage("CREATE TABLE t (a int) ON COMMIT PRESERVE ROWS;"));
    }

    [TestMethod]
    public void AStatementPgProjDoesNotModelIsNotChecked()
    {
        var tree = new PgQueryParser().Parse("CREATE FOREIGN TABLE ft (a int) SERVER srv;");

        Assert.IsFalse(tree.Diagnostics.Any(static diagnostic => diagnostic.Source == ClauseCoverage.DiagnosticSource), string.Join("; ", tree.Diagnostics.Select(static d => d.Message)));
    }

    [TestMethod]
    public void EveryIgnoredFieldHasAReasonAndNamesAKnownNodeType()
    {
        foreach (var (key, reason) in ClauseCoverage.IgnoredFields)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(reason), key);
            Assert.IsTrue(key.Contains('.', StringComparison.Ordinal), key);
        }
    }
}
