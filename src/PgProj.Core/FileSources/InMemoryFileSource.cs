using System.IO.Enumeration;

namespace PgProj.Core;

/// <summary>
/// A project held in memory (G-309a): a map from full path to text, with an optional version for each. For a front end that receives the
/// project's files in a request, and for tests. Paths are normalized with <see cref="Path.GetFullPath(string)"/> and must be rooted; whether
/// two paths that differ only in case are one file is the choice made when the source is created. Changing the source while a run reads it is
/// not supported (a run works from a snapshot, taken before it starts).
/// </summary>
public sealed class InMemoryFileSource : IProjectFileSource
{
    private readonly bool _caseSensitive;
    private readonly Dictionary<string, (string Text, int? Version)> _files;
    private readonly HashSet<string> _directories;

    /// <param name="caseSensitive">Whether <c>T.sql</c> and <c>t.sql</c> are two files. Default true, as on most servers.</param>
    public InMemoryFileSource(bool caseSensitive = true)
    {
        _caseSensitive = caseSensitive;
        var comparer = caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        _files = new Dictionary<string, (string, int?)>(comparer);
        _directories = new HashSet<string>(comparer);
    }

    /// <summary>The number of files held.</summary>
    public int Count => _files.Count;

    /// <summary>Adds the file at <paramref name="path"/> or replaces it, and the folders above it exist from then on. Returns the source.</summary>
    public InMemoryFileSource Set(string path, string text, int? version = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var fullPath = Normalize(path);
        _files[fullPath] = (text, version);
        for (var directory = Path.GetDirectoryName(fullPath); !string.IsNullOrEmpty(directory); directory = Path.GetDirectoryName(directory))
        {
            _directories.Add(directory);
        }

        return this;
    }

    /// <summary>Makes the folder exist, with nothing in it (a project's <c>source</c> folder may be empty).</summary>
    public InMemoryFileSource AddDirectory(string path)
    {
        for (var directory = Path.TrimEndingDirectorySeparator(Normalize(path)); !string.IsNullOrEmpty(directory); directory = Path.GetDirectoryName(directory))
        {
            _directories.Add(directory);
        }

        return this;
    }

    /// <summary>Removes the file; false when there was none.</summary>
    public bool Remove(string path) => _files.Remove(Normalize(path));

    public bool IsCaseSensitive(string directory) => _caseSensitive;

    public bool FileExists(string path) => _files.ContainsKey(Normalize(path));

    public bool DirectoryExists(string path) => _directories.Contains(Path.TrimEndingDirectorySeparator(Normalize(path)));

    public IEnumerable<string> EnumerateFiles(string directory, string searchPattern, bool recursive)
    {
        var folder = Path.TrimEndingDirectorySeparator(Normalize(directory));
        var comparison = _caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        foreach (var file in _files.Keys)
        {
            var parent = Path.GetDirectoryName(file) ?? string.Empty;
            var inside = recursive
                ? (parent.Length > folder.Length && parent.StartsWith(folder, comparison) && IsSeparator(parent[folder.Length])) || string.Equals(parent, folder, comparison)
                : string.Equals(parent, folder, comparison);
            if (inside && FileSystemName.MatchesSimpleExpression(searchPattern, Path.GetFileName(file), ignoreCase: !_caseSensitive))
            {
                yield return file;
            }
        }
    }

    public string ReadAllText(string path)
        => _files.TryGetValue(Normalize(path), out var file)
            ? file.Text
            : throw new FileNotFoundException($"File '{path}' was not found.", path);

    public int? Version(string path) => _files.TryGetValue(Normalize(path), out var file) ? file.Version : null;

    internal static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathRooted(path))
        {
            throw new ArgumentException($"The path '{path}' is not rooted: a source holds full paths.", nameof(path));
        }

        return Path.GetFullPath(path);
    }

    private static bool IsSeparator(char c) => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar;
}
