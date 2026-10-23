using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-128: a rule is created after the table or view it is on and after what its actions use, whatever order the files are in, and dropped before them; it is written
/// as it was written; a change is a drop and a create; and one that only disappears is a destructive change.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RuleDeployTests
{
    private const string Relations = "CREATE TABLE public.t (id int, n int); CREATE TABLE public.log (id int); CREATE VIEW public.v AS SELECT id, n FROM public.t; ";

    private const string Rules = "CREATE RULE v_ins AS ON INSERT TO public.v DO INSTEAD INSERT INTO public.t VALUES (new.id, new.n); "
        + "CREATE RULE t_log AS ON UPDATE TO public.t WHERE new.n <> old.n DO ALSO INSERT INTO public.log VALUES (old.id);";

    [TestMethod]
    public void RulesAreCreatedAfterTheirRelationsWhateverTheOrderInTheFile()
    {
        var statements = Statements(DatabaseModel.Empty, Build(Rules + Relations));

        var tableAt = statements.FindIndex(static s => s.StartsWith("CREATE TABLE \"public\".\"t\"", StringComparison.Ordinal));
        var logAt = statements.FindIndex(static s => s.StartsWith("CREATE TABLE \"public\".\"log\"", StringComparison.Ordinal));
        var viewAt = statements.FindIndex(static s => s.StartsWith("CREATE OR REPLACE VIEW \"public\".\"v\"", StringComparison.Ordinal));
        var insertAt = statements.FindIndex(static s => s.StartsWith("CREATE RULE v_ins", StringComparison.Ordinal));
        var logRuleAt = statements.FindIndex(static s => s.StartsWith("CREATE RULE t_log", StringComparison.Ordinal));
        var order = string.Join("\n---\n", statements);
        Assert.IsTrue(viewAt >= 0 && insertAt > viewAt && insertAt > tableAt, order);
        Assert.IsTrue(logRuleAt > tableAt && logRuleAt > logAt, order);
    }

    [TestMethod]
    public void ARuleIsWrittenAsItWasWrittenWithoutOrReplace()
    {
        var statements = Statements(DatabaseModel.Empty, Build(Relations + "CREATE OR REPLACE RULE r AS ON DELETE TO public.t DO INSTEAD NOTHING;"));

        Assert.AreEqual("CREATE RULE r AS ON DELETE TO public.t DO INSTEAD NOTHING;", statements.Single(static s => s.Contains("RULE", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void AChangedRuleIsDroppedAndCreatedAgainAndIsNotDestructive()
    {
        var oldModel = Build(Relations + "CREATE RULE r AS ON DELETE TO public.t DO INSTEAD NOTHING;");
        var newModel = Build(Relations + "CREATE RULE r AS ON DELETE TO public.t DO ALSO NOTHING;");
        var batch = Generate(oldModel, newModel);

        var script = string.Join("\n", Sql(batch));
        StringAssert.Contains(script, "DROP RULE \"r\" ON \"public\".\"t\";");
        StringAssert.Contains(script, "CREATE RULE r AS ON DELETE TO public.t DO ALSO NOTHING;");
        Assert.IsEmpty(batch.GetDestructiveOperations());
    }

    [TestMethod]
    public void ARemovedRuleIsDroppedBeforeItsRelationAndIsDestructive()
    {
        var oldModel = Build(Relations + "CREATE RULE r AS ON INSERT TO public.v DO INSTEAD NOTHING;");
        var newModel = Build("CREATE TABLE public.keep (a integer);");
        var batch = Generate(oldModel, newModel);

        var statements = Sql(batch);
        var ruleAt = statements.FindIndex(static s => s.StartsWith("DROP RULE", StringComparison.Ordinal));
        var viewAt = statements.FindIndex(static s => s.StartsWith("DROP VIEW", StringComparison.Ordinal));
        Assert.IsTrue(ruleAt >= 0 && viewAt > ruleAt, string.Join("\n---\n", statements));
        Assert.IsTrue(batch.GetDestructiveOperations().Any(static d => d.Reasons.Any(static reason => reason.StartsWith("rule dropped", StringComparison.Ordinal))));
    }

    [TestMethod]
    public void TheSameRuleWrittenTwoWaysDeploysNothing()
    {
        Assert.IsEmpty(Statements(
            Build(Relations + "CREATE RULE r AS ON DELETE TO public.t DO INSTEAD NOTHING;"),
            Build(Relations + "CREATE OR REPLACE RULE r AS ON DELETE TO public.t\n    DO INSTEAD NOTHING")));
    }

    private static OperationBatch Generate(DatabaseModel oldModel, DatabaseModel newModel)
        => new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));

    private static List<string> Sql(OperationBatch batch)
        => batch.Operations
            .Where(static operation => operation is not SchemaCreateOperation)
            .Select(static operation => operation.GenerateSql().Trim().Replace("\r\n", "\n", StringComparison.Ordinal))
            .ToList();

    private static List<string> Statements(DatabaseModel oldModel, DatabaseModel newModel) => Sql(Generate(oldModel, newModel));

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
