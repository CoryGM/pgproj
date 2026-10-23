namespace PgProj.Parser.Tests;

/// <summary>
/// G-225: an index written without a name was named <c>&lt;table&gt;_idx</c>, so a second one on the same table was a duplicate. It is named as PostgreSQL names it:
/// its table, its columns (an expression as PostgreSQL figures it, <c>INCLUDE</c> columns too), and <c>idx</c>, with a number while the name is taken. The
/// names below are what PostgreSQL 16 and 18 gave the same statements.
/// </summary>
[TestClass]
public sealed class UnnamedIndexNamingTests
{
    [TestMethod]
    public void AnUnnamedIndexGetsPostgreSqlsName()
    {
        var names = IndexNames("""
            create table public.t (a int, b text, unique (a) include (b));
            create index on public.t (lower(b));
            create index on public.t (a, b);
            create index on public.t (a, b);
            create unique index on public.t (b) include (a) where a > 0;
            create materialized view public.mv as select a from public.t;
            create index on public.mv (a);
            """);

        CollectionAssert.AreEqual(new[] { "t_lower_idx", "t_a_b_idx", "t_a_b_idx1", "t_b_a_idx", "mv_a_idx" }, names);
    }

    [TestMethod]
    public void ANameTakenInAnotherFileGetsANumberAndAWarning()
    {
        var trees = ConstraintNameAssigner.Assign([
            new PgQueryParser().Parse("create table public.t (a int); create index on public.t (a);"),
            new PgQueryParser().Parse("create table public.u (a int); create index t_a_idx on public.u (a);"),
        ]);

        Assert.AreEqual("t_a_idx1", trees[0].Nodes.OfType<IndexNode>().Single().Name);
        Assert.IsTrue(trees[0].Diagnostics.Any(static diagnostic => diagnostic.Severity == SqlDiagnosticSeverity.Warning && diagnostic.Message.Contains("Unnamed index on 'public.t'", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void NamingAgainGivesTheSameNames()
    {
        var once = ConstraintNameAssigner.Assign([new PgQueryParser().Parse("create table public.t (a int); create index on public.t (a); create index on public.t (a);")]);
        var twice = ConstraintNameAssigner.Assign(once);

        CollectionAssert.AreEqual(once[0].Nodes.OfType<IndexNode>().Select(static index => index.Name).ToArray(), twice[0].Nodes.OfType<IndexNode>().Select(static index => index.Name).ToArray());
        Assert.IsTrue(twice[0].Nodes.OfType<IndexNode>().All(static index => index.IsNameGenerated));
    }

    private static string?[] IndexNames(string sql)
        => ConstraintNameAssigner.Assign([new PgQueryParser().Parse(sql)])[0].Nodes.OfType<IndexNode>().Select(static index => index.Name).ToArray();
}
