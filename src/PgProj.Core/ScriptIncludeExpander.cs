using System.Text;

namespace PgProj.Core;

/// <summary>One file that makes up a pre- or post-deployment script (G-124).</summary>
/// <param name="FilePath">The file's full path.</param>
/// <param name="Text">
/// The file as written, except that each <c>:r</c> line is a <c>-- :r path</c> comment on the same line, so a position in this text is a
/// position in the file and the text is valid SQL to syntax-check on its own.
/// </param>
public sealed record ScriptSourceFile(string FilePath, string Text);

/// <summary>A pre- or post-deployment script with its <c>:r</c> includes expanded (G-124).</summary>
/// <param name="Text">The whole script, each include replaced by the included file's own (expanded) contents.</param>
/// <param name="Files">The script file and every file it includes, each once, the script first.</param>
public sealed record ExpandedScript(string Text, IReadOnlyList<ScriptSourceFile> Files)
{
    /// <summary>The script as a tree of files, each with its <c>run when=</c> marker (G-290e), which <see cref="ScriptPart.Render"/> turns back into <see cref="Text"/>.</summary>
    public ScriptPart? Root { get; init; }

    /// <summary>Every file of the script with the marker it has, in the order they are included.</summary>
    public IEnumerable<ScriptPart> Parts => Root is null ? [] : Root.Descendants();
}

/// <summary>
/// One file of a pre- or post-deployment script (G-290e): its text, interleaved with the files it includes, and the <c>-- pgproj: run when=...</c> marker
/// among the comment lines at its top, if any. A file that is skipped is skipped with everything it includes.
/// </summary>
/// <param name="FilePath">The file's full path.</param>
/// <param name="RunWhen">The marker's value as written (<c>changed</c>, <c>tenant-created</c>, ...), or null without one: the file always runs.</param>
/// <param name="RunWhenLine">The marker's line, for a message.</param>
/// <param name="Pieces">The file's own text (a string) and its includes (a <see cref="ScriptInclude"/>), in order.</param>
public sealed record ScriptPart(string FilePath, string? RunWhen, int RunWhenLine, IReadOnlyList<object> Pieces)
{
    /// <summary>
    /// The script with the files <paramref name="runs"/> refuses replaced by a comment saying so and why. With every file running, this is
    /// <see cref="ExpandedScript.Text"/> exactly.
    /// </summary>
    public string Render(Func<ScriptPart, bool> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        if (!runs(this))
        {
            return SkippedComment(this);
        }

        var text = new StringBuilder();
        foreach (var piece in Pieces)
        {
            if (piece is ScriptInclude include)
            {
                text.Append(include.Marker).Append('\n').Append(include.Part.Render(runs).TrimEnd('\r', '\n'));
            }
            else
            {
                text.Append((string)piece);
            }
        }

        return text.ToString();
    }

    /// <summary>This file and every file it includes, this one first.</summary>
    public IEnumerable<ScriptPart> Descendants()
    {
        yield return this;
        foreach (var include in Pieces.OfType<ScriptInclude>())
        {
            foreach (var part in include.Part.Descendants())
            {
                yield return part;
            }
        }
    }

    /// <summary>What stands for a skipped file in the script: a comment naming it and its marker.</summary>
    public static string SkippedComment(ScriptPart part)
        => $"-- pgproj: skipped {Path.GetFileName(part.FilePath)} (it runs when={part.RunWhen})";
}

/// <summary>A <c>:r</c> include of a <see cref="ScriptPart"/>: the <c>-- :r path</c> comment that replaces the line, and the included file.</summary>
public sealed record ScriptInclude(string Marker, ScriptPart Part);

/// <summary>
/// Expands SSDT-style <c>:r &lt;path&gt;</c> includes in a pre- or post-deployment script (G-124). A line whose first word is <c>:r</c>,
/// found outside strings, comments, and dollar-quoted bodies, is replaced by a <c>-- :r &lt;path&gt;</c> comment and the contents of the named
/// file, which may include others. The path is relative to the file that contains the line, as in SSDT. A missing file or an include
/// cycle is a <see cref="PgProjectValidationException"/> that names the chain. Other SQLCMD syntax (<c>$(name)</c> variables and every
/// other command) is not interpreted.
/// </summary>
public static class ScriptIncludeExpander
{
    public static ExpandedScript Expand(string scriptFilePath) => Expand(scriptFilePath, DiskFileSource.Instance);

    /// <summary>Expands the script at <paramref name="scriptFilePath"/> in <paramref name="source"/>, reading every file it includes from there too (G-309a).</summary>
    public static ExpandedScript Expand(string scriptFilePath, IProjectFileSource source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptFilePath);
        ArgumentNullException.ThrowIfNull(source);

