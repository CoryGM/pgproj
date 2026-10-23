using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-242: <c>ALTER SEQUENCE ... OWNED BY</c> comes before the owner statements. PostgreSQL refuses it once the table has another owner than the sequence ("sequence must have
/// same owner as table it is linked to"), and changing a table's owner changes the owner of the sequences it owns.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SequenceOwnershipOrderTests
{
    [TestMethod]
    public void TheSequenceIsHandedToItsColumnBeforeTheTablesOwnerChanges()
    {
        var statements = Statements(DatabaseModel.Empty, Build("""
            CREATE SEQUENCE public.t_id_seq OWNED BY public.t.id;
            CREATE TABLE public.t (id int DEFAULT nextval('t_id_seq'::regclass), name text);
            ALTER TABLE public.t OWNER TO app_owner;
            """));

        var ownedBy = statements.FindIndex(static s => s.Contains("OWNED BY", StringComparison.Ordinal));
        var owner = statements.FindIndex(static s => s.Contains("OWNER TO", StringComparison.Ordinal));
        Assert.IsGreaterThanOrEqualTo(0, ownedBy, string.Join("\n", statements));
        Assert.IsGreaterThanOrEqualTo(0, owner, string.Join("\n", statements));
        Assert.IsLessThan(owner, ownedBy, string.Join("\n", statements));
    }

    [TestMethod]
    public void WithNoOwnerStatementTheOwnershipStillComesAfterTheTable()
    {
        var statements = Statements(DatabaseModel.Empty, Build("""
            CREATE SEQUENCE public.t_id_seq OWNED BY public.t.id;
            CREATE TABLE public.t (id int DEFAULT nextval('t_id_seq'::regclass));
            """));

        var table = statements.FindIndex(static s => s.StartsWith("CREATE TABLE", StringComparison.Ordinal));
        var ownedBy = statements.FindIndex(static s => s.Contains("OWNED BY", StringComparison.Ordinal));
        Assert.IsLessThan(ownedBy, table, string.Join("\n", statements));
    }

    private static List<string> Statements(DatabaseModel oldModel, DatabaseModel newModel)
    {
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));
        return batch.Operations.Where(static operation => operation is not SchemaCreateOperation).Select(static operation => operation.GenerateSql().Trim().Replace("\r\n", "\n", StringComparison.Ordinal)).ToList();
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
