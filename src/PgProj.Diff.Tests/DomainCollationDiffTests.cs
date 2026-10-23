using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-196: a domain's collation is read and compared as a table column's is. The database names it with its schema (<c>pg_catalog."C"</c>) and leaves out the base
/// type's own, so the spellings of one collation, and none against <c>default</c>, are the same; a different collation, or one added or removed, is a difference.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class DomainCollationDiffTests
{
    [TestMethod]
    public void TheCollationIsRead()
    {
        Assert.AreEqual("C", Build("""CREATE DOMAIN d AS text COLLATE "C";""").Types.Values.Single().DomainCollationName);
        Assert.AreEqual("pg_catalog.C", Build("""CREATE DOMAIN public.d AS text COLLATE pg_catalog."C" DEFAULT 'x';""").Types.Values.Single().DomainCollationName);
        Assert.IsNull(Build("CREATE DOMAIN d AS text;").Types.Values.Single().DomainCollationName);
    }

    [TestMethod]
    [DataRow("""CREATE DOMAIN d AS text COLLATE "C";""", """CREATE DOMAIN d AS text COLLATE "C";""")]
    [DataRow("""CREATE DOMAIN d AS text COLLATE "C" NOT NULL;""", """CREATE DOMAIN public.d AS text COLLATE pg_catalog."C" NOT NULL;""")]
    [DataRow("""CREATE DOMAIN d AS text COLLATE "C";""", """CREATE DOMAIN d AS TEXT collate "pg_catalog"."C";""")]
    [DataRow("CREATE DOMAIN d AS text;", """CREATE DOMAIN d AS text COLLATE "default";""")]
    public void TheSameCollationIsNoChange(string project, string live)
    {
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(Build(live), Build(project))));
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(Build(project), Build(project))));
    }

    [TestMethod]
    [DataRow("""CREATE DOMAIN d AS text COLLATE "C";""", """CREATE DOMAIN d AS text COLLATE "POSIX";""")]
    [DataRow("CREATE DOMAIN d AS text;", """CREATE DOMAIN d AS text COLLATE "C";""")]
    [DataRow("""CREATE DOMAIN d AS text COLLATE "C";""", "CREATE DOMAIN d AS text;")]
    public void ADifferentCollationIsAChange(string before, string after)
    {
        var diff = new DiffEngine().Diff(Build(before), Build(after));

        Assert.HasCount(1, diff.Modified);
        Assert.IsTrue(diff.Modified[0].Details.Any(static detail => detail.PropertyName == nameof(TypeDefinition.DomainCollationName)));
    }

    private static bool IsEmpty(ChangeSet diff) => diff.Added.Count + diff.Removed.Count + diff.Modified.Count == 0;

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
