using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-47: a column added by <c>ALTER TABLE ... ADD COLUMN</c> in the project was missing from the model, so <c>publish</c> exited 0
/// and never created it. It is now deployed exactly like a column declared inside <c>CREATE TABLE</c>.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class AlterTableAddColumnDeployTests
{
    private const string Table = "CREATE TABLE public.customers (id integer PRIMARY KEY, name text);";

    [TestMethod]
    public void ANewTableIsCreatedWithItsAddedColumn()
    {
        var statements = Statements(DatabaseModel.Empty, Build(Table + "ALTER TABLE public.customers ADD COLUMN phone varchar(20) NOT NULL DEFAULT 'none';"));

        var create = statements.Single(static statement => statement.StartsWith("CREATE TABLE", StringComparison.Ordinal));
        StringAssert.Contains(create, "\"phone\" varchar(20) NOT NULL DEFAULT 'none'");
        Assert.IsFalse(statements.Any(static statement => statement.Contains("TODO", StringComparison.Ordinal)), Dump(statements));
    }

    [TestMethod]
    public void AnExistingTableGetsAnAddColumn()
    {
        var statements = Statements(Build(Table), Build(Table + "ALTER TABLE public.customers ADD COLUMN phone varchar(20) NOT NULL DEFAULT 'none';"));

        Assert.HasCount(1, statements, Dump(statements));
        Assert.AreEqual("ALTER TABLE \"public\".\"customers\" ADD COLUMN \"phone\" varchar(20) NOT NULL DEFAULT 'none';", statements[0]);
    }

    [TestMethod]
    public void TheAlterInAnotherFileDeploysTheSameStatement()
    {
        var statements = Statements(Build(Table), Build("ALTER TABLE public.customers ADD COLUMN phone text;", Table));

        Assert.HasCount(1, statements, Dump(statements));
        Assert.AreEqual("ALTER TABLE \"public\".\"customers\" ADD COLUMN \"phone\" text;", statements[0]);
    }

    [TestMethod]
    public void AnAddedColumnAlreadyLiveDeploysNothing()
    {
        var project = Build(Table + "ALTER TABLE public.customers ADD COLUMN phone varchar(20) NOT NULL DEFAULT 'none';");
        var live = Build("CREATE TABLE public.customers (id integer PRIMARY KEY, name text, phone varchar(20) NOT NULL DEFAULT 'none');");

        Assert.IsEmpty(Statements(live, project));
    }

    [TestMethod]
    public void ADefaultThatCallsAProjectFunctionIsCreatedAfterTheFunction()
    {
        // The function's name sorts after the table's, so only a real dependency puts it first.
        var statements = Statements(
            DatabaseModel.Empty,
            Build(
                "CREATE FUNCTION public.zz_now() RETURNS timestamptz LANGUAGE sql AS $$ SELECT now() $$;",
                Table,
                "ALTER TABLE public.customers ADD COLUMN created timestamptz NOT NULL DEFAULT public.zz_now();"));

        var function = statements.FindIndex(static statement => statement.Contains("FUNCTION", StringComparison.Ordinal));
        var table = statements.FindIndex(static statement => statement.StartsWith("CREATE TABLE", StringComparison.Ordinal));
        Assert.IsGreaterThanOrEqualTo(0, function, Dump(statements));
        Assert.IsLessThan(table, function, Dump(statements));
    }

    [TestMethod]
    public void AnAddedColumnsForeignKeyIsDeployedAfterTheTables()
    {
        var statements = Statements(
            DatabaseModel.Empty,
            Build(Table + "CREATE TABLE public.orders (id integer PRIMARY KEY); ALTER TABLE public.customers ADD COLUMN order_id integer REFERENCES public.orders (id);"));

        var foreignKey = statements.FindIndex(static statement => statement.Contains("FOREIGN KEY", StringComparison.Ordinal));
        Assert.IsGreaterThanOrEqualTo(0, foreignKey, Dump(statements));
        Assert.IsTrue(statements.Take(foreignKey).Count(static statement => statement.StartsWith("CREATE TABLE", StringComparison.Ordinal)) == 2, Dump(statements));
    }

    [TestMethod]
    public void AnAddedSerialColumnDeploysItsSequenceOwnership()
    {
        var statements = Statements(Build(Table), Build(Table + "ALTER TABLE public.customers ADD COLUMN ticket serial;"));

        Assert.IsTrue(statements.Any(static statement => statement.StartsWith("ALTER TABLE", StringComparison.Ordinal) && statement.Contains("ADD COLUMN \"ticket\"", StringComparison.Ordinal)), Dump(statements));
        Assert.IsTrue(statements.Any(static statement => statement.Contains("customers_ticket_seq", StringComparison.Ordinal) && statement.StartsWith("CREATE SEQUENCE", StringComparison.Ordinal)), Dump(statements));
        Assert.IsFalse(statements.Any(static statement => statement.Contains("TODO", StringComparison.Ordinal)), Dump(statements));
    }

    private static string Dump(IEnumerable<string> statements) => string.Join(Environment.NewLine, statements);

    private static List<string> Statements(DatabaseModel oldModel, DatabaseModel newModel)
    {
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));
        return batch.Operations
            .Where(static operation => operation is not SchemaCreateOperation)
            .Select(static operation => operation.GenerateSql().Trim())
            .ToList();
    }

    private static DatabaseModel Build(params string[] files)
    {
        var parser = new PgQueryParser();
        return new SemanticPipeline().Execute(files.Select(parser.Parse).ToArray());
    }
}
