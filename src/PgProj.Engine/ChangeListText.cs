using PgProj.Diff;
using PgProj.Semantics;

namespace PgProj.Engine;

/// <summary>
/// A change set as the list <c>diff</c> prints (G-107): each added, removed, and modified object, with a modified object's changed properties. The engine
/// uses it to tell whether two tenants have the same changes (G-290); the CLI prints it.
/// </summary>
internal static class ChangeListText
{
    /// <summary>A property value longer than this is shortened in the text form; the JSON form always carries it whole.</summary>
    public const int MaxValueLength = 160;

    /// <summary>
    /// The added, removed, and modified objects of <paramref name="changeSet"/>, without the closing counts. <paramref name="display"/> rewrites each
    /// shown name and value (G-290: a tenant's schema shown as the template's stand-in); null shows them as they are.
    /// </summary>
    public static void Write(ChangeSet changeSet, TextWriter writer, Func<string, string>? display = null)
    {
        display ??= static text => text;
        if (changeSet.Added.Count > 0)
        {
            writer.WriteLine($"Added ({changeSet.Added.Count}):");
            foreach (var change in changeSet.Added)
            {
                writer.WriteLine($"  + {change.Node.Kind} {display(DatabaseModel.DisplayKey(change.NodeId))}");
            }
        }

        if (changeSet.Removed.Count > 0)
        {
            writer.WriteLine($"Removed ({changeSet.Removed.Count}):");
            foreach (var change in changeSet.Removed)
            {
                writer.WriteLine($"  - {change.Node.Kind} {display(DatabaseModel.DisplayKey(change.NodeId))}");
            }
        }

        if (changeSet.Modified.Count > 0)
        {
            writer.WriteLine($"Modified ({changeSet.Modified.Count}):");
            foreach (var change in changeSet.Modified)
            {
                writer.WriteLine($"  ~ {change.NewNode.Kind} {display(DatabaseModel.DisplayKey(change.NodeId))}");
                foreach (var detail in change.Details)
                {
                    writer.WriteLine($"      {DiffPropertyLabels.For(detail.PropertyName)}: {display(FormatValue(detail.OldValue))} -> {display(FormatValue(detail.NewValue))}");
                }
            }
        }
    }

    /// <summary>One line per value: null is shown as <c>(none)</c>, whitespace runs collapse, and a long value is shortened.</summary>
    public static string FormatValue(string? value)
    {
        if (value is null)
        {
            return "(none)";
        }

        var collapsed = string.Join(' ', value.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        var quoted = collapsed.Length > MaxValueLength ? collapsed[..MaxValueLength] + "..." : collapsed;
        return $"'{quoted}'";
    }
}
