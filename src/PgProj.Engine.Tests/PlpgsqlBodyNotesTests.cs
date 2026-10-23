using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Engine.Tests;

/// <summary>
/// G-303b: when a change drops, renames, or retypes something a PL/pgSQL body names, say so: PostgreSQL records no dependency for such a body, so nothing stops the change and the routine
/// fails when it runs. Information only; the script and its order are unchanged.
/// </summary>
[TestClass]
public sealed class PlpgsqlBodyNotesTests
{
    private const string Reader = """
        CREATE FUNCTION public.report() RETURNS numeric LANGUAGE plpgsql AS $$
        BEGIN
          RETURN (SELECT sum(o.total) FROM orders o JOIN customers c ON c.id = o.customer_id WHERE o.total > 0);
        END $$;
        """;

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);

    private static string[] Notes(string before, string after)
    {
        var changes = new DiffEngine().Diff(Build(before), Build(after));
        return [.. PlpgsqlBodyNotes.Find(changes, Build(after)).Select(static note => $"{note.RoutineKind} {note.RoutineKey}: {note.What}")];
    }

    [TestMethod]
    public void ADroppedTableOrViewABodyNamesIsNoted()
    {
        const string both = "CREATE TABLE orders (id int PRIMARY KEY, customer_id int, total numeric); CREATE TABLE customers (id int PRIMARY KEY);";

        var notes = Notes(both + Reader, "CREATE TABLE orders (id int PRIMARY KEY, customer_id int, total numeric);" + Reader);

        CollectionAssert.AreEqual(new[] { "function public.report(): table 'public.customers' is dropped" }, notes);
    }

    [TestMethod]
    public void ADroppedTablesColumnsAreNotNotedAsWellAndAReplacementViewIsNotADrop()
    {
        const string tables = "CREATE TABLE orders (id int PRIMARY KEY, customer_id int, total numeric); CREATE TABLE customers (id int PRIMARY KEY);";

        var dropped = Notes(tables + Reader, "CREATE TABLE customers (id int PRIMARY KEY);" + Reader);

        Assert.AreEqual(1, dropped.Count(static note => note.Contains("orders", StringComparison.Ordinal)), string.Join('\n', dropped));
        Assert.IsFalse(dropped.Any(static note => note.Contains("column", StringComparison.Ordinal)), "the table's own columns go with it");
    }

    [TestMethod]
    public void ADroppedRetypedOrRenamedColumnNamedThroughItsQualifierIsNoted()
    {
        const string customers = "CREATE TABLE customers (id int PRIMARY KEY);";
        const string before = "CREATE TABLE orders (id int PRIMARY KEY, customer_id int, total numeric, note text);" + customers + Reader;

        var dropped = Notes(before, "CREATE TABLE orders (id int PRIMARY KEY, customer_id int, note text);" + customers + Reader);
        var retyped = Notes(before, "CREATE TABLE orders (id int PRIMARY KEY, customer_id int, total text, note text);" + customers + Reader);
        var renamed = Notes(before, "CREATE TABLE orders (id int PRIMARY KEY, customer_id int,\n -- @renamed-from: total\n amount numeric, note text);" + customers + Reader);

        CollectionAssert.AreEqual(new[] { "function public.report(): column 'public.orders.total' is dropped" }, dropped);
        CollectionAssert.AreEqual(new[] { "function public.report(): column 'public.orders.total' is given another type" }, retyped);
        CollectionAssert.AreEqual(new[] { "function public.report(): column 'public.orders.total' is renamed" }, renamed);
    }

    [TestMethod]
    public void AColumnTheBodyDoesNotQualifyIsNotRead()
    {
        const string function = "CREATE FUNCTION public.count_it() RETURNS bigint LANGUAGE plpgsql AS $$ BEGIN RETURN (SELECT count(total) FROM orders); END $$;";

        var notes = Notes("CREATE TABLE orders (id int PRIMARY KEY, total numeric);" + function, "CREATE TABLE orders (id int PRIMARY KEY);" + function);

        CollectionAssert.AreEqual(Array.Empty<string>(), notes);
    }

    [TestMethod]
    public void ADroppedFunctionOrProcedureABodyCallsIsNotedButAReplacedOneIsNot()
    {
        const string caller = "CREATE FUNCTION public.run() RETURNS int LANGUAGE plpgsql AS $$ BEGIN CALL tidy(); RETURN helper(1); END $$;";

        var dropped = Notes(
            "CREATE FUNCTION public.helper(x int) RETURNS int LANGUAGE sql AS 'SELECT x'; CREATE PROCEDURE public.tidy() LANGUAGE sql AS 'SELECT 1';" + caller,
            caller);
        var replaced = Notes(
            "CREATE FUNCTION public.helper(x int) RETURNS int LANGUAGE sql AS 'SELECT x'; CREATE PROCEDURE public.tidy() LANGUAGE sql AS 'SELECT 1';" + caller,
            "CREATE FUNCTION public.helper(x bigint) RETURNS bigint LANGUAGE sql AS 'SELECT x'; CREATE PROCEDURE public.tidy() LANGUAGE sql AS 'SELECT 2';" + caller);

        CollectionAssert.AreEquivalent(
            new[] { "function public.run(): function 'public.helper' is dropped", "function public.run(): procedure 'public.tidy' is dropped" },
            dropped);
        CollectionAssert.AreEqual(Array.Empty<string>(), replaced);
    }

    [TestMethod]
    public void WhatABodyDoesNotNameOrAChangeThatKeepsItIsNotNoted()
    {
        const string both = "CREATE TABLE orders (id int PRIMARY KEY, customer_id int, total numeric); CREATE TABLE customers (id int PRIMARY KEY);";

        CollectionAssert.AreEqual(Array.Empty<string>(), Notes(both + Reader + "CREATE TABLE unrelated (x int);", both + Reader), "an unrelated drop");
        CollectionAssert.AreEqual(Array.Empty<string>(), Notes(both + Reader, both.Replace("total numeric", "total numeric, extra int", StringComparison.Ordinal) + Reader), "an added column");
        CollectionAssert.AreEqual(Array.Empty<string>(), Notes(both + Reader, both + Reader), "no change");
    }

    [TestMethod]
    public void AnUnqualifiedNameMatchesOnlyAnObjectOnTheRoutinesSearchPath()
    {
        const string reader = "CREATE FUNCTION public.f() RETURNS int LANGUAGE plpgsql AS $$ BEGIN RETURN (SELECT count(*) FROM thing); END $$;";

        var elsewhere = Notes("CREATE SCHEMA other; CREATE TABLE other.thing (x int);" + reader, "CREATE SCHEMA other;" + reader);
        var onPath = Notes("CREATE TABLE public.thing (x int);" + reader, reader);

        CollectionAssert.AreEqual(Array.Empty<string>(), elsewhere);
        CollectionAssert.AreEqual(new[] { "function public.f(): table 'public.thing' is dropped" }, onPath);
    }

    [TestMethod]
    public void TheMessageSaysWhyNothingStopsItAndNamesEachRoutineAndObject()
    {
        var text = PlpgsqlBodyNotes.Describe([new PlpgsqlBodyNote("function", "public.report()", "table 'public.customers' is dropped")], "This publish drops, renames, or retypes");

        StringAssert.StartsWith(text, "This publish drops, renames, or retypes what the body of a PL/pgSQL routine names.");
        StringAssert.Contains(text, "PostgreSQL records no dependency");
        StringAssert.Contains(text, "- function 'public.report()' names table 'public.customers' is dropped.");
    }
}
