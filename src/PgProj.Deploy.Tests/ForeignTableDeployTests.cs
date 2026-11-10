using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-133 (the user's design of 2026-11-10): a foreign table is created after its server with its options, its columns' options, and its comment; its options and its columns'
/// change in place; a move to another server drops and creates it with what is on it, refused while something else uses it; dropping it is destructive, its columns are not
/// (it keeps no rows); a server that has to be rebuilt is refused while a foreign table is on it.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ForeignTableDeployTests
{
    private const string Servers = "CREATE EXTENSION postgres_fdw;\nCREATE SERVER remote FOREIGN DATA WRAPPER postgres_fdw;\nCREATE SERVER other FOREIGN DATA WRAPPER postgres_fdw;\n";
    private const string Table = "CREATE FOREIGN TABLE public.ft (\n    a integer OPTIONS (column_name 'x') NOT NULL,\n    b text\n) SERVER remote OPTIONS (table_name 't');\nCOMMENT ON FOREIGN TABLE public.ft IS 'remote rows';\nGRANT SELECT ON public.ft TO app;\n";

    [TestMethod]
    public void ANewForeignTableIsCreatedAfterItsServer()
    {
        var statements = Statements(Build(""), Build(Servers + Table), out var batch);

        var create = statements.FindIndex(static statement => statement.StartsWith("CREATE FOREIGN TABLE \"public\".\"ft\"", StringComparison.Ordinal));
        Assert.IsGreaterThan(statements.FindIndex(static statement => statement.StartsWith("CREATE SERVER \"remote\"", StringComparison.Ordinal)), create, string.Join("\n", statements));
        StringAssert.Contains(statements[create], "\"a\" integer OPTIONS (column_name 'x') NOT NULL");
        StringAssert.Contains(statements[create], "\nSERVER \"remote\" OPTIONS (table_name 't');");
        StringAssert.Contains(statements[create], "COMMENT ON FOREIGN TABLE \"public\".\"ft\" IS 'remote rows';");
        Assert.IsEmpty(batch.GetDestructiveOperations());
    }

    [TestMethod]
    public void TheTablesAndColumnsOptionsChangeInPlace()
    {
        var project = Servers + Table.Replace("table_name 't'", "table_name 'u', fetch_size '100'", StringComparison.Ordinal).Replace("column_name 'x'", "column_name 'y'", StringComparison.Ordinal);
        var statements = Statements(Build(Servers + Table), Build(project), out var batch);

        CollectionAssert.AreEqual(
            new[]
            {
                "ALTER FOREIGN TABLE \"public\".\"ft\" OPTIONS (ADD fetch_size '100', SET table_name 'u');",
                "ALTER FOREIGN TABLE \"public\".\"ft\" ALTER COLUMN \"a\" OPTIONS (SET column_name 'y');",
            },
            string.Join("\n", statements).Split('\n'),
            string.Join("\n", statements));
        Assert.IsEmpty(batch.GetDestructiveOperations());
    }

    [TestMethod]
    public void AForeignTablesCommentIsChangedAsAForeignTables()
    {
        var statements = Statements(Build(Servers + Table), Build(Servers + Table.Replace("'remote rows'", "'other rows'", StringComparison.Ordinal)), out _);

        CollectionAssert.AreEqual(new[] { "COMMENT ON FOREIGN TABLE \"public\".\"ft\" IS 'other rows';" }, statements.ToArray());
    }

    [TestMethod]
    public void AMoveToAnotherServerDropsAndCreatesItWithItsGrant()
    {
        var statements = Statements(Build(Servers + Table), Build(Servers + Table.Replace("SERVER remote", "SERVER other", StringComparison.Ordinal)), out var batch);

        var drop = statements.IndexOf("DROP FOREIGN TABLE \"public\".\"ft\";");
        var create = statements.FindIndex(static statement => statement.StartsWith("CREATE FOREIGN TABLE \"public\".\"ft\"", StringComparison.Ordinal));
        var grant = statements.FindIndex(static statement => statement.StartsWith("GRANT SELECT ON", StringComparison.Ordinal));
        Assert.IsTrue(drop >= 0 && drop < create && create < grant, string.Join("\n", statements));
        StringAssert.Contains(statements[create], "SERVER \"other\"");
        Assert.IsEmpty(batch.GetDestructiveOperations(), "A foreign table keeps no rows, so moving it loses none.");
        Assert.IsFalse(batch.Operations.OfType<BlockedOperation>().Any());
    }

    [TestMethod]
    public void AMoveToAnotherServerIsRefusedWhileAViewReadsIt()
    {
        const string view = "CREATE VIEW public.v AS SELECT a FROM public.ft;\n";
        var batch = Batch(Build(Servers + Table + view), Build(Servers + Table.Replace("SERVER remote", "SERVER other", StringComparison.Ordinal) + view));

        var blocked = batch.Operations.OfType<BlockedOperation>().Single();
        StringAssert.Contains(blocked.Message, "The foreign table public.ft moves to another server");
        StringAssert.Contains(blocked.Message, "public.v");
    }

    [TestMethod]
    public void DroppingAForeignTableIsDestructiveAndDroppingItsColumnIsNot()
    {
        var statements = Statements(Build(Servers + Table), Build(Servers), out var batch);

        CollectionAssert.Contains(statements, "DROP FOREIGN TABLE \"public\".\"ft\";");
        Assert.IsTrue(batch.GetDestructiveOperations().SelectMany(static operation => operation.Reasons).Any(static reason => reason.StartsWith("foreign table dropped; every query that reads ft stops working", StringComparison.Ordinal)));

        Statements(Build(Servers + Table), Build(Servers + Table.Replace(",\n    b text", string.Empty, StringComparison.Ordinal)), out var columnBatch);
        Assert.IsEmpty(columnBatch.GetDestructiveOperations());
    }

    [TestMethod]
    public void AnOrdinaryTableThatBecomesForeignLosesItsRowsAndIsDestructive()
    {
        Statements(Build(Servers + "CREATE TABLE public.ft (a integer NOT NULL, b text);\n"), Build(Servers + Table), out var batch);

        Assert.IsNotEmpty(batch.GetDestructiveOperations());
    }

    [TestMethod]
    public void AServerThatMustBeRebuiltIsRefusedWhileAForeignTableIsOnIt()
    {
        var batch = Batch(Build(Servers + Table), Build(Servers.Replace("CREATE SERVER remote FOREIGN", "CREATE SERVER remote TYPE 'pg' FOREIGN", StringComparison.Ordinal) + Table));

        var blocked = batch.Operations.OfType<BlockedOperation>().Single();
        StringAssert.Contains(blocked.Message, "public.ft");
    }

    private static OperationBatch Batch(DatabaseModel oldModel, DatabaseModel newModel)
        => new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));

    private static List<string> Statements(DatabaseModel oldModel, DatabaseModel newModel, out OperationBatch batch)
    {
        batch = Batch(oldModel, newModel);
        return batch.Operations
            .Where(static operation => operation is not SchemaCreateOperation)
            .Select(static operation => operation.GenerateSql().Replace("\r\n", "\n", StringComparison.Ordinal).Trim())
            .Where(static sql => sql.Length > 0)
            .ToList();
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([AlterTableColumnApplier.Apply([new PgQueryParser().Parse(sql)]).Single()]);
}
