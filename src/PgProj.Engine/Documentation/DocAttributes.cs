using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PgProj.Engine;

/// <summary>What the Markdown and HTML output show about an object beyond its structure: the starter set's tags that have a value, and custom tags, as labelled lines. Shared so both say the same things.</summary>
internal static partial class DocAttributes
{
    internal sealed record Attribute(string Label, IReadOnlyList<string> Values);

    /// <summary>The labelled lines of <paramref name="item"/>, in the order of the starter set; tags that were not written are left out.</summary>
    internal static IReadOnlyList<Attribute> For(DocItem item)
    {
        var lines = new List<Attribute>();
        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                lines.Add(new Attribute(label, [value]));
            }
        }

        Add("Business name", item.BusinessName);
        Add("Owner", item.Owner);
        Add("Classification", item.Classification);
        if (item.Pii is { } pii)
        {
            Add("Personal data (PII)", pii ? "yes" : "no");
        }

        if (item.Deprecated is { Flag: true } deprecated)
        {
            Add("Deprecated", string.IsNullOrWhiteSpace(deprecated.Reason) ? "yes" : deprecated.Reason);
        }

        Add("Since version", item.SinceVersion);
        Add("Since date", item.SinceDate);
        if (item.RetentionMonths is { } months)
        {
            Add("Retention", months == 1 ? "1 month" : $"{months.ToString(CultureInfo.InvariantCulture)} months");
        }

        Add("Retain until", item.RetentionDate);
        if (item.Examples is { Count: > 0 })
        {
            lines.Add(new Attribute(item.Examples.Count == 1 ? "Example" : "Examples", item.Examples));
        }

        if (item.Custom is not null)
        {
            foreach (var (name, values) in item.Custom.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                lines.Add(new Attribute(name, values.Where(static value => value.Length > 0).DefaultIfEmpty("yes").ToArray()));
            }
        }

        return lines;
    }

    /// <summary>An anchor for an object's qualified name: lower case, with anything but letters, digits, dots, underscores, and hyphens replaced by a hyphen.</summary>
    internal static string Anchor(string qualifiedName) => NotAnchorCharacter().Replace(qualifiedName.ToLowerInvariant(), "-");

    [GeneratedRegex(@"[^a-z0-9._-]+", RegexOptions.CultureInvariant)]
    private static partial Regex NotAnchorCharacter();

    /// <summary>A UTC time as the documents write it.</summary>
    internal static string Utc(DateTime time) => time.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";

    /// <summary>Every documented object, in document order, which is how the contents are listed.</summary>
    internal static IEnumerable<(string Schema, string Group, DocItem Item)> Objects(DocSchema schema)
    {
        foreach (var table in schema.Tables)
        {
            yield return (schema.Name, "Tables", table);
        }

        foreach (var view in schema.Views)
        {
            yield return (schema.Name, "Views", view);
        }

        foreach (var routine in schema.Routines)
        {
            yield return (schema.Name, "Functions and procedures", routine);
        }

        foreach (var type in schema.Types)
        {
            yield return (schema.Name, "Types", type);
        }

        foreach (var sequence in schema.Sequences)
        {
            yield return (schema.Name, "Sequences", sequence);
        }
    }

    /// <summary>The nullable text of a boolean for a table cell.</summary>
    internal static string YesNo(bool value) => value ? "yes" : "no";

    internal static string Join(IEnumerable<string> values, string separator = ", ") => string.Join(separator, values);

    internal static StringBuilder Line(this StringBuilder builder, string text = "") => builder.Append(text).Append('\n');
}
