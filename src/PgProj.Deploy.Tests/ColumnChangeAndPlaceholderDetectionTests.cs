using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// Regression coverage for G-44: column changes are emitted by the owning table's operation and must never
/// become operations of their own (their placeholder SQL made <c>publish --execute</c> refuse to add any table
/// or column), and placeholders inside a statement must be detected while a user's own <c>-- TODO:</c> comment
/// inside a routine body must not be.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ColumnChangeAndPlaceholderDetectionTests
{
    [TestMethod]
    public void AddingATableEmitsNoColumnOperationsAndNothingUnsupported()
    {
        var batch = Generate(
            DatabaseModel.Empty,
            BuildModel("create table public.t (id bigint primary key, name text);"));

        Assert.IsFalse(batch.Operations.Any(static operation => operation.NodeId is "public.t.id" or "public.t.name"), "Column changes must not become operations.");
        Assert.IsEmpty(batch.GetUnsupportedOperations(), Describe(batch.GetUnsupportedOperations()));
        StringAssert.Contains(new ScriptGenerator().GenerateSqlScript(batch), "CREATE TABLE \"public\".\"t\"");
    }

    [TestMethod]
    public void AddingAColumnEmitsOnlyTheAlterTableStatement()
    {
        var batch = Generate(
            BuildModel("create table public.t (id bigint not null);"),
            BuildModel("create table public.t (id bigint not null, name text);"));

        var script = new ScriptGenerator().GenerateSqlScript(batch);

        StringAssert.Contains(script, "ALTER TABLE \"public\".\"t\" ADD COLUMN \"name\" text;");
        Assert.HasCount(1, batch.Operations);
        Assert.IsEmpty(batch.GetUnsupportedOperations(), Describe(batch.GetUnsupportedOperations()));
    }

    [TestMethod]
    public void RemovingAndChangingColumnsEmitOnlyTableAlterations()
    {
        var batch = Generate(
            BuildModel("create table public.t (id bigint not null, name text, note text);"),
            BuildModel("create table public.t (id bigint not null, name varchar(20));"));

        var script = new ScriptGenerator().GenerateSqlScript(batch);

        StringAssert.Contains(script, "DROP COLUMN \"note\"");
        StringAssert.Contains(script, "ALTER COLUMN \"name\" TYPE");
        Assert.IsFalse(batch.Operations.Any(static operation => operation.NodeId.StartsWith("public.t.", StringComparison.OrdinalIgnoreCase)), "Column changes must not become operations.");
        Assert.IsEmpty(batch.GetUnsupportedOperations(), Describe(batch.GetUnsupportedOperations()));
    }

    [TestMethod]
    public void RemovingATableEmitsOnlyTheDropTable()
    {
        var batch = Generate(
            BuildModel("create table public.t (id bigint not null, name text);"),
            DatabaseModel.Empty);

        Assert.HasCount(1, batch.Operations);
        Assert.IsEmpty(batch.GetUnsupportedOperations(), Describe(batch.GetUnsupportedOperations()));
        StringAssert.Contains(new ScriptGenerator().GenerateSqlScript(batch), "DROP TABLE \"public\".\"t\"");
    }

    [TestMethod]
    public void ForeignKeyPlaceholderAfterTheAlterTableHeaderIsDetected()
    {
        // A foreign key with no referenced columns cannot be emitted; the placeholder follows the ALTER TABLE
        // header, so a start-of-SQL check alone misses it.
        var foreignKey = new ConstraintDefinition(
            "public.a.a_parent_fkey", "a_parent_fkey", "public", "a", "FOREIGN KEY", [], [], "p", [], null, [], [], [], [], [], [], [], [])
        {
            ForeignKeyColumns = ["parent_id"],
        };
        var batch = new OperationBatch
        {
            Operations = [new CreateOperation { NodeId = foreignKey.Key, NodeKind = ChangeNodeKind.Constraint, Node = foreignKey }],
        };

        StringAssert.Contains(batch.Operations[0].GenerateSql(), "-- TODO");
        Assert.IsFalse(batch.Operations[0].GenerateSql().TrimStart().StartsWith("-- TODO", StringComparison.Ordinal));
        Assert.HasCount(1, batch.GetUnsupportedOperations());
    }

    [TestMethod]
    public void IndexPlaceholderInsideTheStatementIsDetected()
    {
        var index = new IndexDefinition("public.t_idx", "t_idx", "public", "t", "btree", [], [], [], [], [], [], [], []);
        var batch = new OperationBatch
        {
            Operations = [new CreateOperation { NodeId = index.Key, NodeKind = ChangeNodeKind.Index, Node = index }],
        };

        Assert.HasCount(1, batch.GetUnsupportedOperations());
    }

    [TestMethod]
    public void UserTodoCommentInsideARoutineBodyIsNotAPlaceholder()
    {
        var batch = Generate(
            DatabaseModel.Empty,
            BuildModel("""
                create function public.f() returns integer language plpgsql as $$
                begin
                    -- TODO: replace this with the real calculation
                    return 1;
                end;
                $$;
                """));

        var script = new ScriptGenerator().GenerateSqlScript(batch);

        StringAssert.Contains(script, "-- TODO: replace this with the real calculation");
        Assert.IsEmpty(batch.GetUnsupportedOperations(), Describe(batch.GetUnsupportedOperations()));
    }

    [TestMethod]
    public void InformationalNotesAreNotPlaceholders()
    {
        var batch = Generate(DatabaseModel.Empty, BuildModel("create table public.empty_table ();"));

        var script = new ScriptGenerator().GenerateSqlScript(batch);

        StringAssert.Contains(script, "-- NOTE:");
        Assert.IsFalse(script.Contains("-- TODO", StringComparison.Ordinal), script);
        Assert.IsEmpty(batch.GetUnsupportedOperations(), Describe(batch.GetUnsupportedOperations()));
    }

    [TestMethod]
    public void PlaceholderAtTheStartOfAnOperationIsStillDetected()
    {
        var enumType = new TypeDefinition("public.mood", "mood", "public", "user-defined", [], [], [], [], [], [], [], []);
        var batch = new OperationBatch
        {
            Operations = [new CreateOperation { NodeId = enumType.Key, NodeKind = ChangeNodeKind.Type, Node = enumType }],
        };

        Assert.HasCount(1, batch.GetUnsupportedOperations());
    }

    private static OperationBatch Generate(DatabaseModel oldModel, DatabaseModel newModel)
        => new ScriptGenerator().Generate(
            new DiffEngine().Diff(oldModel, newModel),
            new DependencyAnalyzer().Analyze(oldModel, newModel));

    private static string Describe(IEnumerable<SqlOperation> operations)
        => string.Join(", ", operations.Select(static operation => $"{operation.NodeKind}:{operation.NodeId}"));

    private static DatabaseModel BuildModel(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
