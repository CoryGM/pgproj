using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-207 step 3: types are compared by their normalized names, so the spelling a type is written in is never a difference, while a real change
/// still is. It closes G-199: a typed table whose type has a field written <c>float8</c> or <c>varchar(10)</c> was compared against PostgreSQL's
/// <c>double precision</c> and <c>character varying(10)</c> by the field's text, modifiers included, and differed on every run.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class TypeAsWrittenDiffTests
{
    [TestMethod]
    public void ATypedTablesColumnsAreTheSameWhateverTheFieldsSpelling()
    {
        const string project = "CREATE TYPE pt AS (x float8, code varchar(10), n int); CREATE TABLE typed OF pt;";
        const string server = "CREATE TYPE pt AS (x double precision, code character varying(10), n integer); CREATE TABLE typed OF pt;";

        Assert.AreEqual(0, ChangeCount(project, server));
        Assert.AreEqual(0, ChangeCount(server, project));
    }

    [TestMethod]
    public void ATypedTablesChangedFieldIsStillAChange()
    {
        Assert.AreNotEqual(0, ChangeCount(
            "CREATE TYPE pt AS (code varchar(10)); CREATE TABLE typed OF pt;",
            "CREATE TYPE pt AS (code varchar(20)); CREATE TABLE typed OF pt;"));
    }

    [TestMethod]
    [DataRow("INT", "integer")]
    [DataRow("VARCHAR(10)", "character varying(10)")]
    [DataRow("timestamptz", "timestamp with time zone")]
    [DataRow("float8", "double precision")]
    public void AColumnTypesSpellingIsNotAChange(string written, string other)
    {
        Assert.AreEqual(0, ChangeCount($"CREATE TABLE t (c {written});", $"CREATE TABLE t (c {other});"));
    }

    [TestMethod]
    public void ARoutinesTypeSpellingIsNotAChange()
    {
        Assert.AreEqual(0, ChangeCount(
            "CREATE FUNCTION f(a INT) RETURNS VARCHAR LANGUAGE sql AS $$ SELECT 'x' $$;",
            "CREATE FUNCTION f(a integer) RETURNS character varying LANGUAGE sql AS $$ SELECT 'x' $$;"));
    }

    private static int ChangeCount(string oldSql, string newSql)
    {
        var changes = new DiffEngine().Diff(Build(oldSql), Build(newSql));
        return changes.Added.Count + changes.Removed.Count + changes.Modified.Count;
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
