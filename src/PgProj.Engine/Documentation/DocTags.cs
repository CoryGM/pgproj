using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PgProj.Engine;

/// <summary>How serious a documentation problem is: an error makes <c>pgproj document</c> exit non-zero (unless told not to), a warning does not.</summary>
public enum DocSeverity
{
    /// <summary>Reported; the exit code is unchanged.</summary>
    Warning,
    /// <summary>Reported; <c>pgproj document</c> exits non-zero unless told not to.</summary>
    Error,
}

/// <summary>A problem found in the documentation of one object: a tag with a value that cannot be read, two tags that cannot be together, a tag repeated.</summary>
/// <param name="Severity">Whether it fails the command.</param>
/// <param name="Tag">The tag the problem is about, without the <c>@</c>.</param>
/// <param name="Message">What is wrong, in a sentence.</param>
internal sealed record DocIssue(DocSeverity Severity, string Tag, string Message);

/// <summary>An object's deprecation: a bare <c>@deprecated</c> is a flag, and its value, when it has one, says why or what replaces it.</summary>
internal sealed record DocDeprecation(bool Flag, string? Reason);

/// <summary>What a <c>COMMENT ON</c> text says, read by <see cref="DocTags.Parse"/>: the description, the starter set's keys, any other tag, and the problems found.</summary>
internal sealed record DocAnnotations
{
    public static readonly DocAnnotations None = new();

    public string? Description { get; init; }

    public string? BusinessName { get; init; }

    public string? Owner { get; init; }

    public string? Classification { get; init; }

    public bool? Pii { get; init; }

    public DocDeprecation? Deprecated { get; init; }

    public string? SinceVersion { get; init; }

    public string? SinceDate { get; init; }

    public int? RetentionMonths { get; init; }

    public string? RetentionDate { get; init; }

    public IReadOnlyList<string> Examples { get; init; } = [];

    public IReadOnlyList<string> SeeAlso { get; init; } = [];

    /// <summary>Tags that are not in the starter set, by name (lower case), each with every value it was given.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Custom { get; init; } = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>The text of a tag whose value could not be read (or that conflicts), by name, so the output still shows what was written.</summary>
    public IReadOnlyDictionary<string, string> Invalid { get; init; } = new Dictionary<string, string>();

    public IReadOnlyList<DocIssue> Issues { get; init; } = [];

    public bool HasContent
        => Description is not null || BusinessName is not null || Owner is not null || Classification is not null || Pii is not null || Deprecated is not null
            || SinceVersion is not null || SinceDate is not null || RetentionMonths is not null || RetentionDate is not null
            || Examples.Count > 0 || SeeAlso.Count > 0 || Custom.Count > 0 || Invalid.Count > 0;
}

