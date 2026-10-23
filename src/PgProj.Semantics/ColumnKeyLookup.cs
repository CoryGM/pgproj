using System.Runtime.CompilerServices;

namespace PgProj.Semantics;

/// <summary>
/// The columns of a model's tables found by key or by table-qualified name (<c>table.column</c>) in constant time (G-314). It answers as a scan of
/// <c>model.Tables.Values.SelectMany(table =&gt; table.Columns)</c> does: the first column, in that order, whose key or table-qualified name is the
/// reference, compared ordinally. A pass that resolved every reference with that scan was quadratic in the project's columns.
/// </summary>
internal sealed class ColumnKeyLookup
{
    // Counts the columns indexed under the counter a test started, so a test can tell that a pass indexes the model once and not per reference (G-314);
    // a test cannot time it. It flows with the execution context to the large-stack thread the passes run on.
    private static readonly AsyncLocal<StrongBox<int>?> s_indexed = new();

    private readonly ColumnDefinition[] _columns;
    private readonly Dictionary<string, int> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _byTableQualifiedName = new(StringComparer.Ordinal);

    public ColumnKeyLookup(DatabaseModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        _columns = [.. model.Tables.Values.SelectMany(static table => table.Columns)];
        for (var index = 0; index < _columns.Length; index++)
        {
            var column = _columns[index];
            _byKey.TryAdd(column.Key, index);
            _byTableQualifiedName.TryAdd(column.TableName is null ? column.Name : $"{column.TableName}.{column.Name}", index);
        }

        if (s_indexed.Value is { } indexed)
        {
            Interlocked.Add(ref indexed.Value, _columns.Length);
        }
    }

    /// <summary>Starts counting the columns indexed from here on in this execution context and the threads it starts (tests only).</summary>
    internal static StrongBox<int> StartCounting()
    {
        var indexed = new StrongBox<int>();
        s_indexed.Value = indexed;
        return indexed;
    }

    /// <summary>The first column whose key or table-qualified name is <paramref name="reference"/>.</summary>
    public ColumnDefinition? Find(string reference)
    {
        var byKey = _byKey.TryGetValue(reference, out var keyIndex) ? keyIndex : int.MaxValue;
        var byName = _byTableQualifiedName.TryGetValue(reference, out var nameIndex) ? nameIndex : int.MaxValue;
        var first = Math.Min(byKey, byName);
        return first == int.MaxValue ? null : _columns[first];
    }

    /// <summary>The first column whose key is <paramref name="key"/>.</summary>
    public ColumnDefinition? FindByKey(string key)
        => _byKey.TryGetValue(key, out var index) ? _columns[index] : null;
}
