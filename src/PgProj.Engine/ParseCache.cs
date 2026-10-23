using PgProj.Parser;

namespace PgProj.Engine;

/// <summary>
/// The parse of each of a project's files, kept from one build to the next (G-311), so a build after an edit parses only the files whose text changed. An
/// editor keeps one per open project and passes it on every <see cref="BuildRequest"/> of that project (<see cref="BuildRequest.ParseCache"/>); a program
/// that builds once, like the CLI, passes none. A file's parse is reused when its path, its text, and the schema an unqualified name is created in (the
/// first of <c>searchPath</c>, or the tenant template's schema) are what they were; the semantic stage still runs on the whole project.
/// </summary>
/// <remarks>
/// Safe to use from several threads. A build drops the entries of the files it no longer has, so the cache holds one project's files; one cache shared by
/// two projects only parses more. <see cref="Clear"/> empties it.
/// </remarks>
public sealed class ParseCache
{
    private readonly Lock _gate = new();
    private readonly Dictionary<EntryKey, Entry> _entries = [];
    private int _parses;

    /// <summary>The number of file parses held.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Drops every parse held.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    /// <summary>The parses made through this cache because none was held (tests).</summary>
    internal int Parses => Volatile.Read(ref _parses);

    /// <summary>
    /// The parse of <paramref name="text"/>, the file at <paramref name="path"/> read with <paramref name="defaultSchema"/>: the one held when the text is the
    /// same, otherwise <paramref name="parse"/>'s, which is then held. The tree is never changed after parsing (the steps after it make new trees), so one
    /// tree can serve every build.
    /// </summary>
    internal SqlSyntaxTree GetOrParse(string path, string defaultSchema, string text, Func<string, SqlSyntaxTree> parse)
    {
        var key = new EntryKey(path, defaultSchema);
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var held) && string.Equals(held.Text, text, StringComparison.Ordinal))
            {
                return held.Tree;
            }
        }

        // Parsed outside the lock, so files parse in parallel (G-312).
        var tree = parse(text);
        Interlocked.Increment(ref _parses);
        lock (_gate)
        {
            _entries[key] = new Entry(text, tree);
        }

        return tree;
    }

    /// <summary>Drops every parse but those of <paramref name="used"/>: a build's files, so a file removed or read another way is not held.</summary>
    internal void KeepOnly(IEnumerable<EntryKey> used)
    {
        var keep = used.ToHashSet();
        lock (_gate)
        {
            foreach (var key in _entries.Keys.Where(key => !keep.Contains(key)).ToArray())
            {
                _entries.Remove(key);
            }
        }
    }

    /// <summary>A file and the schema its unqualified names were created in.</summary>
    internal readonly record struct EntryKey(string Path, string DefaultSchema);

    private sealed record Entry(string Text, SqlSyntaxTree Tree);
}
