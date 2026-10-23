using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-222: a column's <c>STORAGE</c> and <c>COMPRESSION</c> were dropped from <c>CREATE TABLE</c>. They are deployed as written, compared only when the project states
/// them (<c>STORAGE default</c> is not compared; <c>COMPRESSION default</c> is none of the column's own), and changed in place with <c>SET STORAGE</c> and
/// <c>SET COMPRESSION</c>. <c>LIKE ... INCLUDING STORAGE | COMPRESSION</c> copies them.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ColumnStorageCompressionTests
{
    [TestMethod]
    public void TheyAreReadAndDeployedAfterTheType()
    {
        var model = Build("CREATE TABLE public.t (a text STORAGE MAIN COMPRESSION lz4 COLLATE \"C\" NOT NULL, b text);");
        var column = model.Tables["public.t"].Columns[0];
        Assert.AreEqual("main", column.Storage);
        Assert.AreEqual("lz4", column.Compression);
        Assert.IsNull(model.Tables["public.t"].Columns[1].Storage);

        StringAssert.Contains(ScriptFor(DatabaseModel.Empty, model), "\"a\" text STORAGE main COMPRESSION lz4 COLLATE \"C\" NOT NULL");
    }

    [TestMethod]
    public void AStatedChangeIsMadeInPlace()
    {
        var script = ScriptFor(
            Build("CREATE TABLE public.t (a text STORAGE main COMPRESSION lz4);"),
            Build("CREATE TABLE public.t (a text STORAGE external COMPRESSION pglz, b text COMPRESSION lz4);"));

        StringAssert.Contains(script, "ALTER TABLE \"public\".\"t\" ALTER COLUMN \"a\" SET STORAGE external;", script);
        StringAssert.Contains(script, "ALTER TABLE \"public\".\"t\" ALTER COLUMN \"a\" SET COMPRESSION pglz;", script);
        StringAssert.Contains(script, "ADD COLUMN \"b\" text COMPRESSION lz4;", script);
    }

    [TestMethod]
    [DataRow("a text STORAGE main COMPRESSION lz4", "a text")]
    [DataRow("a text STORAGE main", "a text STORAGE default")]
    [DataRow("a text", "a text COMPRESSION default")]
    public void WhatTheProjectDoesNotStateIsNotCompared(string database, string project)
        => Assert.IsEmpty(new DiffEngine().Diff(Build($"CREATE TABLE public.t ({database});"), Build($"CREATE TABLE public.t ({project});")).Modified);

    [TestMethod]
    public void LikeCopiesThemOnlyWhenItsOptionsSaySo()
    {
        var model = Build("CREATE TABLE public.a (x text STORAGE main COMPRESSION lz4); CREATE TABLE public.b (LIKE public.a INCLUDING STORAGE INCLUDING COMPRESSION); CREATE TABLE public.c (LIKE public.a);");

        Assert.AreEqual("main lz4", $"{model.Tables["public.b"].Columns[0].Storage} {model.Tables["public.b"].Columns[0].Compression}");
        Assert.IsNull(model.Tables["public.c"].Columns[0].Storage);
        Assert.IsNull(model.Tables["public.c"].Columns[0].Compression);
    }

    private static string ScriptFor(DatabaseModel oldModel, DatabaseModel newModel)
        => new ScriptGenerator().GenerateSqlScript(new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel)));

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
