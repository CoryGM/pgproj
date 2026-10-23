namespace PgProj.Core;

/// <summary>
/// Open, unsaved documents laid over another source (G-309a), normally the files on disk: an editor's buffers. A document set here replaces the
/// file of that path in the source under it, and one for a path the source does not have is found too, as a new file. Documents are set, and
/// removed, as the editor opens, edits, and closes them; each may carry the editor's version of it.
/// </summary>
public sealed class OverlayFileSource : IProjectFileSource
{
    private readonly IProjectFileSource _under;
    private readonly InMemoryFileSource _documents;

    /// <param name="under">The source the documents are laid over.</param>
    /// <param name="caseSensitive">Whether two documents whose paths differ only in case are two; by default as the source under it answers for the current directory.</param>
    public OverlayFileSource(IProjectFileSource under, bool? caseSensitive = null)
    {
        _under = under ?? throw new ArgumentNullException(nameof(under));
        // Two documents are one when the files under them would be.
        _documents = new InMemoryFileSource(caseSensitive ?? under.IsCaseSensitive(Environment.CurrentDirectory));
    }

    /// <summary>The documents currently laid over the source.</summary>
    public int Count => _documents.Count;

    /// <summary>Lays the document at <paramref name="path"/> over the source, or replaces it with a newer version of it.</summary>
    public OverlayFileSource Set(string path, string text, int? version = null)
    {
        _documents.Set(path, text, version);
        return this;
    }

    /// <summary>Makes the folder exist, with nothing in it.</summary>
    public OverlayFileSource AddDirectory(string path)
    {
        _documents.AddDirectory(path);
        return this;
    }

    /// <summary>Takes the document away (the editor closed it): the file under it, if any, is seen again. False when there was none.</summary>
    public bool Remove(string path) => _documents.Remove(path);

    public bool IsCaseSensitive(string directory) => _under.IsCaseSensitive(directory);

    public bool FileExists(string path) => _documents.FileExists(path) || _under.FileExists(path);

    public bool DirectoryExists(string path) => _documents.DirectoryExists(path) || _under.DirectoryExists(path);

    public IEnumerable<string> EnumerateFiles(string directory, string searchPattern, bool recursive)
    {
        var comparer = _under.IsCaseSensitive(directory) ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var seen = new HashSet<string>(comparer);
        // A document's path wins over the spelling of the file under it.
        foreach (var file in _documents.EnumerateFiles(directory, searchPattern, recursive))
        {
            if (seen.Add(file))
            {
                yield return file;
            }
        }

        if (!_under.DirectoryExists(directory))
        {
            yield break;
        }

        foreach (var file in _under.EnumerateFiles(directory, searchPattern, recursive))
        {
            if (seen.Add(file))
            {
                yield return file;
            }
        }
    }

    public string ReadAllText(string path) => _documents.FileExists(path) ? _documents.ReadAllText(path) : _under.ReadAllText(path);

    public string ReadAllText(string path, System.Text.Encoding encoding) => _documents.FileExists(path) ? _documents.ReadAllText(path) : _under.ReadAllText(path, encoding);

    public int? Version(string path) => _documents.FileExists(path) ? _documents.Version(path) : _under.Version(path);
}
