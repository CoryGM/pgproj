using PgProj.Dependencies;
using PgProj.Deploy;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Engine.Tests;

/// <summary>
/// G-265 (signed off by the user on 2026-10-13): the statements of a script that only a superuser can run, which <c>publish --execute</c> runs only with
/// <c>--allow-superuser-statements</c>: anything on an event trigger, a routine created or replaced <c>LEAKPROOF</c>, and a routine in an untrusted language.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SuperuserStatementsTests
{
    private const string EventTrigger = """
        CREATE FUNCTION public.on_ddl() RETURNS event_trigger LANGUAGE plpgsql AS $$ BEGIN END $$;
        CREATE EVENT TRIGGER audit_ddl ON ddl_command_end EXECUTE FUNCTION public.on_ddl();
        """;

    [TestMethod]
    public void AnEventTriggerALeakproofRoutineAndAnUntrustedLanguageAreFound()
    {
        var statements = Find(EventTrigger + """
            CREATE FUNCTION public.lp(a int) RETURNS int LANGUAGE sql IMMUTABLE LEAKPROOF AS $$ SELECT a $$;
            CREATE FUNCTION public.abs_of(int) RETURNS int LANGUAGE internal IMMUTABLE STRICT AS $$int4abs$$;
            CREATE FUNCTION public.py() RETURNS int LANGUAGE plpython3u AS $$ return 1 $$;
            CREATE FUNCTION public.plain() RETURNS int LANGUAGE sql AS $$ SELECT 1 $$;
            """, untrustedLanguages: ["plpython3u"]);

        CollectionAssert.AreEquivalent(
            new[]
            {
                "EventTrigger event trigger:audit_ddl: an event trigger is created, changed, or dropped only by a superuser",
                "Function public.lp(int4): only a superuser can make a routine LEAKPROOF",
                "Function public.abs_of(int4): only a superuser can create a routine in the untrusted language internal",
                "Function public.py(): only a superuser can create a routine in the untrusted language plpython3u",
            },
            statements.Select(static statement => $"{statement.NodeKind} {DatabaseModel.DisplayKey(statement.NodeId)}: {statement.Reason}").ToArray());
    }

    [TestMethod]
    public void CAndInternalAreUntrustedWhateverTheServerSays()
    {
        var statements = Find("CREATE FUNCTION public.h() RETURNS int LANGUAGE c AS 'lib', 'sym';", untrustedLanguages: []);

        Assert.AreEqual("only a superuser can create a routine in the untrusted language c", statements.Single().Reason);
    }

    [TestMethod]
    public void ATrustedLanguageAndAnOrdinaryProjectHaveNone()
    {
        Assert.IsEmpty(Find("""
            CREATE TABLE public.t (a int);
            CREATE FUNCTION public.f() RETURNS int LANGUAGE plpgsql AS $$ BEGIN RETURN 1; END $$;
            CREATE FUNCTION public.g() RETURNS int LANGUAGE plperl AS $$ return 1 $$;
            """, untrustedLanguages: ["plperlu"]));
    }

    [TestMethod]
    public void DroppingAnEventTriggerNeedsASuperuserToo()
    {
        var withTrigger = Build(EventTrigger);
        var without = Build("CREATE FUNCTION public.on_ddl() RETURNS event_trigger LANGUAGE plpgsql AS $$ BEGIN END $$;");

        var statements = SuperuserStatements.Find(Generate(withTrigger, without), []);

        Assert.AreEqual(ChangeNodeKind.EventTrigger, statements.Single().NodeKind);
    }

    [TestMethod]
    public void DisablingATablesInternalTriggersNeedsASuperuser()
    {
        const string table = "CREATE TABLE public.t (id integer);";
        var disabled = Build(table + "ALTER TABLE public.t DISABLE TRIGGER ALL;");

        var statement = Find(table + "ALTER TABLE public.t DISABLE TRIGGER ALL;", []).Single();
        Assert.AreEqual("only a superuser can enable or disable a table's internal (foreign-key) triggers (TRIGGER ALL)", statement.Reason);

        // A changed state needs one too; a table with the internal triggers enabled, or a project that states nothing, does not.
        Assert.HasCount(1, SuperuserStatements.Find(Generate(Build(table), disabled), []));
        Assert.IsEmpty(Find(table + "ALTER TABLE public.t ENABLE TRIGGER ALL;", []));
        Assert.IsEmpty(SuperuserStatements.Find(Generate(disabled, Build(table)), []));
    }

    [TestMethod]
    public void TheScriptHeaderNamesTheStatementsAndTheOption()
    {
        var header = SuperuserStatements.ScriptHeader(Find(EventTrigger, []));

        StringAssert.StartsWith(header, "-- pgproj: this script has statements that only a superuser can run (pgproj publish --execute runs them only with --allow-superuser-statements):");
        StringAssert.Contains(header, "--   [EventTrigger] event trigger:audit_ddl: an event trigger is created, changed, or dropped only by a superuser");
        Assert.AreEqual(string.Empty, SuperuserStatements.ScriptHeader([]));
    }

    private static IReadOnlyList<SuperuserStatement> Find(string sql, IReadOnlyCollection<string> untrustedLanguages)
        => SuperuserStatements.Find(Generate(DatabaseModel.Empty, Build(sql)), untrustedLanguages);

    private static OperationBatch Generate(DatabaseModel oldModel, DatabaseModel newModel)
        => new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
