namespace PgProj.Semantics;

/// <summary>
/// How two lists of foreign data options are compared (G-132, the user's design of 2026-11-10): by name, in no order; an option one side has and the other does not is
/// a difference; a value is compared only when neither side names it with a variable and it is not a <c>password</c> (G-336 decision 3, item 6): a database never shows
/// the variable, and pgproj never reads a password back.
/// </summary>
public static class ForeignOptions
{
    /// <summary>The option names whose difference matters, each with the change: <c>ADD</c> (only <paramref name="desired"/> has it), <c>DROP</c>, or <c>SET</c>.</summary>
    public static IReadOnlyList<(string Name, string Action)> Differences(IReadOnlyList<ForeignOptionDefinition> current, IReadOnlyList<ForeignOptionDefinition> desired)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(desired);

        var currentByName = current.GroupBy(static option => option.Name, StringComparer.Ordinal).ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        var desiredByName = desired.GroupBy(static option => option.Name, StringComparer.Ordinal).ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        var differences = new List<(string, string)>();
        foreach (var name in currentByName.Keys.Union(desiredByName.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var has = currentByName.TryGetValue(name, out var mine);
            var wants = desiredByName.TryGetValue(name, out var theirs);
            if (!has)
            {
                differences.Add((name, "ADD"));
            }
            else if (!wants)
            {
                differences.Add((name, "DROP"));
            }
            else if (mine!.IsValueCompared && theirs!.IsValueCompared && !string.Equals(mine.Value, theirs.Value, StringComparison.Ordinal))
            {
                differences.Add((name, "SET"));
            }
        }

        return differences;
    }

    /// <summary>The options as <c>diff</c> shows them: <c>name=value</c>, a value that is not compared (a variable, a password) never shown.</summary>
    public static IReadOnlyList<string> Describe(IReadOnlyList<ForeignOptionDefinition> options)
        => options.Select(static option => option.Variable is { } variable ? $"{option.Name}=:'{variable}'" : option.IsPassword ? $"{option.Name}=*****" : $"{option.Name}={option.Value}")
            .Order(StringComparer.Ordinal)
            .ToArray();
}
