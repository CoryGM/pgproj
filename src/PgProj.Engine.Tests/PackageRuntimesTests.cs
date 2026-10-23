using System.Text.RegularExpressions;

namespace PgProj.Engine.Tests;

/// <summary>
/// G-319: the PgProj.Engine package runs on the runtimes libpg_query is built for, and says so in four places that must agree: the libraries the parser
/// copies (PgProj.Parser.csproj), the libraries the package carries (PgProj.Engine.csproj), the runtimes its build check accepts
/// (buildTransitive/PgProj.Engine.targets), and the platforms its readme names. Adding a runtime to one and not the others fails here.
/// </summary>
[TestClass]
public sealed partial class PackageRuntimesTests
{
    private static readonly string[] Expected = ["linux-arm64", "linux-x64", "osx-arm64", "win-arm64", "win-x64"];

    [TestMethod]
    public void ThePackageCarriesAndAcceptsTheRuntimesTheParserIsBuiltFor()
    {
        var root = RepositoryRoot();
        var parser = File.ReadAllText(Path.Combine(root, "src", "PgProj.Parser", "PgProj.Parser.csproj"));
        var engine = File.ReadAllText(Path.Combine(root, "src", "PgProj.Engine", "PgProj.Engine.csproj"));
        var check = File.ReadAllText(Path.Combine(root, "src", "PgProj.Engine", "buildTransitive", "PgProj.Engine.targets"));
        var readme = File.ReadAllText(Path.Combine(root, "src", "PgProj.Engine", "README.md"));

        CollectionAssert.AreEqual(Expected, Runtimes(LinkedRuntime().Matches(parser)), "the parser's libraries");
        CollectionAssert.AreEqual(Expected, Runtimes(PackedRuntime().Matches(engine)), "the package's libraries");
        CollectionAssert.AreEqual(Expected, Runtimes(AcceptedRuntimes().Match(check).Groups[1].Value.Split(';', StringSplitOptions.RemoveEmptyEntries)), "the build check");
        foreach (var runtime in Expected)
        {
            StringAssert.Contains(readme, runtime, "the readme names the platforms");
        }
    }

    private static string[] Runtimes(MatchCollection matches) => Runtimes(matches.Select(static match => match.Groups[1].Value));

    private static string[] Runtimes(IEnumerable<string> runtimes) => [.. runtimes.Distinct().Order(StringComparer.Ordinal)];

    [GeneratedRegex(@"Link=""runtimes/([a-z0-9-]+)/native/")]
    private static partial Regex LinkedRuntime();

    [GeneratedRegex(@"PackagePath=""runtimes/([a-z0-9-]+)/native/")]
    private static partial Regex PackedRuntime();

    [GeneratedRegex(@"<_PgProjEngineRuntimes>([^<]*)</_PgProjEngineRuntimes>")]
    private static partial Regex AcceptedRuntimes();

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "pgproj.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root (pgproj.slnx) was not found above " + AppContext.BaseDirectory);
    }
}
