using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-194: a table's parents (<c>INHERITS</c>) are compared as a set, and its inherited columns as columns. The way the database scripts a child (its own columns,
/// the parents in another order) is not a difference; a parent added or removed, or a column a parent gains, is.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class TableInheritsDiffTests
{
    [TestMethod]
    [DataRow(
        "CREATE TABLE p (id int, name text); CREATE TABLE c (extra int) INHERITS (p);",
        "CREATE TABLE public.p (id integer, name text); CREATE TABLE public.c (extra integer) INHERITS (public.p);")]
    [DataRow(
        "CREATE TABLE a (x int); CREATE TABLE b (y int); CREATE TABLE c (z int) INHERITS (a, b);",
        "CREATE TABLE public.c (z integer) INHERITS (public.b, public.a); CREATE TABLE public.b (y integer); CREATE TABLE public.a (x integer);")]
    [DataRow(
        "CREATE TABLE p (name text DEFAULT 'x'); CREATE TABLE c (name text) INHERITS (p);",
        "CREATE TABLE public.p (name text DEFAULT 'x'::text); CREATE TABLE public.c (name text DEFAULT 'x'::text) INHERITS (public.p);")]
    public void TheSameInheritanceIsNoChange(string project, string live)
    {
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(Build(live), Build(project))), Dump(new DiffEngine().Diff(Build(live), Build(project))));
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(Build(project), Build(project))));
    }

    [TestMethod]
    [DataRow("CREATE TABLE p (id int); CREATE TABLE c (x int) INHERITS (p);", "CREATE TABLE p (id int); CREATE TABLE c (x int);")]
    [DataRow("CREATE TABLE p (id int); CREATE TABLE c (x int);", "CREATE TABLE p (id int); CREATE TABLE c (x int) INHERITS (p);")]
    [DataRow("CREATE TABLE p (id int); CREATE TABLE q (id int); CREATE TABLE c (x int) INHERITS (p);", "CREATE TABLE p (id int); CREATE TABLE q (id int); CREATE TABLE c (x int) INHERITS (q);")]
    public void AParentAddedOrRemovedIsAChange(string before, string after)
    {
        var diff = new DiffEngine().Diff(Build(before), Build(after));

        Assert.IsTrue(diff.Modified.Any(static change => change.NodeId == "public.c" && change.Details.Any(static detail => detail.PropertyName == nameof(TableDefinition.InheritsFrom))), Dump(diff));
    }

    [TestMethod]
    public void AColumnAParentGainsIsAChangeToTheChildToo()
    {
        var diff = new DiffEngine().Diff(
            Build("CREATE TABLE p (id int); CREATE TABLE c (x int) INHERITS (p);"),
            Build("CREATE TABLE p (id int, added date); CREATE TABLE c (x int) INHERITS (p);"));

        Assert.IsTrue(diff.Modified.Any(static change => change.NodeId == "public.c"), Dump(diff));
    }

    [TestMethod]
    public void ACheckThatIsNoInheritIsComparedAsSuch()
    {
        const string Written = "CREATE TABLE p (id int, CONSTRAINT p_pos CHECK (id > 0) NO INHERIT);";

        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(Build("CREATE TABLE public.p (id integer); ALTER TABLE public.p ADD CONSTRAINT p_pos CHECK ((id > 0)) NO INHERIT;"), Build(Written))));
        var diff = new DiffEngine().Diff(Build("CREATE TABLE p (id int, CONSTRAINT p_pos CHECK (id > 0));"), Build(Written));
        Assert.IsTrue(diff.Modified.Any(static change => change.Details.Any(static detail => detail.PropertyName == nameof(ConstraintDefinition.IsNoInherit))), Dump(diff));
    }

    private static string Dump(ChangeSet diff)
        => string.Join("\n", diff.Modified.Select(static change => $"{change.NodeId}: {string.Join("; ", change.Details.Select(static detail => detail.PropertyName))}"))
            + $"\nadded {diff.Added.Count} removed {diff.Removed.Count}";

    private static bool IsEmpty(ChangeSet diff) => diff.Added.Count + diff.Removed.Count + diff.Modified.Count == 0;

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
