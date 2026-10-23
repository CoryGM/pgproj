using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-197: the canonical form a routine's options are compared in. A clause stating PostgreSQL's default is the same as none, a <c>SET</c> list is
/// compared by name whatever the order or quoting it was written in, and a <c>FROM CURRENT</c> setting matches the value PostgreSQL stored for it.
/// </summary>
[TestClass]
public sealed class RoutineOptionsComparisonTests
{
    [TestMethod]
    public void AttributesListEveryOptionThatIsNotTheDefault()
    {
        var signature = Parse("create function f() returns setof int language sql stable strict security definer leakproof parallel safe cost 5 rows 42 as $$ select 1 $$;");

        Assert.AreEqual("STABLE STRICT SECURITY DEFINER LEAKPROOF PARALLEL SAFE COST 5 ROWS 42", RoutineSignatureComparison.Attributes(signature));
    }

    [TestMethod]
    public void DefaultsAreLeftOut()
    {
        var signature = Parse("create function f() returns setof int language sql not leakproof parallel unsafe cost 100 rows 1000 as $$ select 1 $$;");

        Assert.AreEqual("VOLATILE", RoutineSignatureComparison.Attributes(signature));
    }

    [TestMethod]
    public void ConfigurationIsByNameWithTheValuesJoined()
    {
        var signature = Parse("create function f() returns int language sql set work_mem = '64MB' set search_path = pg_catalog, \"My S\" as $$ select 1 $$;");

        Assert.AreEqual("search_path = pg_catalog, My S; work_mem = 64MB", RoutineSignatureComparison.Configuration(signature));
    }

    [TestMethod]
    public void TheLastValueForANameIsTheOneKept()
    {
        var signature = Parse("create function f() returns int language sql set work_mem = '1MB' set work_mem = '2MB' as $$ select 1 $$;");

        Assert.AreEqual("work_mem = 2MB", RoutineSignatureComparison.Configuration(signature));
    }

    [TestMethod]
    public void AValueComparedWithFromCurrentIsFromCurrent()
    {
        var live = Parse("create function f() returns int language sql set work_mem = '4MB' set search_path = public as $$ select 1 $$;");
        var project = Parse("create function f() returns int language sql set work_mem from current set search_path = public as $$ select 1 $$;");

        Assert.AreEqual("search_path = public; work_mem FROM CURRENT", RoutineSignatureComparison.Configuration(live, project));
        Assert.AreEqual(RoutineSignatureComparison.Configuration(live, project), RoutineSignatureComparison.Configuration(project, live));
        Assert.AreEqual("search_path = public; work_mem = 4MB", RoutineSignatureComparison.Configuration(live));
    }

    [TestMethod]
    public void NoSettingsIsEmpty()
    {
        Assert.AreEqual(string.Empty, RoutineSignatureComparison.Configuration(Parse("create function f() returns int language sql as $$ select 1 $$;")));
        Assert.AreEqual(string.Empty, RoutineSignatureComparison.Configuration(null));
    }

    private static RoutineSignature Parse(string sql)
        => new PgQueryParser().Parse(sql).Functions.Single().Signature!;
}
