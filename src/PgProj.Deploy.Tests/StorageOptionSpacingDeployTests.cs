using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-177: an option that PostgreSQL already has (<c>fillfactor=70</c>) is not set again because the project writes it <c>fillfactor = 70</c>,
/// and what is deployed is still the text as written.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class StorageOptionSpacingDeployTests
{
    [TestMethod]
    public void AnOptionThatHasNotChangedIsNotSetAgain()
    {
        var live = Build("fillfactor=70, autovacuum_enabled=false");
        var project = Build("fillfactor = 80, autovacuum_enabled = false");

        var generator = new ScriptGenerator();
        var script = generator.GenerateSqlScript(generator.Generate(new DiffEngine().Diff(live, project), new DependencyAnalyzer().Analyze(live, project)));

        StringAssert.Contains(script, "SET (fillfactor = 80)");
        Assert.DoesNotContain("autovacuum_enabled", script, StringComparison.Ordinal);
    }

    [TestMethod]
    public void ANewTableIsCreatedWithTheOptionsExactlyAsWritten()
    {
        var project = Build("fillfactor = 70");

        var generator = new ScriptGenerator();
        var script = generator.GenerateSqlScript(generator.Generate(new DiffEngine().Diff(DatabaseModel.Empty, project), new DependencyAnalyzer().Analyze(DatabaseModel.Empty, project)));

        StringAssert.Contains(script, "WITH (fillfactor = 70)");
    }

    private static DatabaseModel Build(string options)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse($"CREATE TABLE public.t (a integer) WITH ({options});")]);
}
