using System.Collections;

namespace PgProj.Cli.Tests;

/// <summary>G-336 (the user's design of 2026-11-09): the CLI reads the deploy scripts' variables from <c>PGPROJ_VAR_&lt;name&gt;</c> and never shows a value.</summary>
[TestClass]
public sealed class ScriptVariableSettingsTests
{
    [TestMethod]
    public void EachPrefixedVariableIsAValueByTheNameAfterThePrefix()
    {
        var environment = new Hashtable
        {
            ["PGPROJ_VAR_app_password"] = "s3cret",
            ["pgproj_var_Region"] = "eu", // Windows does not keep the prefix's case
            ["PGPROJ_VAR_"] = "no name",
            ["PGPROJ_TARGET_CONNECTION"] = "Host=x",
            ["PATH"] = "/bin",
        };

        var variables = ScriptVariableSettings.Read(environment);

        CollectionAssert.AreEquivalent(new[] { "app_password", "Region" }, variables.Keys.ToArray());
        Assert.AreEqual("s3cret", variables["app_password"]);
        Assert.AreEqual("eu", variables["Region"]);
    }

    [TestMethod]
    public void AboutNamesTheVariablesSetButNeverTheirValues()
    {
        var lines = AboutCommand.Lines(Result(), "1.0.0", "an archive", ["app_password", "region"]).ToArray();

        var line = lines.Single(static text => text.StartsWith("Script variables set", StringComparison.Ordinal));
        StringAssert.Contains(line, "app_password, region");
        StringAssert.Contains(line, "values not shown");
        Assert.IsFalse(AboutCommand.Lines(Result(), "1.0.0", "an archive").Any(static text => text.StartsWith("Script variables", StringComparison.Ordinal)), "nothing is said when none is set");
    }

    [TestMethod]
    public void PublishsHelpSaysWhereTheValuesComeFromAndThatTheScriptKeepsThePlaceholders()
    {
        var command = PublishCommand.Create(new CliContext());

        StringAssert.Contains(command.Description!, "PGPROJ_VAR_<name>");
        StringAssert.Contains(command.Description!, "A printed or written script keeps the variables as written");
        StringAssert.Contains(command.Description!, "psql -v name=value -f <file>");
    }

    private static PgProj.Engine.AboutResult Result() => new()
    {
        EngineVersion = "1.0.0",
        OperatingSystem = "Linux",
        OperatingSystemArchitecture = "X64",
        ProcessArchitecture = "X64",
        Runtime = ".NET 10",
        RuntimeIdentifier = "linux-x64",
        ParserLoaded = true,
        ProjectStatus = PgProj.Engine.AboutProjectStatus.NotRequested,
    };
}
