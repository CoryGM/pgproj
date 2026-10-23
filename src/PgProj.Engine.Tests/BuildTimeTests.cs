using PgProj.Core;

namespace PgProj.Engine.Tests;

/// <summary>
/// G-321: what G-311 and G-313 made faster stays faster, timed as <c>copilot-tests.md</c> §3 allows, each comparing two ways of doing the same work in one
/// run with a wide margin: a build after one edit with a <see cref="ParseCache"/> against a build without it, and a check of one document against a build
/// of its project. The project is one where parsing is a large share of a build: a thousand files, each a table with long comments.
/// </summary>
[TestClass]
[TestCategory("Performance")]
[DoNotParallelize]
public sealed class BuildTimeTests
{
    private static readonly string Root = Path.GetFullPath("/g321");
    private static readonly string ProjectFile = Path.Combine(Root, "pgproj.json");

    [TestMethod]
    public void ABuildAfterOneEditIsFasterWithTheParseCache()
    {
        // Arrange: the cache holds the project's parses; each timed build follows an edit to one file, so that file is parsed again.
        var files = Project();
        var cold = new BuildRequest { Files = files, ProjectFilePath = ProjectFile };
        var cached = cold with { ParseCache = new ParseCache() };
        Build(cached);
        var edit = 0;
        void EditOneFile() => files.Set(File(5), Text(5) + $"-- edit {edit++}\n");

        // Act
        var withoutCache = TimedRuns.Median(() => { EditOneFile(); Build(cold); });
        var withCache = TimedRuns.Median(() => { EditOneFile(); Build(cached); });

        // Assert: about a third of the time on this project (G-311 measured 65 ms against 211 ms); a cache that never hits measures about 0.9.
        Assert.IsLessThan(0.6, withCache / withoutCache, $"with the cache {withCache.TotalMilliseconds:F0} ms, without {withoutCache.TotalMilliseconds:F0} ms");
    }

    [TestMethod]
    public void CheckingOneDocumentIsMuchFasterThanABuild()
    {
        // Arrange
        var files = Project();
        var build = new BuildRequest { Files = files, ProjectFilePath = ProjectFile };
        var check = new DocumentCheckRequest { Files = files, ProjectFilePath = ProjectFile, DocumentPath = File(5) };

        // Act
        var building = TimedRuns.Median(() => Build(build));
        var checking = TimedRuns.Median(() => new PgProjEngine().CheckDocumentAsync(check).GetAwaiter().GetResult());

        // Assert: a hundredth of the time or less on this project (G-313); a fifth is the bound.
        Assert.IsLessThan(0.2, checking / building, $"the check {checking.TotalMilliseconds:F0} ms, the build {building.TotalMilliseconds:F0} ms");
    }

    private static void Build(BuildRequest request)
    {
        var result = new PgProjEngine().BuildAsync(request).GetAwaiter().GetResult();
        Assert.AreEqual(BuildStatus.Succeeded, result.Status);
    }

    private static InMemoryFileSource Project()
    {
        var files = new InMemoryFileSource().Set(ProjectFile, """{ "name": "g321", "version": "1", "source": "./sql" }""");
        for (var i = 0; i < 1_000; i++)
        {
            files.Set(File(i), Text(i));
        }

        return files;
    }

    private static string File(int i) => Path.Combine(Root, "sql", $"t{i}.sql");

    private static string Text(int i)
        => $"CREATE TABLE public.t{i} (id bigint PRIMARY KEY, name text);\n"
            + string.Concat(Enumerable.Range(0, 20).Select(c => $"COMMENT ON COLUMN public.t{i}.name IS 'note {c} {new string('x', 200)}';\n"));
}
