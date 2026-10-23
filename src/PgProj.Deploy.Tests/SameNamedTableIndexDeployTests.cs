using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-89: <see cref="DiffEngine"/> matched an index to a table by bare table name, ignoring schema, so each index of two
/// same-named tables in different schemas was reported once per table and <see cref="ScriptGenerator"/> threw
/// ("An item with the same key has already been added"). Removing one of the tables also reported the other table's
/// index as removed.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SameNamedTableIndexDeployTests
{
    private const string BothTables = """
        create schema a;
        create schema b;
        create table a.pp (id integer, emails jsonb);
        create table b.pp (id integer, emails jsonb);
        create index a_pp_idx on a.pp using gin (emails);
        create index b_pp_idx on b.pp using gin (emails);
        """;

    [TestMethod]
    public void EachIndexOfSameNamedTablesIsCreatedOnceOnItsOwnTable()
    {
        var statements = Statements(DatabaseModel.Empty, Build(BothTables));

        Assert.AreEqual(
            1,
            statements.Count(static statement => statement == "CREATE INDEX \"a_pp_idx\" ON \"a\".\"pp\" USING gin (\"emails\");"),
            Dump(statements));
        Assert.AreEqual(
            1,
            statements.Count(static statement => statement == "CREATE INDEX \"b_pp_idx\" ON \"b\".\"pp\" USING gin (\"emails\");"),
            Dump(statements));
        Assert.AreEqual(2, statements.Count(static statement => statement.StartsWith("CREATE INDEX", StringComparison.Ordinal)), Dump(statements));
    }

    [TestMethod]
    public void RemovingOneSameNamedTableDoesNotDropTheOtherTablesIndex()
    {
        var after = Build("""
            create schema a;
            create schema b;
            create table a.pp (id integer, emails jsonb);
            create index a_pp_idx on a.pp using gin (emails);
            """);

        var statements = Statements(Build(BothTables), after);

        Assert.IsFalse(statements.Any(static statement => statement.Contains("a_pp_idx", StringComparison.Ordinal)), Dump(statements));
        Assert.IsTrue(statements.Contains("DROP TABLE \"b\".\"pp\";"), Dump(statements));
    }

    private static string Dump(IEnumerable<string> lines) => string.Join("\n---\n", lines);

    private static List<string> Statements(DatabaseModel oldModel, DatabaseModel newModel)
    {
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));
        return batch.Operations
            .Select(static operation => operation.GenerateSql().Trim().Replace("\r\n", "\n", StringComparison.Ordinal))
            .ToList();
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