/// <summary>
/// Reads the structure out of an object's <c>COMMENT ON</c> text (G-159). The text before the first tag is the description; a line that starts with <c>@name:</c> (spaces before it are
/// allowed, the colon and the value are optional) is a tag, and its value runs on over the indented lines after it. The starter set's tags are <c>description</c>, <c>business-name</c>, <c>owner</c>,
/// <c>classification</c>, <c>pii</c>, <c>deprecated</c>, <c>since-version</c>, <c>since-date</c>, <c>retention-months</c>, <c>retention-date</c>, <c>example</c>, and <c>see-also</c>;
/// any other tag is kept as a custom one. Names are not case sensitive. All of them are optional.
/// </summary>
public static partial class DocTags
{
    /// <summary>The tags of the starter set, in the order the documentation lists them.</summary>
    public static readonly IReadOnlyList<string> StarterSet =
    [
        "description", "business-name", "owner", "classification", "pii", "deprecated", "since-version", "since-date", "retention-months", "retention-date", "example", "see-also",
    ];

    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd", "yyyy-MM-ddTHH:mmK", "yyyy-MM-ddTHH:mm:ssK", "yyyy-MM-ddTHH:mm:ss.FFFFFFFK",
    ];

    [GeneratedRegex(@"^\s*@(?<name>[A-Za-z][A-Za-z0-9_-]*)\s*(?::\s*(?<value>.*))?$", RegexOptions.CultureInvariant)]
    private static partial Regex TagLine();

    /// <summary>The kinds of object <see cref="Parse"/> knows, as <see cref="DocItem"/>s name them.</summary>
    public static readonly IReadOnlyList<string> Kinds = ["schema", "table", "view", "column", "constraint", "trigger", "policy", "index", "routine", "type", "sequence"];

    private static readonly string[] DataKinds = ["schema", "table", "view", "column"];

    /// <summary>
    /// The kinds of object a starter-set tag applies to, for the tags that do not apply to every kind (G-162): classification and retention describe data, so they are for the objects that hold or
    /// group it; <c>pii</c> is for the ones that hold it; an example is for what has a value or a use. The tags not listed (the description, names, owner, links, deprecation, and since) apply to every kind.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> AppliesTo = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
        ["classification"] = DataKinds,
        ["pii"] = ["table", "view", "column"],
        ["retention-months"] = DataKinds,
        ["retention-date"] = DataKinds,
        ["example"] = ["column", "constraint", "routine", "type"],
    };

    /// <summary>Reads <paramref name="comment"/> (a <c>COMMENT ON</c> text, or <see langword="null"/> for none). Given the <paramref name="kind"/> of object it documents, a starter-set tag that does not apply to that kind is warned about (and still kept).</summary>
    internal static DocAnnotations Parse(string? comment, string? kind = null)
    {
        if (string.IsNullOrWhiteSpace(comment))
        {
            return DocAnnotations.None;
        }

        var leading = new List<string>();
        var tags = new List<(string Name, StringBuilder Value)>();
        var trailing = new List<string>();
        foreach (var line in comment.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var match = TagLine().Match(line);
            if (match.Success)
            {
                tags.Add((match.Groups["name"].Value.ToLowerInvariant().Replace('_', '-'), new StringBuilder(match.Groups["value"].Success ? match.Groups["value"].Value.Trim() : string.Empty)));
            }
            else if (tags.Count == 0)
            {
                leading.Add(line);
            }
            else if (line.Length > 0 && char.IsWhiteSpace(line[0]) && !string.IsNullOrWhiteSpace(line))
            {
                var value = tags[^1].Value;
                if (value.Length > 0)
                {
                    value.Append('\n');
                }

                value.Append(line.Trim());
            }
            else if (!string.IsNullOrWhiteSpace(line))
            {
                // Text after the tags that is not a tag's continuation still belongs to the description; it is never dropped.
                trailing.Add(line);
            }
        }

        var issues = new List<DocIssue>();
        if (kind is not null)
        {
            foreach (var name in tags.Select(static tag => tag.Name).Distinct(StringComparer.Ordinal))
            {
                if (AppliesTo.TryGetValue(name, out var kinds) && !kinds.Contains(kind, StringComparer.Ordinal))
                {
                    issues.Add(new DocIssue(DocSeverity.Warning, name, $"@{name} does not apply to a {kind} (it is for {DescribeKinds(kinds)}); it is shown as written."));
                }
            }
        }

        var invalid = new Dictionary<string, string>(StringComparer.Ordinal);
        var custom = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var examples = new List<string>();
        var seeAlso = new List<string>();
        var singles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, builder) in tags)
        {
            var value = builder.ToString().Trim();
            switch (name)
            {
                case "example":
                    AddIfPresent(examples, value);
                    break;
                case "see-also":
                    AddIfPresent(seeAlso, value);
                    break;
                case "description" or "business-name" or "owner" or "classification" or "pii" or "deprecated" or "since-version" or "since-date" or "retention-months" or "retention-date":
                    if (singles.ContainsKey(name))
                    {
                        issues.Add(new DocIssue(DocSeverity.Warning, name, $"@{name} is given more than once; the last one is used."));
                    }

                    singles[name] = value;
                    break;
                default:
                    if (!custom.TryGetValue(name, out var values))
                    {
                        custom[name] = values = [];
                    }

                    values.Add(value);
                    break;
            }
        }

        var description = string.Join('\n', leading).Trim();
        if (singles.TryGetValue("description", out var tagged) && tagged.Length > 0)
        {
            description = description.Length == 0 ? tagged : $"{description}\n\n{tagged}";
        }

        if (trailing.Count > 0)
        {
            var extra = string.Join('\n', trailing).Trim();
            description = description.Length == 0 ? extra : $"{description}\n\n{extra}";
        }

        bool? pii = null;
        if (singles.TryGetValue("pii", out var piiText))
        {
            switch (piiText.ToLowerInvariant())
            {
                case "true" or "yes":
                    pii = true;
                    break;
                case "false" or "no":
                    pii = false;
                    break;
                default:
                    invalid["pii"] = piiText;
                    issues.Add(new DocIssue(DocSeverity.Error, "pii", $"@pii must be true or false (or yes or no), not '{piiText}'."));
                    break;
            }
        }

        DocDeprecation? deprecated = null;
        if (singles.TryGetValue("deprecated", out var deprecatedText))
        {
            deprecated = deprecatedText.ToLowerInvariant() is "false" or "no"
                ? null
                : new DocDeprecation(true, deprecatedText.Length == 0 || deprecatedText.ToLowerInvariant() is "true" or "yes" ? null : deprecatedText);
        }

        var sinceDate = ReadDate(singles, "since-date", utcOnly: false, invalid, issues);
        var retentionDate = ReadDate(singles, "retention-date", utcOnly: true, invalid, issues);

        int? retentionMonths = null;
        if (singles.TryGetValue("retention-months", out var monthsText))
        {
            if (int.TryParse(monthsText, NumberStyles.None, CultureInfo.InvariantCulture, out var months) && months > 0)
            {
                retentionMonths = months;
            }
            else
            {
                invalid["retention-months"] = monthsText;
                issues.Add(new DocIssue(DocSeverity.Error, "retention-months", $"@retention-months must be a whole number of months greater than zero, not '{monthsText}'."));
            }
        }

        if (singles.ContainsKey("retention-months") && singles.ContainsKey("retention-date"))
        {
            // Both cannot be given: keep what was written, and leave the parsed values empty.
            invalid["retention-months"] = singles["retention-months"];
            invalid["retention-date"] = singles["retention-date"];
            retentionMonths = null;
            retentionDate = null;
            issues.RemoveAll(static issue => issue.Tag is "retention-months" or "retention-date");
            issues.Add(new DocIssue(DocSeverity.Error, "retention-date", "@retention-months and @retention-date cannot both be given; give one of them."));
        }

        return new DocAnnotations
        {
            Description = description.Length == 0 ? null : description,
            BusinessName = Single(singles, "business-name"),
            Owner = Single(singles, "owner"),
            Classification = Single(singles, "classification"),
            Pii = pii,
            Deprecated = deprecated,
            SinceVersion = Single(singles, "since-version"),
            SinceDate = sinceDate,
            RetentionMonths = retentionMonths,
            RetentionDate = retentionDate,
            Examples = examples,
            SeeAlso = seeAlso,
            Custom = custom.ToDictionary(static pair => pair.Key, static pair => (IReadOnlyList<string>)pair.Value, StringComparer.Ordinal),
            Invalid = invalid,
            Issues = issues,
        };
    }

    private static string DescribeKinds(IReadOnlyList<string> kinds)
        => kinds.Count == 1 ? $"a {kinds[0]}" : $"{string.Join(", ", kinds.Take(kinds.Count - 1))} and {kinds[^1]}";

    private static void AddIfPresent(List<string> values, string value)
    {
        if (value.Length > 0)
        {
            values.Add(value);
        }
    }

    private static string? Single(Dictionary<string, string> singles, string name)
        => singles.TryGetValue(name, out var value) && value.Length > 0 ? value : null;

    /// <summary>Reads an ISO 8601 date or date-time (a date alone is that day). A date that must be UTC (<paramref name="utcOnly"/>) is refused with any other offset.</summary>
    private static string? ReadDate(Dictionary<string, string> singles, string name, bool utcOnly, Dictionary<string, string> invalid, List<DocIssue> issues)
    {
        if (!singles.TryGetValue(name, out var text) || text.Length == 0)
        {
            return null;
        }

        if (!DateTimeOffset.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            invalid[name] = text;
            issues.Add(new DocIssue(DocSeverity.Error, name, $"@{name} must be an ISO 8601 date (2026-10-02) or date and time (2026-10-02T14:03:00Z), not '{text}'."));
            return null;
        }

        if (utcOnly && parsed.Offset != TimeSpan.Zero)
        {
            invalid[name] = text;
            issues.Add(new DocIssue(DocSeverity.Error, name, $"@{name} must be in UTC (a date alone, or a time ending in Z), not '{text}'."));
            return null;
        }

        return text;
    }
}