        var files = new List<ScriptSourceFile>();
        var fullPath = Path.GetFullPath(scriptFilePath);
        // Two paths are one file when the script's file system says so (G-212).
        var pathComparer = source.IsCaseSensitive(Path.GetDirectoryName(fullPath)!) ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var (text, root) = ExpandFile(fullPath, [], files, pathComparer, source);
        return new ExpandedScript(text, files) { Root = root };
    }

    private static (string Text, ScriptPart Part) ExpandFile(string fullPath, List<string> chain, List<ScriptSourceFile> files, StringComparer pathComparer, IProjectFileSource source)
    {
        var content = source.ReadAllText(fullPath);
        chain.Add(fullPath);

        var expanded = new StringBuilder(content.Length);
        var own = new StringBuilder(content.Length);
        var pieces = new List<object>();
        var copiedTo = 0;
        foreach (var directive in FindDirectives(content))
        {
            var lineNumber = LineOf(content, directive.Start);
            var pathText = ReadPath(content.AsSpan(directive.PathStart, directive.End - directive.PathStart));
            if (pathText.Length == 0)
            {
                throw new PgProjectValidationException($"{Describe(fullPath, lineNumber)}: ':r' needs the path of a file to include.");
            }

            var includePath = ResolveInclude(Path.GetDirectoryName(fullPath)!, pathText);
            if (chain.Contains(includePath, pathComparer))
            {
                throw new PgProjectValidationException(
                    $"{Describe(fullPath, lineNumber)}: ':r {pathText}' includes a file that is already being included: {string.Join(" -> ", chain.Append(includePath))}.");
            }

            if (!source.FileExists(includePath))
            {
                throw new PgProjectValidationException(
                    $"{Describe(fullPath, lineNumber)}: ':r {pathText}' names a file that does not exist: '{includePath}'.");
            }

            var marker = $"-- :r {pathText}";
            var (includedText, includedPart) = ExpandFile(includePath, chain, files, pathComparer, source);
            var included = includedText.TrimEnd('\r', '\n');

            expanded.Append(content, copiedTo, directive.Start - copiedTo).Append(marker).Append('\n').Append(included);
            own.Append(content, copiedTo, directive.Start - copiedTo).Append(marker);
            pieces.Add(content[copiedTo..directive.Start]);
            pieces.Add(new ScriptInclude(marker, includedPart));
            copiedTo = directive.End;
        }

        expanded.Append(content, copiedTo, content.Length - copiedTo);
        own.Append(content, copiedTo, content.Length - copiedTo);
        pieces.Add(content[copiedTo..]);
        var (runWhen, runWhenLine) = ReadRunWhen(content, fullPath);

        chain.RemoveAt(chain.Count - 1);
        if (!files.Any(file => pathComparer.Equals(file.FilePath, fullPath)))
        {
            // A file is listed once, and the script itself first: it is added after its includes, so put it in front.
            files.Insert(chain.Count == 0 ? 0 : files.Count, new ScriptSourceFile(fullPath, own.ToString()));
        }

        return (expanded.ToString(), new ScriptPart(fullPath, runWhen, runWhenLine, pieces));
    }

    /// <summary>
    /// A file's <c>-- pgproj: run when=value</c> marker (G-290e): a line comment among the comment and blank lines at the top of the file, before its
    /// first statement. Null when there is none; a second marker there is an error.
    /// </summary>
    private static (string? Value, int Line) ReadRunWhen(string content, string fullPath)
    {
        string? value = null;
        var valueLine = 0;
        var lineNumber = 0;
        foreach (var line in content.Split('\n'))
        {
            lineNumber++;
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (!trimmed.StartsWith("--", StringComparison.Ordinal))
            {
                break;
            }

            var match = RunWhenMarker.Match(trimmed);
            if (!match.Success)
            {
                continue;
            }

            if (value is not null)
            {
                throw new PgProjectValidationException($"{Describe(fullPath, lineNumber)}: a second '-- pgproj: run when=' marker (the first is on line {valueLine}); a file has one.");
            }

            value = match.Groups["value"].Value;
            valueLine = lineNumber;
        }

        return (value, valueLine);
    }

    private static readonly System.Text.RegularExpressions.Regex RunWhenMarker =
        new(@"^--\s*pgproj:\s*run\s+when\s*=\s*(?<value>\S+)\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static string Describe(string filePath, int line) => $"{filePath}:{line}";

    private static int LineOf(string text, int position)
    {
        var line = 1;
        for (var i = 0; i < position; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    /// <summary>
    /// The full path a <c>:r</c> path names, relative to <paramref name="directory"/> (the including file's) unless rooted. <c>\</c> and <c>/</c> are both a folder
    /// separator on every operating system (G-317, the user's decision): a project written on Windows, as SQL Server Data Tools users write it
    /// (<c>:r .\Seed\file.sql</c>), builds on Linux and macOS too, so a file whose name contains <c>\</c> cannot be included there.
    /// </summary>
    internal static string ResolveInclude(string directory, string pathText)
    {
        var path = pathText.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(directory, path));
    }

    /// <summary>The path on a <c>:r</c> line: what follows the command, trimmed, without one pair of surrounding double quotes.</summary>
    private static string ReadPath(ReadOnlySpan<char> text)
    {
        var path = text.Trim();
        if (path.Length >= 2 && path[0] == '"' && path[^1] == '"')
        {
            path = path[1..^1].Trim();
        }

        return path.ToString();
    }

    /// <summary>Where a <c>:r</c> line starts (its first character, indentation included), where its path starts, and where it ends.</summary>
    private readonly record struct Directive(int Start, int PathStart, int End);
    private static List<Directive> FindDirectives(string text)
    {
        var directives = new List<Directive>();
        var lineStart = 0;
        var atLineStart = true;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\n')
            {
                atLineStart = true;
                i++;
                continue;
            }

            if (atLineStart && (c == ' ' || c == '\t' || c == '\r'))
            {
                i++;
                continue;
            }

            if (atLineStart)
            {
                lineStart = LineStartOf(text, i);
                if (IsIncludeCommand(text, i))
                {
                    var end = i;
                    while (end < text.Length && text[end] != '\n' && text[end] != '\r')
                    {
                        end++;
                    }

                    directives.Add(new Directive(lineStart, i + 2, end));
                    i = end;
                    continue;
                }

                atLineStart = false;
            }

            i = c switch
            {
                '-' when Next(text, i) == '-' => SkipLineComment(text, i),
                '/' when Next(text, i) == '*' => SkipBlockComment(text, i),
                '\'' => SkipQuoted(text, i, '\'', backslashEscapes: i > 0 && text[i - 1] is 'e' or 'E' && !IsIdentifierPart(i > 1 ? text[i - 2] : ' ')),
                '"' => SkipQuoted(text, i, '"', backslashEscapes: false),
                '$' => SkipDollarQuote(text, i),
                _ => i + 1,
            };
        }

        return directives;
    }

    private static int LineStartOf(string text, int position)
    {
        var start = position;
        while (start > 0 && text[start - 1] != '\n')
        {
            start--;
        }

        return start;
    }

    private static char Next(string text, int i) => i + 1 < text.Length ? text[i + 1] : '\0';

    private static bool IsIncludeCommand(string text, int i)
        => text[i] == ':' && i + 2 < text.Length && text[i + 1] is 'r' or 'R' && text[i + 2] is ' ' or '\t' or '"';

    private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';

    private static int SkipLineComment(string text, int i)
    {
        while (i < text.Length && text[i] != '\n')
        {
            i++;
        }

        return i;
    }

    /// <summary>PostgreSQL block comments nest.</summary>
    private static int SkipBlockComment(string text, int i)
    {
        var depth = 0;
        while (i < text.Length)
        {
            if (text[i] == '/' && Next(text, i) == '*')
            {
                depth++;
                i += 2;
            }
            else if (text[i] == '*' && Next(text, i) == '/')
            {
                depth--;
                i += 2;
                if (depth == 0)
                {
                    return i;
                }
            }
            else
            {
                i++;
            }
        }

        return i;
    }

    /// <summary>A quoted string or identifier: the quote character doubled is a quote in it, and in an <c>E''</c> string a backslash escapes the next character.</summary>
    private static int SkipQuoted(string text, int i, char quote, bool backslashEscapes)
    {
        i++;
        while (i < text.Length)
        {
            if (backslashEscapes && text[i] == '\\')
            {
                i += 2;
            }
            else if (text[i] == quote)
            {
                if (Next(text, i) == quote)
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }
            else
            {
                i++;
            }
        }

        return i;
    }

    /// <summary>A dollar-quoted body (<c>$$ ... $$</c> or <c>$tag$ ... $tag$</c>), or a single character when the <c>$</c> starts none.</summary>
    private static int SkipDollarQuote(string text, int i)
    {
        if (i > 0 && IsIdentifierPart(text[i - 1]))
        {
            return i + 1;
        }

        var tagEnd = i + 1;
        while (tagEnd < text.Length && (char.IsLetter(text[tagEnd]) || text[tagEnd] == '_' || (tagEnd > i + 1 && char.IsDigit(text[tagEnd]))))
        {
            tagEnd++;
        }

        if (tagEnd >= text.Length || text[tagEnd] != '$')
        {
            return i + 1;
        }

        var tag = text.Substring(i, tagEnd - i + 1);
        var close = text.IndexOf(tag, tagEnd + 1, StringComparison.Ordinal);
        return close < 0 ? text.Length : close + tag.Length;
    }
}
