using System.Text;

namespace PgProj.Core;

/// <summary>
/// The project's files on disk: what the loader read before there was a contract (G-309a). Whether file names differ by case is asked of the
/// file system for each directory (<see cref="FileSystemCase"/>, G-212), not decided by the operating system.
/// </summary>
public sealed class DiskFileSource : IProjectFileSource
{
    public static DiskFileSource Instance { get; } = new();

    public bool IsCaseSensitive(string directory) => FileSystemCase.IsCaseSensitive(directory);

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public IEnumerable<string> EnumerateFiles(string directory, string searchPattern, bool recursive)
        => Directory.GetFiles(directory, searchPattern, recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);

    public string ReadAllText(string path) => File.ReadAllText(path);

    public string ReadAllText(string path, Encoding encoding) => File.ReadAllText(path, encoding);
}
