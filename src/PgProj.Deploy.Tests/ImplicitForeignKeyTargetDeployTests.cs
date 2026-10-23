using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-43: a foreign key without a referenced column list must deploy as complete, executable SQL instead of
/// the fail-closed <c>-- TODO</c> placeholder.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ImplicitForeignKeyTargetDeployTests
{
    [TestMethod]
    public void ImplicitTargetForeignKeyEmitsRealSql()
    {
        var script = GenerateScript("""
            create table public.a (parent_id bigint references public.p);
            create table public.p (id bigint primary key);
            """);

        StringAssert.Contains(script, "FOREIGN KEY (\"parent_id\") REFERENCES \"public\".\"p\" (\"id\")");
        Assert.IsFalse(script.Contains("-- TODO: Add constraint expression", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void NoConstraintOperationContainsAPlaceholder()
    {
        var model = new SemanticPipeline().Execute([new PgQueryParser().Parse("""
            create table public.a (parent_id bigint references public.p);
            create table public.p (id bigint primary key);
            """)]);
        var batch = new ScriptGenerator().Generate(
            new DiffEngine().Diff(DatabaseModel.Empty, model),
            new DependencyAnalyzer().Analyze(DatabaseModel.Empty, model));

        // Checked per constraint operation, anywhere in the statement: the unsupported-operation check (G-28)
        // only recognizes a placeholder at the very start of an operation's SQL, so a placeholder after an
        // ALTER TABLE header (the pre-G-43 output) is not caught until PostgreSQL rejects the script (G-44).
        var placeholders = batch.Operations
            .Where(static operation => operation.NodeKind == ChangeNodeKind.Constraint)
            .Where(static operation => operation.GenerateSql().Contains("-- TODO", StringComparison.Ordinal))
            .Select(static operation => operation.NodeId)
            .ToArray();

        Assert.IsEmpty(placeholders, string.Join(", ", placeholders));
    }

    private static string GenerateScript(string sql)
    {
        var model = new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
        var generator = new ScriptGenerator();
        var batch = generator.Generate(
            new DiffEngine().Diff(DatabaseModel.Empty, model),
            new DependencyAnalyzer().Analyze(DatabaseModel.Empty, model));

        return generator.GenerateSqlScript(batch);
    }
}
