using System.Text;

namespace PgProj.Core;

/// <summary>
/// Where a project's files are read from (G-309a). The loader reads <c>pgproj.json</c>, finds the schema files, expands the deploy scripts'
/// <c>:r</c> includes, and reads the tenant file only through this contract, so a project can come from disk, from memory (a web front end
/// that receives the files in a request), or from an editor's unsaved buffers laid over the files on disk.
/// <para>
/// Paths are full paths in the source's own space: real paths for a disk, and for any other source whatever rooted paths its owner chooses
/// (<c>C:\work\app\sql\a.sql</c>, <c>/work/app/sql/a.sql</c>). The loader works in full paths and does not translate them. A source is read
/// from one thread at a time during a run; the engine reads a run's files into a snapshot first, so a file changed meanwhile cannot make
/// a run inconsistent.
/// </para>
/// </summary>
public interface IProjectFileSource
{
    /// <summary>
    /// Whether file names in <paramref name="directory"/> differ by case (G-212): two paths are one file when they differ only in case on a
    /// source that is case-insensitive. A disk asks its file system for each directory; a source that is not a disk answers for all of it.
    /// </summary>
    bool IsCaseSensitive(string directory);

    bool FileExists(string path);

    bool DirectoryExists(string path);

    /// <summary>The full paths of the files in <paramref name="directory"/> whose name matches <paramref name="searchPattern"/> (<c>*</c> and <c>?</c>), and, with <paramref name="recursive"/>, in the folders below it. In no particular order.</summary>
    IEnumerable<string> EnumerateFiles(string directory, string searchPattern, bool recursive);

    string ReadAllText(string path);

    /// <summary>The text of the file read as <paramref name="encoding"/> (a disk uses it, strictly if the encoding is; a source that holds text already ignores it).</summary>
    string ReadAllText(string path, Encoding encoding) => ReadAllText(path);

    /// <summary>The version of the file's content, when the source has one: an editor's document version, so a result can say which state of a buffer it describes. Null when it has none.</summary>
    int? Version(string path) => null;
}
