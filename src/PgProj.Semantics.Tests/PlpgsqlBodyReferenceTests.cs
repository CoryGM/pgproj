using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-303a: what a PL/pgSQL body names that the project does not define is a warning, never an error, and never a dependency: PostgreSQL does not check the body when the routine is created,
/// so the order of creation is untouched and two routines may call each other. The reader is conservative so that a warning can be believed.
/// </summary>
[TestClass]
public sealed class PlpgsqlBodyReferenceTests
{
    private const string Tables = "CREATE TABLE orders (id int PRIMARY KEY, total numeric); CREATE VIEW big AS SELECT id FROM orders; CREATE SEQUENCE ticket_seq;";

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);

    private static string[] BodyWarnings(DatabaseModel model)
        => [.. model.Diagnostics.Where(static diagnostic => diagnostic.Message.StartsWith("The PL/pgSQL body", StringComparison.Ordinal)).Select(static diagnostic => diagnostic.Message)];

    private static string Function(string body, string returns = "int") => $"CREATE FUNCTION public.f() RETURNS {returns} LANGUAGE plpgsql AS $$ {body} $$;";

    [TestMethod]
    public void ATableViewSequenceAndFunctionTheProjectDefinesAreNotReported()
    {
        var model = Build(
            Tables + "CREATE FUNCTION helper(x int) RETURNS int LANGUAGE sql AS 'SELECT x'; CREATE PROCEDURE log_it(m text) LANGUAGE sql AS 'SELECT 1';"
            + Function("DECLARE v int; BEGIN SELECT total INTO v FROM orders; PERFORM id FROM big; PERFORM nextval('ticket_seq'); PERFORM helper(1); CALL log_it('x'); RETURN coalesce(v, length('x')); END"));

        CollectionAssert.AreEqual(Array.Empty<string>(), BodyWarnings(model));
    }

    [TestMethod]
    public void ATableOrFunctionTheProjectLacksIsAWarningNamingTheRoutineAndTheName()
    {
        var model = Build(Tables + Function("BEGIN INSERT INTO audit(msg) VALUES ('x'); PERFORM missing_fn(1); RETURN (SELECT count(*) FROM orders o JOIN nowhere n ON n.id = o.id); END"));

        var warnings = BodyWarnings(model);

        Assert.HasCount(3, warnings);
        Assert.IsTrue(warnings.Any(static warning => warning.Contains("function 'public.f()'", StringComparison.Ordinal) && warning.Contains("'audit'", StringComparison.Ordinal)), string.Join('\n', warnings));
        Assert.IsTrue(warnings.Any(static warning => warning.Contains("calls function 'missing_fn'", StringComparison.Ordinal)));
        Assert.IsTrue(warnings.Any(static warning => warning.Contains("'nowhere'", StringComparison.Ordinal)));
        Assert.IsTrue(model.Diagnostics.Where(static diagnostic => diagnostic.Message.StartsWith("The PL/pgSQL body", StringComparison.Ordinal)).All(static diagnostic => diagnostic.Severity == SemanticDiagnosticSeverity.Warning));
        Assert.IsFalse(model.HasErrors);
    }

    [TestMethod]
    public void AProcedureAndANameReportedOnceAreHandledToo()
    {
        var model = Build("CREATE PROCEDURE p() LANGUAGE plpgsql AS $$ BEGIN INSERT INTO gone VALUES (1); DELETE FROM gone; END $$;");

        var warnings = BodyWarnings(model);

        Assert.HasCount(1, warnings);
        StringAssert.Contains(warnings[0], "procedure 'public.p()'");
    }

    [TestMethod]
    public void WhatTheBodyCreatesItselfIsNotMissing()
        => CollectionAssert.AreEqual(
            Array.Empty<string>(),
            BodyWarnings(Build(Function("BEGIN CREATE TEMP TABLE scratch AS SELECT 1 AS a; INSERT INTO scratch VALUES (2); PERFORM a FROM scratch; DROP TABLE scratch; RETURN 1; END"))));

    [TestMethod]
    public void ATriggersTransitionTablesAreRelationsInItsFunction()
    {
        var model = Build(
            Tables
            + "CREATE FUNCTION audit_rows() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN INSERT INTO orders SELECT * FROM new_rows; RETURN NULL; END $$;"
            + "CREATE TRIGGER t AFTER INSERT ON orders REFERENCING NEW TABLE AS new_rows FOR EACH STATEMENT EXECUTE FUNCTION audit_rows();");

        CollectionAssert.AreEqual(Array.Empty<string>(), BodyWarnings(model));
    }

    [TestMethod]
    public void ASchemaTheProjectHasNothingInIsAnotherSystemsAndSystemSchemasAreBuiltIn()
    {
        var model = Build(
            Tables + Function("BEGIN PERFORM 1 FROM cron.job; PERFORM 1 FROM pg_catalog.pg_class; PERFORM 1 FROM information_schema.tables; PERFORM 1 FROM pg_stat_activity; RETURN 1; END"));

        CollectionAssert.AreEqual(Array.Empty<string>(), BodyWarnings(model));
    }

    [TestMethod]
    public void AQualifiedNameInASchemaTheProjectHasStillWarnsWhenItIsNotThere()
    {
        var model = Build(Tables + Function("BEGIN PERFORM 1 FROM public.missing; RETURN 1; END"));

        var warnings = BodyWarnings(model);

        Assert.HasCount(1, warnings);
        StringAssert.Contains(warnings[0], "'public.missing'");
    }

    [TestMethod]
    public void DynamicSqlIsNotRead()
        => CollectionAssert.AreEqual(Array.Empty<string>(), BodyWarnings(Build(Function("BEGIN EXECUTE 'select * from ' || quote_ident('dyn') || ' where x = 1'; RETURN 1; END"))));

    [TestMethod]
    public void AReturnQueryBodyAndATriggerFunctionAreReadAndTheirNamesChecked()
    {
        var model = Build(
            Tables
            + "CREATE FUNCTION s() RETURNS SETOF int LANGUAGE plpgsql AS $$ BEGIN RETURN QUERY SELECT id FROM absent; END $$;"
            + "CREATE FUNCTION t() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN NEW.id = (SELECT max(id) FROM also_absent); RETURN NEW; END $$;");

        var warnings = BodyWarnings(model);

        Assert.IsTrue(warnings.Any(static warning => warning.Contains("'absent'", StringComparison.Ordinal)), string.Join('\n', warnings));
        Assert.IsTrue(warnings.Any(static warning => warning.Contains("'also_absent'", StringComparison.Ordinal)), string.Join('\n', warnings));
    }

    [TestMethod]
    public void NothingTheBodyNamesIsADependencyAndTwoRoutinesMayCallEachOther()
    {
        var model = Build(
            "CREATE FUNCTION a() RETURNS int LANGUAGE plpgsql AS $$ BEGIN RETURN b() + (SELECT count(*) FROM later); END $$;"
            + "CREATE FUNCTION b() RETURNS int LANGUAGE plpgsql AS $$ BEGIN RETURN a(); END $$;"
            + "CREATE TABLE later (id int);");

        Assert.IsFalse(model.HasErrors, string.Join('\n', model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        foreach (var function in model.Functions.Values)
        {
            CollectionAssert.AreEqual(Array.Empty<string>(), function.ReferencedTables.ToArray(), function.Key);
            CollectionAssert.AreEqual(Array.Empty<string>(), function.ReferencedFunctions.ToArray(), function.Key);
        }
    }

    [TestMethod]
    public void ALanguageSqlBodyIsLeftToTheExistingChecks()
        => CollectionAssert.AreEqual(Array.Empty<string>(), BodyWarnings(Build("CREATE FUNCTION s() RETURNS int LANGUAGE sql AS 'SELECT count(*) FROM absent';")));
}
