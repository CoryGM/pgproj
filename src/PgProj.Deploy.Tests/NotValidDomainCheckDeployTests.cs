using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-233: a domain's check can be <c>NOT VALID</c>, written <c>ALTER DOMAIN ... ADD CONSTRAINT ... NOT VALID</c> (<c>CREATE DOMAIN</c> has no such clause). It is
/// deployed that way, and compared in one direction: the project stating it valid while the database has it not valid is <c>VALIDATE CONSTRAINT</c>, in place.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class NotValidDomainCheckDeployTests
{
    private const string Domain = "CREATE DOMAIN public.d AS int; ";

    [TestMethod]
    public void ANotValidCheckIsAddedAfterTheDomainAsAlterDomain()
    {
        var model = Build(Domain + "ALTER DOMAIN public.d ADD CONSTRAINT pos CHECK (VALUE > 0) NOT VALID;");

        Assert.IsTrue(model.Diagnostics.All(static diagnostic => diagnostic.Severity != SemanticDiagnosticSeverity.Error), string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
        var script = Script(DatabaseModel.Empty, model);
        StringAssert.Contains(script, "CREATE DOMAIN \"public\".\"d\" AS int;");
        StringAssert.Contains(script, "ALTER DOMAIN \"public\".\"d\" ADD CONSTRAINT \"pos\" CHECK (VALUE > 0) NOT VALID;");
    }

    [TestMethod]
    public void AValidCheckAddedByAlterDomainIsCreatedWithTheDomain()
    {
        var script = Script(DatabaseModel.Empty, Build(Domain + "ALTER DOMAIN public.d ADD CONSTRAINT pos CHECK (VALUE > 0);"));

        StringAssert.Contains(script, "CONSTRAINT \"pos\" CHECK (VALUE > 0)");
        Assert.IsFalse(script.Contains("NOT VALID", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void ANotValidCheckInTheDatabaseThatTheProjectStatesValidIsValidatedInPlace()
    {
        var script = Script(
            Build(Domain + "ALTER DOMAIN public.d ADD CONSTRAINT pos CHECK (VALUE > 0) NOT VALID;"),
            Build("CREATE DOMAIN public.d AS int CONSTRAINT pos CHECK (VALUE > 0);"));

        StringAssert.Contains(script, "ALTER DOMAIN \"public\".\"d\" VALIDATE CONSTRAINT \"pos\";");
        Assert.IsFalse(script.Contains("DROP", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void AValidatedCheckSatisfiesAProjectThatStatesNotValid()
    {
        Assert.AreEqual(string.Empty, Script(
            Build("CREATE DOMAIN public.d AS int CONSTRAINT pos CHECK (VALUE > 0);"),
            Build(Domain + "ALTER DOMAIN public.d ADD CONSTRAINT pos CHECK (VALUE > 0) NOT VALID;")));
    }

    [TestMethod]
    public void TheSameDomainOnBothSidesIsNoChange()
    {
        var model = Build(Domain + "ALTER DOMAIN public.d ADD CONSTRAINT pos CHECK (VALUE > 0) NOT VALID;");

        Assert.AreEqual(string.Empty, Script(model, model));
    }

    [TestMethod]
    public void AChangedNotValidCheckIsReplacedAndStaysNotValid()
    {
        var script = Script(
            Build(Domain + "ALTER DOMAIN public.d ADD CONSTRAINT pos CHECK (VALUE > 0) NOT VALID;"),
            Build(Domain + "ALTER DOMAIN public.d ADD CONSTRAINT pos CHECK (VALUE > 5) NOT VALID;"));

        StringAssert.Contains(script, "DROP CONSTRAINT \"pos\"");
        StringAssert.Contains(script, "ADD CONSTRAINT \"pos\" CHECK (VALUE > 5) NOT VALID;");
    }

    [TestMethod]
    public void AnAlterDomainOfADomainTheProjectDoesNotDeclareIsAWarning()
    {
        var model = Build("ALTER DOMAIN public.nope ADD CONSTRAINT pos CHECK (VALUE > 0);");

        Assert.IsTrue(model.Diagnostics.Any(static d => d.Message.Contains("does not declare that domain", StringComparison.Ordinal)), string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
    }

    private static string Script(DatabaseModel oldModel, DatabaseModel newModel)
    {
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));
        return string.Join("\n", batch.Operations.Where(static operation => operation is not SchemaCreateOperation).Select(static operation => operation.GenerateSql().Replace("\r\n", "\n", StringComparison.Ordinal)));
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
