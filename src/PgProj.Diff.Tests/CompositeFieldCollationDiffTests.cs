using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-192: a composite field's collation is compared as a table column's is. The database names it with its schema (<c>pg_catalog."C"</c>) and leaves out the type's own
/// collation, so the spellings of one collation, and none against <c>default</c>, are the same; a different collation, or one added or removed, is a difference.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class CompositeFieldCollationDiffTests
{
    [TestMethod]
    [DataRow("""CREATE TYPE pt AS (label text COLLATE "C");""", """CREATE TYPE pt AS (label text COLLATE "C");""")]
    [DataRow("""CREATE TYPE pt AS (label text COLLATE "C");""", """CREATE TYPE public.pt AS ("label" text COLLATE pg_catalog."C");""")]
    [DataRow("""CREATE TYPE pt AS (label text COLLATE "C");""", """CREATE TYPE pt AS (LABEL TEXT collate "pg_catalog"."C");""")]
    [DataRow("""CREATE TYPE pt AS (label text);""", """CREATE TYPE pt AS (label text COLLATE "default");""")]
    [DataRow("""CREATE TYPE pt AS (a text COLLATE "C", b int);""", """CREATE TYPE pt AS (b integer, a text COLLATE pg_catalog."C");""")]
    public void TheSameCollationIsNoChange(string project, string live)
    {
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(Build(live), Build(project))));
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(Build(project), Build(project))));
    }

    [TestMethod]
    [DataRow("""CREATE TYPE pt AS (label text COLLATE "C");""", """CREATE TYPE pt AS (label text COLLATE "POSIX");""")]
    [DataRow("""CREATE TYPE pt AS (label text);""", """CREATE TYPE pt AS (label text COLLATE "C");""")]
    [DataRow("""CREATE TYPE pt AS (label text COLLATE "C");""", """CREATE TYPE pt AS (label text);""")]
    public void ADifferentCollationIsAChange(string before, string after)
    {
        var diff = new DiffEngine().Diff(Build(before), Build(after));

        Assert.HasCount(1, diff.Modified);
        Assert.IsTrue(diff.Modified[0].Details.Any(static detail => detail.PropertyName == nameof(TypeDefinition.CompositeFields)));
    }

    private static bool IsEmpty(ChangeSet diff) => diff.Added.Count + diff.Removed.Count + diff.Modified.Count == 0;

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
