using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-206: names and enum labels are compared as PostgreSQL compares them: exactly, after an unquoted name is folded. Objects that differ only by case
/// are two objects, a change of case is a change, and unquoted spellings in any case are one name.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class CaseSensitiveNameDiffTests
{
    [TestMethod]
    public void ObjectsDifferingOnlyByCaseConverge()
    {
        const string sql = """
            CREATE TYPE "Mood" AS ENUM ('ok', 'OK'); CREATE TYPE mood AS ENUM ('sad');
            CREATE TABLE "T" ("Id" int, id text, m "Mood", n mood); CREATE TABLE t (a int);
            CREATE FUNCTION f(m mood) RETURNS int LANGUAGE sql AS $$ SELECT 1 $$; CREATE FUNCTION f(m "Mood") RETURNS int LANGUAGE sql AS $$ SELECT 2 $$;
            """;

        var model = Build(sql);
        Assert.AreEqual(2, model.Tables.Count);
        Assert.AreEqual(2, model.Functions.Count);
        Assert.AreEqual(0, ChangeCount(sql, sql));
    }

    [TestMethod]
    public void AnEnumLabelsCaseIsAChange()
    {
        Assert.AreEqual(1, ChangeCount("CREATE TYPE m AS ENUM ('ok');", "CREATE TYPE m AS ENUM ('OK');"));
    }

    [TestMethod]
    public void UnquotedSpellingsAreOneName()
    {
        Assert.AreEqual(0, ChangeCount("CREATE TABLE MyTable (MyCol int);", "CREATE TABLE mytable (MYCOL int);"));
    }

    [TestMethod]
    public void AQuotedColumnsCaseChangeIsAChange()
    {
        Assert.AreNotEqual(0, ChangeCount("CREATE TABLE t (\"Col\" int);", "CREATE TABLE t (col int);"));
    }

    private static int ChangeCount(string oldSql, string newSql)
    {
        var changes = new DiffEngine().Diff(Build(oldSql), Build(newSql));
        return changes.Added.Count + changes.Removed.Count + changes.Modified.Count;
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
