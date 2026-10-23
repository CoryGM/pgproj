using System.Runtime.InteropServices;

namespace PgProj.Parser.Tests;

/// <summary>
/// G-81: the parser's native library is <c>libpg_query.dll</c>, <c>libpg_query.so</c>, or <c>libpg_query.dylib</c> depending on the
/// operating system, found under <c>runtimes/&lt;rid&gt;/native</c>. It used to look for the Windows library only.
/// </summary>
[TestClass]
public sealed class NativeLibraryLocationTests
{
    [TestMethod]
    [DataRow("Windows", Architecture.X64, "win-x64", "libpg_query.dll")]
    [DataRow("Windows", Architecture.Arm64, "win-arm64", "libpg_query.dll")]
    [DataRow("Linux", Architecture.X64, "linux-x64", "libpg_query.so")]
    [DataRow("Linux", Architecture.Arm64, "linux-arm64", "libpg_query.so")]
    [DataRow("OSX", Architecture.Arm64, "osx-arm64", "libpg_query.dylib")]
    public void ASupportedPlatformMapsToItsRuntimeAndLibraryFile(string platform, Architecture architecture, string runtimeIdentifier, string fileName)
    {
        // Arrange
        var os = OSPlatform.Create(platform);

        // Act
        var target = PgQueryInterop.GetTarget(os, architecture);

        // Assert
        Assert.IsNotNull(target);
        Assert.AreEqual(runtimeIdentifier, target.RuntimeIdentifier);
        Assert.AreEqual(fileName, target.FileName);
    }

    [TestMethod]
    [DataRow("OSX", Architecture.X64)]
    [DataRow("Linux", Architecture.Arm)]
    [DataRow("Linux", Architecture.X86)]
    [DataRow("FreeBSD", Architecture.X64)]
    public void APlatformWithNoPackagedLibraryHasNoTarget(string platform, Architecture architecture)
    {
        // Arrange
        var os = OSPlatform.Create(platform);

        // Act
        var target = PgQueryInterop.GetTarget(os, architecture);

        // Assert
        Assert.IsNull(target);
    }

    [TestMethod]
    public void TheLibraryIsLookedForBesideTheExecutableThenInTheRuntimeFolders()
    {
        // Arrange
        var target = new PgQueryInterop.NativeLibraryTarget("linux-x64", "libpg_query.so");
        var baseDirectory = Path.Combine("app", "bin");

        // Act
        var paths = PgQueryInterop.CandidatePaths(baseDirectory, target);

        // Assert
        CollectionAssert.AreEqual(
            new[]
            {
                Path.Combine("app", "bin", "libpg_query.so"),
                Path.Combine("app", "bin", "linux-x64", "libpg_query.so"),
                Path.Combine("app", "bin", "runtimes", "linux-x64", "native", "libpg_query.so"),
            },
            paths);
    }

    [TestMethod]
    public void TheLibraryForThisMachineIsInTheBuildOutput()
    {
        // Arrange
        var os = OperatingSystem.IsWindows() ? OSPlatform.Windows : OperatingSystem.IsLinux() ? OSPlatform.Linux : OSPlatform.OSX;
        var target = PgQueryInterop.GetTarget(os, RuntimeInformation.ProcessArchitecture);
        Assert.IsNotNull(target, "This test machine's platform has no packaged libpg_query.");

        // Act
        var found = PgQueryInterop.CandidatePaths(AppContext.BaseDirectory, target).Any(File.Exists);

        // Assert
        Assert.IsTrue(found, $"{target.FileName} was not copied to the build output under runtimes/{target.RuntimeIdentifier}/native.");
    }
}
