using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-139: a rule that the live database records as using a column this publish retypes (or a routine or operator it drops and creates again) is dropped before the
/// change and created again after it, from the project, as a view is (G-103, G-105); a rule the project does not have is refused before anything runs.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RuleColumnChangeDeployTests
{
    private static string Project(string nType)
        => $"CREATE TABLE public.t (id int, n {nType}); CREATE TABLE public.log (id int); "
            + "CREATE RULE t_log AS ON UPDATE TO public.t WHERE new.n <> old.n DO ALSO INSERT INTO public.log VALUES (old.id);";

    private static readonly string RuleKey = RuleKeys.Create("public", "t", "t_log");

    [TestMethod]
    public void ARuleThatUsesTheColumnIsDroppedBeforeTheAlterAndCreatedAgainAfterIt()
    {
        var statements = Statements(Live(Project("int"), [new LiveDependency("rule", RuleKey, "public.t", "n")]), Build(Project("bigint")));

        var drop = statements.FindIndex(static s => s.StartsWith("DROP RULE \"t_log\"", StringComparison.Ordinal));
        var alter = statements.FindIndex(static s => s.StartsWith("ALTER TABLE \"public\".\"t\" ALTER COLUMN \"n\" TYPE", StringComparison.Ordinal));
        var create = statements.FindIndex(static s => s.StartsWith("CREATE RULE t_log", StringComparison.Ordinal));
        Assert.IsTrue(drop >= 0 && alter > drop && create > alter, string.Join("\n---\n", statements));
    }

    [TestMethod]
    public void ARuleThatUsesAnotherColumnIsLeftAlone()
    {
        var statements = Statements(Live(Project("int"), [new LiveDependency("rule", RuleKey, "public.t", "id")]), Build(Project("bigint")));

        Assert.IsFalse(statements.Any(static s => s.Contains("RULE", StringComparison.Ordinal)), string.Join("\n---\n", statements));
    }

    [TestMethod]
    public void ARuleThatUsesARoutineBeingDroppedAndCreatedAgainIsRebuilt()
    {
        var before = "CREATE FUNCTION public.f(x int) RETURNS int LANGUAGE sql AS $$ SELECT x $$; CREATE TABLE public.t (id int); CREATE TABLE public.log (id int); CREATE RULE t_log AS ON INSERT TO public.t DO ALSO INSERT INTO public.log VALUES (public.f(new.id));";
        var after = before.Replace("RETURNS int LANGUAGE sql AS $$ SELECT x $$", "RETURNS bigint LANGUAGE sql AS $$ SELECT x::bigint $$", StringComparison.Ordinal);

        var statements = Statements(Live(before, [new LiveDependency("rule", RuleKey, "public.f(int4)", null, "routine")]), Build(after));

        var dropRule = statements.FindIndex(static s => s.StartsWith("DROP RULE", StringComparison.Ordinal));
        var dropFunction = statements.FindIndex(static s => s.StartsWith("DROP FUNCTION", StringComparison.Ordinal));
        var createRule = statements.FindIndex(static s => s.StartsWith("CREATE RULE", StringComparison.Ordinal));
        var createFunction = statements.FindIndex(static s => s.StartsWith("CREATE OR REPLACE FUNCTION", StringComparison.Ordinal));
        Assert.IsTrue(dropRule >= 0 && dropFunction > dropRule && createFunction > dropFunction && createRule > createFunction, string.Join("\n---\n", statements));
    }

    [TestMethod]
    public void ARuleTheProjectDoesNotHaveIsRefused()
    {
        var batch = Batch(Live(Project("int"), [new LiveDependency("rule", "rule:public.t.other", "public.t", "n")]), Build(Project("bigint")));

        var blocked = batch.Operations.OfType<BlockedOperation>().Single();
        StringAssert.Contains(blocked.Message, "rule:public.t.other");
    }

    [TestMethod]
    public void ARebuiltRuleIsNotDestructive()
    {
        var batch = Batch(Live(Project("int"), [new LiveDependency("rule", RuleKey, "public.t", "n")]), Build(Project("bigint")));

        Assert.IsFalse(batch.GetDestructiveOperations().Any(static d => d.Reasons.Any(static reason => reason.StartsWith("rule dropped", StringComparison.Ordinal))));
    }

    private static DatabaseModel Live(string sql, IReadOnlyList<LiveDependency> dependencies)
    {
        var model = Build(sql);
        model.LiveDependencies = dependencies;
        return model;
    }

    private static OperationBatch Batch(DatabaseModel oldModel, DatabaseModel newModel)
        => new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));

    private static List<string> Statements(DatabaseModel oldModel, DatabaseModel newModel)
        => Batch(oldModel, newModel).Operations
            .Where(static operation => operation is not SchemaCreateOperation)
            .Select(static operation => operation.GenerateSql().Trim().Replace("\r\n", "\n", StringComparison.Ordinal))
            .ToList();

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
