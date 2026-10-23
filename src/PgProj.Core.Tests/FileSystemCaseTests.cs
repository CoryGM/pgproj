namespace PgProj.Core.Tests;

/// <summary>
/// G-212: whether file names differing only by case are two files is the file system's to say, not the operating system's (macOS's default is
/// case-insensitive). <see cref="FileSystemCase"/> asks the file system with a temporary file; <c>init</c> then writes <c>T.sql</c> and <c>t.sql</c>
/// on a case-sensitive one, and suffixes the second with <c>~2</c> on any other.
/// </summary>
[TestClass]
public sealed class FileSystemCaseTests
{
    [TestMethod]
    public void TheAnswerIsTheFileSystemsOwn()
    {
        var directory = Directory.CreateTempSubdirectory("pgproj-g212-").FullName;
        try
        {
            // An independent check: write two files whose names differ only by case, and count what is there.
            File.WriteAllText(Path.Combine(directory, "probe.txt"), "a");
            File.WriteAllText(Path.Combine(directory, "PROBE.txt"), "b");
            var caseSensitive = Directory.GetFiles(directory).Length == 2;
            foreach (var file in Directory.GetFiles(directory))
            {
                File.Delete(file);
            }

            Assert.AreEqual(caseSensitive, FileSystemCase.IsCaseSensitive(directory));
            Assert.AreEqual(caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase, FileSystemCase.PathComparer(directory));
            Assert.IsEmpty(Directory.GetFiles(directory), "The probe file is deleted.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void ADirectoryThatDoesNotExistYetIsAnsweredByItsNearestParent()
    {
        var directory = Directory.CreateTempSubdirectory("pgproj-g212-").FullName;
        try
        {
            var missing = Path.Combine(directory, "not", "yet");

            Assert.AreEqual(FileSystemCase.IsCaseSensitive(directory), FileSystemCase.IsCaseSensitive(missing));
            Assert.IsFalse(Directory.Exists(Path.Combine(directory, "not")), "Nothing is created on the way.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void AWriteErrorFallsBackToTheOperatingSystem()
    {
        // The user's rule: when the temporary file cannot be written (no permission there), the operating system decides.
        var directory = Directory.CreateTempSubdirectory("pgproj-g212-").FullName;
        try
        {
            Assert.AreEqual(FileSystemCase.DefaultForOperatingSystem(), FileSystemCase.IsCaseSensitive(directory, static _ => throw new UnauthorizedAccessException("denied")));
            Assert.AreEqual(FileSystemCase.DefaultForOperatingSystem(), FileSystemCase.IsCaseSensitive(directory, static _ => throw new IOException("read-only file system")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void TheFallbackIsTheOperatingSystemsUsualFileSystem()
    {
        Assert.AreEqual(!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS(), FileSystemCase.DefaultForOperatingSystem());
    }
}
