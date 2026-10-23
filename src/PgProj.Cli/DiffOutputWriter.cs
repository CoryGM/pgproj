using PgProj.Diff;
using PgProj.Engine;

namespace PgProj.Cli;

/// <summary>
/// Writes a <see cref="ChangeSet"/> for <c>pgproj diff</c> (G-107). The text form lists every added, removed, and modified object, with a
/// modified object's changed properties, and ends with the <c>Added: n</c>/<c>Removed: n</c>/<c>Modified: n</c> lines <c>diff</c> has
/// always printed, so a script reading those still works. The JSON form (<c>--format json</c>) is the engine's result document
/// (<see cref="CompareResult.ToJson"/>, G-309b).
/// </summary>
public static class DiffOutputWriter
{
    /// <summary>A property value longer than this is shortened in the text form; the JSON form always carries it whole.</summary>
    internal const int MaxTextValueLength = ChangeListText.MaxValueLength;

    public static void WriteText(ChangeSet changeSet, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(changeSet);
        ArgumentNullException.ThrowIfNull(writer);

        WriteChanges(changeSet, writer);
        writer.WriteLine($"Added: {changeSet.Added.Count}");
        writer.WriteLine($"Removed: {changeSet.Removed.Count}");
        writer.WriteLine($"Modified: {changeSet.Modified.Count}");
    }

    /// <summary>
    /// The added, removed, and modified objects of <paramref name="changeSet"/>, without the closing counts. <paramref name="display"/> rewrites each
    /// shown name and value (G-290: a tenant's schema shown as the template's stand-in); null shows them as they are.
    /// </summary>
    internal static void WriteChanges(ChangeSet changeSet, TextWriter writer, Func<string, string>? display = null)
        => ChangeListText.Write(changeSet, writer, display);

    /// <summary>One line per value: null is shown as <c>(none)</c>, whitespace runs collapse, and a long value is shortened.</summary>
    internal static string FormatTextValue(string? value) => ChangeListText.FormatValue(value);
}
