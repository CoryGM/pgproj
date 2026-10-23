using System.Text;
using PgProj.Parser;

namespace PgProj.Core;

public sealed partial class LiveDatabaseModelLoader
{
    /// <summary>How many unreadable objects an error lists before it only counts the rest (G-121).</summary>
    private const int MaxListedUnparseableFiles = 10;

    /// <summary>
    /// The scaffold files (<see cref="BuildScaffoldFiles"/>) whose SQL pgproj's parser cannot read, each with the parser's message and the
    /// line it stopped on (G-121). What PostgreSQL reports for an object comes from the server's own grammar, so a feature newer than the
    /// bundled parser's (a PostgreSQL 18 <c>NOT ENFORCED</c> constraint, a temporal key) fails here. Syntax only: nothing is resolved.
    /// </summary>
    internal IReadOnlyList<UnparseableFile> FindUnparseableFiles(IReadOnlyList<ScaffoldedFile> files)
    {
        var unparseable = new List<UnparseableFile>();
        foreach (var file in files)
        {
            var error = _parser.Parse(file.Content).Diagnostics.FirstOrDefault(static diagnostic => diagnostic.Severity == SqlDiagnosticSeverity.Error);
            if (error is not null)
            {
                unparseable.Add(new UnparseableFile(file.RelativePath, error.Message, LineAt(file.Content, error.CursorPosition)));
            }
        }

        return unparseable;
    }

    /// <summary>
    /// The <c>searchPath</c> a scaffolded project needs (G-251), or empty when <c>public</c> alone does. A function body or a type may name another function or type without a schema, as the
    /// database accepts it because the session that created it had that schema on its <c>search_path</c>; a project built from it with the default path would not build. The files are read
    /// back, and the smallest set of schemas that finds every such name is chosen (the schema that finds the most names first, ties by name), then written ahead of <c>public</c>.
    /// </summary>
    internal IReadOnlyList<string> FindNeededSearchPath(IReadOnlyList<ScaffoldedFile> files)
    {
        var trees = files.Select(file => _parser.Parse(file.Content)).ToArray();
        var chosen = new List<string>();
        for (var round = 0; round < 3; round++)
        {
            var path = chosen.Count == 0 ? null : (IReadOnlyList<string>)[.. chosen, "public"];
            var findings = _semanticBuilder.Build(trees, searchPath: path).SearchPathFindings;
            if (findings.Count == 0)
            {
                return chosen;
            }

            var unmet = findings.ToList();
            while (unmet.Count > 0)
            {
                var best = unmet.SelectMany(static schemas => schemas).GroupBy(static schema => schema, StringComparer.Ordinal)
                    .OrderByDescending(static group => group.Count()).ThenBy(static group => group.Key, StringComparer.Ordinal).First().Key;
                chosen.Add(best);
                unmet.RemoveAll(schemas => schemas.Contains(best, StringComparer.Ordinal));
            }
        }

        return chosen;
    }

    /// <summary>
    /// The error that stops <c>diff</c>/<c>publish</c> (<paramref name="action"/> "read") or <c>init</c> ("scaffolded") when part of the
    /// database cannot be parsed (G-121). Continuing without those objects would compare against, or scaffold, a database that is missing
    /// them: <c>diff</c> would show objects that exist as added, and a statement that failed took every other object with it.
    /// <paramref name="fallbackErrors"/> is the whole snapshot's parse error, used when no single object is to blame.
    /// </summary>
    internal static InvalidOperationException CreateUnparseableException(
        string action,
        IReadOnlyList<UnparseableFile> unparseable,
        IReadOnlyList<SqlDiagnostic> fallbackErrors)
    {
        var message = new StringBuilder()
            .Append("The database could not be ").Append(action)
            .Append(": pgproj cannot parse the SQL PostgreSQL gives for ");

        if (unparseable.Count == 0)
        {
            message.Append("it (").Append(fallbackErrors.FirstOrDefault()?.Message ?? "unknown error").Append("). ");
        }
        else
        {
            message.Append(unparseable.Count == 1 ? "this object" : $"these {unparseable.Count} objects")
                .Append(" (each named by the file pgproj init writes it to):");
            foreach (var file in unparseable.Take(MaxListedUnparseableFiles))
            {
                message.AppendLine().Append("  ").Append(file.RelativePath).Append(": ").Append(file.Message);
                if (file.Line is not null)
                {
                    message.Append(": ").Append(file.Line);
                }
            }

            if (unparseable.Count > MaxListedUnparseableFiles)
            {
                message.AppendLine().Append("  ... and ").Append(unparseable.Count - MaxListedUnparseableFiles).Append(" more");
            }

            message.AppendLine();
        }

        return new InvalidOperationException(message
            .Append("This is a PostgreSQL feature pgproj does not model yet (for example a PostgreSQL 18 NOT ENFORCED constraint), or syntax newer than its parser understands.")
            .Append(" Nothing was compared or written, since leaving the object out would treat the database as if it did not have it.")
            .ToString());
    }

    /// <summary>The trimmed line of <paramref name="sql"/> holding libpg_query's 1-based character position, or null when there is none.</summary>
    private static string? LineAt(string sql, int? cursorPosition)
    {
        if (cursorPosition is not { } position || position < 1 || position > sql.Length)
        {
            return null;
        }

        var start = sql.LastIndexOf('\n', position - 1) + 1;
        var end = sql.IndexOf('\n', position - 1);
        var line = sql[start..(end < 0 ? sql.Length : end)].Trim();
        return line.Length == 0 ? null : line;
    }
}

/// <summary>A scaffold file the parser cannot read (G-121): its path, the parser's message, and the line it stopped on, when known.</summary>
internal sealed record UnparseableFile(string RelativePath, string Message, string? Line);
