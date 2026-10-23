using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-221: <c>CREATE TABLE ... (LIKE source ...)</c> was deployed as a table with only its own columns. The <c>LIKE</c> is now deployed as written, where it was
/// written, after the source and what it copies; what it copies is not created a second time, though a copied comment is set. Once the table exists, a copy
/// the source gains is added to it like any other column, constraint, or index.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class TableLikeDeployTests
{
    private const string Source = "CREATE TABLE a (id int PRIMARY KEY, n text, CONSTRAINT a_ck CHECK (id > 0)); CREATE INDEX a_n ON a (n); COMMENT ON CONSTRAINT a_ck ON a IS 'ck'; ";

    [TestMethod]
    public void TheLikeIsDeployedAsWrittenWhereItWasWritten()
    {
        var sql = Statements(Generate(Build(string.Empty), Build(Source + "CREATE TABLE b (x int, like a Including All, extra int);")));

        var table = sql.Single(static statement => statement.StartsWith("CREATE TABLE \"public\".\"b\"", StringComparison.Ordinal));
        StringAssert.StartsWith(table, "CREATE TABLE \"public\".\"b\" (\n    \"x\" int,\n    like a Including All,\n    \"extra\" int\n);");
    }

    [TestMethod]
    public void TheTableIsCreatedAfterWhatItCopiesAndTheCopiesAreNotCreatedAgain()
    {
        var sql = Statements(Generate(Build(string.Empty), Build(Source + "CREATE TABLE b (LIKE a INCLUDING ALL);")));

        var table = sql.FindIndex(static statement => statement.StartsWith("CREATE TABLE \"public\".\"b\"", StringComparison.Ordinal));
        Assert.IsTrue(sql.FindIndex(static statement => statement.Contains("ADD CONSTRAINT \"a_ck\"", StringComparison.Ordinal)) < table, string.Join("\n", sql));
        Assert.IsTrue(sql.FindIndex(static statement => statement.Contains("ADD CONSTRAINT \"a_pkey\"", StringComparison.Ordinal)) < table, string.Join("\n", sql));
        Assert.IsTrue(sql.FindIndex(static statement => statement.StartsWith("CREATE INDEX \"a_n\"", StringComparison.Ordinal)) < table, string.Join("\n", sql));
        Assert.IsFalse(sql.Any(static statement => statement.Contains("ALTER TABLE \"public\".\"b\"", StringComparison.Ordinal) || (statement.StartsWith("CREATE ", StringComparison.Ordinal) && statement.Contains("ON \"public\".\"b\"", StringComparison.Ordinal))), string.Join("\n", sql));
        Assert.IsTrue(sql.FindIndex(static statement => statement == "COMMENT ON CONSTRAINT \"a_ck\" ON \"public\".\"b\" IS 'ck';") > table, string.Join("\n", sql));
    }

    [TestMethod]
    public void WhatTheSourceGainsIsAddedToATableThatExists()
    {
        const string after = "CREATE TABLE a (id int PRIMARY KEY, n text, m int, CONSTRAINT a_ck CHECK (id > 0), CONSTRAINT a_m CHECK (m > 0)); CREATE INDEX a_n ON a (n); CREATE INDEX a_m_i ON a (m); ";
        var sql = Statements(Generate(Build(Source + "CREATE TABLE b (LIKE a INCLUDING ALL);"), Build(after + "CREATE TABLE b (LIKE a INCLUDING ALL);")));

        CollectionAssert.IsSubsetOf(
            new[] { "ALTER TABLE \"public\".\"b\" ADD COLUMN \"m\" int;", "ALTER TABLE \"public\".\"b\"\nADD CONSTRAINT \"a_m\"\nCHECK (m > 0);", "CREATE INDEX \"b_m_idx\" ON \"public\".\"b\" USING btree (\"m\");" },
            sql,
            string.Join("\n", sql));
    }

    private static List<string> Statements(OperationBatch batch)
        => batch.Operations
            .Where(static operation => operation is not SchemaCreateOperation)
            .Select(static operation => operation.GenerateSql().Trim())
            .Where(static sql => !sql.StartsWith("-- ", StringComparison.Ordinal))
            .Select(static sql => sql.Replace("\n-- NOTE: Constraints and indexes deploy as separate ALTER TABLE/CREATE INDEX statements.", string.Empty, StringComparison.Ordinal))
            .ToList();

    private static OperationBatch Generate(DatabaseModel oldModel, DatabaseModel newModel)
        => new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute(string.IsNullOrEmpty(sql) ? [] : [new PgQueryParser().Parse(sql)]);
}
