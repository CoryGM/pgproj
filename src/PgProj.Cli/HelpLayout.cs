using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using System.Runtime.CompilerServices;
using System.Text;

namespace PgProj.Cli;

/// <summary>
/// The layout of <c>--help</c> (G-168). System.CommandLine's own help printed the root's commands with their whole descriptions and each
/// option as one long line beside its name, with nothing between them, and it has no public way to change that layout. This one prints
/// the sections apart, the commands as one line each, each option on its own line with its description wrapped and indented under it and
/// a blank line after it, and the examples last.
/// </summary>
/// <remarks>
/// A description is written one line per paragraph, list item, or sentence that should start a line; the layout wraps each line to the
/// width and indents its continuation under the line's own indentation (and under the text of a <c>- </c> item). A paragraph that starts
/// with <c>Examples:</c> is printed after the options, under its own heading.
/// </remarks>
public static class HelpLayout
{
    private const string ExamplesHeading = "Examples:";
    private const int DefaultWidth = 100;
    private const int OptionIndent = 2;
    private const int OptionTextIndent = 6;

    private static readonly ConditionalWeakTable<Command, string> Summaries = [];

    /// <summary>Sets the one-line summary the root's help lists <paramref name="command"/> with.</summary>
    public static void SetSummary(Command command, string summary)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);
        Summaries.AddOrUpdate(command, summary);
    }

    /// <summary>The summary set for <paramref name="command"/>, or the first line of its description.</summary>
    public static string Summary(Command command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return Summaries.TryGetValue(command, out var summary)
            ? summary
            : (command.Description ?? string.Empty).Split('\n', 2)[0].Trim();
    }

    /// <summary>Makes <paramref name="root"/>'s help option, which every command inherits, print this layout.</summary>
    public static void Install(RootCommand root)
    {
        ArgumentNullException.ThrowIfNull(root);
        foreach (var option in root.Options.OfType<HelpOption>())
        {
            option.Action = new Action();
        }
    }

    /// <summary>The help of <paramref name="command"/>, wrapped to <paramref name="width"/> columns.</summary>
    public static string Render(Command command, int width = DefaultWidth)
    {
        ArgumentNullException.ThrowIfNull(command);
        width = Math.Max(width, 40);

        var path = CommandPath(command);
        var (description, examples) = SplitExamples(command.Description ?? string.Empty);
        var subcommands = command.Subcommands.Where(static c => !c.Hidden).ToList();
        var options = Options(command);
        var text = new StringBuilder();

        if (command is RootCommand)
        {
            Section(text, null, description, width, 0);
            Section(text, "Usage:", [$"  {path} <command> [options]"], width, 0);
        }
        else
        {
            Section(text, null, [Summary(command)], width, 0);
            Section(text, "Usage:", [$"  {path}{RequiredOptionsUsage(options)} [options]"], width, 0);
            Section(text, "Description:", description, width, 2);
        }

        if (subcommands.Count > 0)
        {
            var nameWidth = subcommands.Max(static c => c.Name.Length) + 4;
            var lines = subcommands.SelectMany(subcommand => new[] { string.Empty, "  " + subcommand.Name.PadRight(nameWidth) + Summary(subcommand) }).Skip(1).ToList();
            Section(text, "Commands:", lines, width, 0);
        }

        if (options.Count > 0)
        {
            var lines = new List<string>();
            foreach (var option in options)
            {
                if (lines.Count > 0)
                {
                    lines.Add(string.Empty);
                }

                lines.Add(new string(' ', OptionIndent) + OptionSignature(option) + (option.Required ? "  (required)" : string.Empty));
                foreach (var paragraph in (option.Description ?? string.Empty).Split('\n').Select(static l => l.TrimEnd('\r')))
                {
                    lines.AddRange(WrapLine(new string(' ', OptionTextIndent) + paragraph.Trim(), width));
                }

                if (DefaultText(option) is { } defaultText)
                {
                    lines.Add(new string(' ', OptionTextIndent) + "Default: " + defaultText);
                }
            }

            Section(text, "Options:", lines, width, 0, wrap: false);
        }

        Section(text, ExamplesHeading, [.. examples.Select(static example => "  " + example)], width, 0, wrap: false);

        if (command is RootCommand && subcommands.Count > 0)
        {
            Section(text, null, [$"Run '{path} <command> --help' for a command's description, options, and examples."], width, 0);
        }

        // Two blank lines between the help and the shell's next prompt.
        text.AppendLine().AppendLine();
        return text.ToString();
    }

    // Each line indented by `indent` and wrapped, unless the lines are already laid out.
    private static void Section(StringBuilder text, string? heading, IReadOnlyList<string> lines, int width, int indent, bool wrap = true)
    {
        if (lines.All(string.IsNullOrWhiteSpace))
        {
            return;
        }

        if (text.Length > 0)
        {
            text.AppendLine();
        }

        if (heading is not null)
        {
            text.AppendLine(heading);
        }

        foreach (var line in lines)
        {
            foreach (var wrapped in wrap ? WrapLine(line.Length == 0 ? line : new string(' ', indent) + line, width) : [line])
            {
                text.AppendLine(wrapped);
            }
        }
    }

    // The description's lines, with the paragraph that starts with "Examples:" taken out (its lines without their own indentation).
    private static (IReadOnlyList<string> Description, IReadOnlyList<string> Examples) SplitExamples(string description)
    {
        var lines = description.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n').ToList();
        var start = lines.FindIndex(static line => line.Trim() == ExamplesHeading);
        if (start < 0)
        {
            return (Trim(lines), []);
        }

        var end = lines.FindIndex(start, static line => line.Trim().Length == 0);
        end = end < 0 ? lines.Count : end;
        var examples = lines[(start + 1)..end];
        var commonIndent = examples.Where(static l => l.Trim().Length > 0).Select(static l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
        lines.RemoveRange(start, end - start);
        return (Trim(lines), [.. examples.Select(l => l.Length >= commonIndent ? l[commonIndent..] : l.TrimStart())]);
    }

    // Leading and trailing blank lines dropped, and runs of blank lines made one.
    private static List<string> Trim(List<string> lines)
    {
        var result = new List<string>();
        foreach (var line in lines.Select(static l => l.TrimEnd()))
        {
            if (line.Length > 0 || (result.Count > 0 && result[^1].Length > 0))
            {
                result.Add(line);
            }
        }

        while (result.Count > 0 && result[^1].Length == 0)
        {
            result.RemoveAt(result.Count - 1);
        }

        return result;
    }

    // One line wrapped to `width`, its continuation indented under its text: under the text after "- " for a list item, and under the
    // second column for a line of two columns (a term, two or more spaces, and its text).
    internal static IEnumerable<string> WrapLine(string line, int width)
    {
        var body = line.TrimStart();
        var indent = line.Length - body.Length;
        var gap = body.IndexOf("  ", StringComparison.Ordinal);
        var column = body.StartsWith("- ", StringComparison.Ordinal) ? 2
            : gap > 0 && body.Length > gap + 2 && body[(gap + 2)..].Trim().Length > 0 ? body.Length - body[gap..].TrimStart().Length
            : 0;
        var prefix = body[..column];
        var hanging = new string(' ', indent + column);
        return Wrap(body[column..], Math.Max(width - indent - column, 20))
            .Select((wrapped, index) => index == 0 ? new string(' ', indent) + prefix + wrapped : hanging + wrapped);
    }

    // Words packed into lines of at most `width` characters; a word longer than that gets a line of its own.
    private static IEnumerable<string> Wrap(string text, int width)
    {
        if (text.Length == 0)
        {
            yield return string.Empty;
            yield break;
        }

        var line = new StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                yield return line.ToString();
                line.Clear();
            }

            if (line.Length > 0)
            {
                line.Append(' ');
            }

            line.Append(word);
        }

        if (line.Length > 0)
        {
            yield return line.ToString();
        }
    }

    private static string CommandPath(Command command)
    {
        var names = new List<string>();
        for (Symbol? symbol = command; symbol is Command current; symbol = current.Parents.FirstOrDefault())
        {
            names.Insert(0, current.Name);
        }

        return string.Join(' ', names);
    }

    // The command's own options, then those it inherits (--help, and --version on the root), without the hidden ones.
    private static List<Option> Options(Command command)
    {
        var options = command.Options.Where(static o => !o.Hidden).ToList();
        for (var parent = command.Parents.OfType<Command>().FirstOrDefault(); parent is not null; parent = parent.Parents.OfType<Command>().FirstOrDefault())
        {
            options.AddRange(parent.Options.Where(o => o.Recursive && !o.Hidden && options.All(existing => existing.Name != o.Name)));
        }

        return options;
    }

    private static string RequiredOptionsUsage(IEnumerable<Option> options)
        => string.Concat(options.Where(static o => o.Required).Select(static o => " " + o.Name + ValuePlaceholder(o)));

    // "-o, --output <path>": the short aliases, then the name, then the value it takes.
    internal static string OptionSignature(Option option)
    {
        var names = option.Aliases
            .Where(static alias => alias.StartsWith('-'))
            .OrderBy(static alias => alias.Length)
            .ThenBy(static alias => alias, StringComparer.Ordinal)
            .Append(option.Name);
        return string.Join(", ", names) + ValuePlaceholder(option);
    }

    private static string ValuePlaceholder(Option option)
    {
        if (option.Arity.MaximumNumberOfValues == 0 || option.ValueType == typeof(bool) || option.ValueType == typeof(bool?))
        {
            return string.Empty;
        }

        var name = $"<{option.HelpName ?? option.Name.TrimStart('-')}>";
        return option.Arity.MinimumNumberOfValues == 0 ? $" [{name}]" : " " + name;
    }

    // The default value, unless the description already says it (most of them do).
    private static string? DefaultText(Option option)
    {
        if (!option.HasDefaultValue || option.ValueType == typeof(bool)
            || (option.Description ?? string.Empty).Contains("default", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return option.GetDefaultValue() switch
        {
            null => null,
            string value => value,
            System.Collections.IEnumerable values => string.Join(", ", values.Cast<object>()),
            var value => value.ToString(),
        };
    }

    // The console's width when help is shown on one (capped so prose stays readable), otherwise the default.
    private static int ConsoleWidth()
    {
        try
        {
            return Console.IsOutputRedirected ? DefaultWidth : Math.Clamp(Console.WindowWidth - 1, 40, DefaultWidth);
        }
        catch (IOException)
        {
            return DefaultWidth;
        }
    }

    private sealed class Action : SynchronousCommandLineAction
    {
        public override bool ClearsParseErrors => true;

        public override int Invoke(ParseResult parseResult)
        {
            parseResult.InvocationConfiguration.Output.Write(Render(parseResult.CommandResult.Command, ConsoleWidth()));
            return 0;
        }
    }
}
