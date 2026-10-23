using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-84: <see cref="SemanticDiffNormalizationPass"/> used to sort a table's columns alphabetically by name, so every generated
/// <c>CREATE TABLE</c>/<c>ADD COLUMN</c> listed columns alphabetically instead of in the order the author declared them. Column
/// order is not compared by <see cref="DiffEngine"/> (columns are matched by identity key), so nothing needed the sort; it only
/// corrupted the order the deploy layer reads back out.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ColumnDeclarationOrderDeployTests
{
    [TestMethod]
    public void CreateTableListsColumnsInDeclarationOrderNotAlphabetically()
    {
        var statements = Statements(
            DatabaseModel.Empty,
            Build("CREATE TABLE public.orders (id integer PRIMARY KEY, customer_name text, order_date date, amount numeric(10,2), status text);"));

        AssertContainsStartingWith(
            statements,
            "CREATE TABLE \"public\".\"orders\" (\n"
            + "    \"id\" integer NOT NULL,\n"
            + "    \"customer_name\" text,\n"
            + "    \"order_date\" date,\n"
            + "    \"amount\" numeric(10,2),\n"
            + "    \"status\" text\n"
            + ");");
    }

    [TestMethod]
    public void AddColumnAppliesColumnsInDeclarationOrderNotAlphabetically()
    {
        var before = Build("CREATE TABLE public.orders (id integer PRIMARY KEY);");
        var after = Build("CREATE TABLE public.orders (id integer PRIMARY KEY, zeta text, alpha text, middle text);");

        var joined = Dump(Statements(before, after));

        var zeta = joined.IndexOf("ADD COLUMN \"zeta\"", StringComparison.Ordinal);
        var alpha = joined.IndexOf("ADD COLUMN \"alpha\"", StringComparison.Ordinal);
        var middle = joined.IndexOf("ADD COLUMN \"middle\"", StringComparison.Ordinal);

        Assert.IsTrue(zeta >= 0 && alpha >= 0 && middle >= 0, joined);
        Assert.IsTrue(zeta < alpha, joined);
        Assert.IsTrue(alpha < middle, joined);
    }

    [TestMethod]
    public void SelfDiffOfANonAlphabeticalColumnOrderReportsNoChanges()
    {
        var model = Build("CREATE TABLE public.orders (id integer PRIMARY KEY, customer_name text, order_date date, amount numeric(10,2), status text);");

        var diff = new DiffEngine().Diff(model, model);

        Assert.AreEqual(0, diff.Added.Count, Dump(diff.Added.Select(static change => change.NodeId)));
        Assert.AreEqual(0, diff.Removed.Count, Dump(diff.Removed.Select(static change => change.NodeId)));
        Assert.AreEqual(0, diff.Modified.Count, Dump(diff.Modified.Select(static change => change.NodeId)));
    }

    private static void AssertContainsStartingWith(IReadOnlyList<string> statements, string expected)
        => Assert.IsTrue(statements.Any(statement => statement.StartsWith(expected, StringComparison.Ordinal)), $"Expected a statement starting with:\n{expected}\n\nActual:\n{Dump(statements)}");

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
