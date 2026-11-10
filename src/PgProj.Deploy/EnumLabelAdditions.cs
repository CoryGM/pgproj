namespace PgProj.Deploy;

/// <summary>
/// How an enum's label list can change in place (G-183): labels may be inserted anywhere, but none removed, renamed, or moved, because
/// <c>ALTER TYPE ... ADD VALUE [BEFORE | AFTER 'label']</c> is the only edit PostgreSQL offers. Anything else is a drop and create, which fails
/// while a column uses the type.
/// </summary>
internal static class EnumLabelAdditions
{
    /// <summary>
    /// The database's labels with the project's <c>@renamed-label</c> annotations applied (G-191), in order, and the renames that do something: one whose old
    /// label the database has and whose new one it does not yet (one already applied names a label the database no longer has, and is nothing to do).
    /// </summary>
    internal static (IReadOnlyList<string> Labels, IReadOnlyList<PgProj.Parser.EnumLabelRename> Renames) ApplyRenames(IReadOnlyList<string> oldLabels, IReadOnlyList<PgProj.Parser.EnumLabelRename> renames)
    {
        var labels = oldLabels.ToList();
        var applied = new List<PgProj.Parser.EnumLabelRename>();
        foreach (var rename in renames)
        {
            var index = labels.FindIndex(label => string.Equals(label, rename.From, StringComparison.Ordinal));
            if (index >= 0 && !labels.Contains(rename.To, StringComparer.Ordinal))
            {
                labels[index] = rename.To;
                applied.Add(rename);
            }
        }

        return (labels, applied);
    }

    /// <summary>
    /// True when <paramref name="newLabels"/> is reached in place (G-183, G-191): the renames applied (<c>RENAME VALUE</c>), then labels only inserted
    /// (<c>ADD VALUE</c>); at least one of the two does something.
    /// </summary>
    internal static bool IsInPlace(IReadOnlyList<string> oldLabels, IReadOnlyList<string> newLabels, IReadOnlyList<PgProj.Parser.EnumLabelRename> renames)
    {
        var (renamed, applied) = ApplyRenames(oldLabels, renames);
        return renamed.SequenceEqual(newLabels, StringComparer.Ordinal) ? applied.Count > 0 : Plan(renamed, newLabels) is not null;
    }

    /// <summary>The database's labels (after the renames) that the project's type does not have: what a swap cannot keep (G-191).</summary>
    internal static IReadOnlyList<string> Removed(IReadOnlyList<string> oldLabels, IReadOnlyList<string> newLabels, IReadOnlyList<PgProj.Parser.EnumLabelRename> renames)
        => ApplyRenames(oldLabels, renames).Labels.Where(label => !newLabels.Contains(label, StringComparer.Ordinal)).ToArray();

    /// <summary>
    /// The statements-to-be for turning <paramref name="oldLabels"/> into <paramref name="newLabels"/>, in the order to run them, or null when the
    /// change is not only insertions (or there is nothing to add). A label that goes after every old label is a plain <c>ADD VALUE</c>; one before an old
    /// label is <c>BEFORE</c> the first old label after it; the others go <c>AFTER</c> the label before them, which exists by then.
    /// </summary>
    internal static IReadOnlyList<(string Label, string? Position, string? Neighbour)>? Plan(IReadOnlyList<string> oldLabels, IReadOnlyList<string> newLabels)
    {
        // An enum with no labels yet takes its first ones in place too (G-249).
        if (newLabels.Count <= oldLabels.Count)
        {
            return null;
        }

        // The old labels must appear in the new list in the same relative order.
        var oldIndex = 0;
        foreach (var label in newLabels)
        {
            if (oldIndex < oldLabels.Count && string.Equals(label, oldLabels[oldIndex], StringComparison.Ordinal))
            {
                oldIndex++;
            }
        }

        if (oldIndex != oldLabels.Count || newLabels.Distinct(StringComparer.Ordinal).Count() != newLabels.Count)
        {
            return null;
        }

        var existing = new HashSet<string>(oldLabels, StringComparer.Ordinal);
        var plan = new List<(string, string?, string?)>();
        for (var index = 0; index < newLabels.Count; index++)
        {
            var label = newLabels[index];
            if (existing.Contains(label))
            {
                continue;
            }

            var nextOld = newLabels.Skip(index + 1).FirstOrDefault(existing.Contains);
            if (nextOld is null)
            {
                plan.Add((label, null, null));
            }
            else if (index == 0)
            {
                plan.Add((label, "BEFORE", nextOld));
            }
            else
            {
                plan.Add((label, "AFTER", newLabels[index - 1]));
            }

            existing.Add(label);
        }

        return plan;
    }
}
