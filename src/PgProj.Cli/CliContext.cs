using PgProj.Core;
using PgProj.Engine;
using PgProj.Semantics;

namespace PgProj.Cli;

/// <summary>What the commands share: the diagnostics' text form, paths as they are shown, and <c>pgproj.json</c> read alone. The rest is the engine's (G-320).</summary>
public sealed class CliContext
{
    private readonly PgProjectLoader _projectLoader = new();

    /// <summary>Reads <c>pgproj.json</c> alone, for a setting a command needs before it decides whether to read the project's SQL files.</summary>
    public PgProject LoadProject(string projectFilePath) => _projectLoader.Load(projectFilePath);

    /// <summary>
    /// <see cref="WriteDiagnostics(DatabaseModel, CliLog)"/> for the diagnostics a build result carries (G-309a): written the same way, from the
    /// engine's data, so what is printed is what the result says.
    /// </summary>
    public void WriteDiagnostics(IReadOnlyList<EngineDiagnostic> diagnostics, CliLog log)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(log);

        if (log.IsQuiet)
        {
            foreach (var diagnostic in diagnostics.Where(static d => d.Severity == EngineSeverity.Error))
            {
                log.Error(FormatDiagnostic(diagnostic));
            }

            return;
        }

        // One warning per missing function, not one per object that calls it (G-285).
        foreach (var diagnostic in DiagnosticGrouping.ForOutput(diagnostics))
        {
            log.InfoWriter.WriteLine(FormatDiagnostic(diagnostic));
        }

        if (diagnostics.Count == 0)
        {
            log.InfoWriter.WriteLine("No diagnostics.");
        }

        if (log.IsVerbose)
        {
            var counts = diagnostics.GroupBy(static d => d.Severity).OrderBy(static g => g.Key).Select(static g => $"{g.Count()} {(g.Key == EngineSeverity.Information ? "Info" : g.Key.ToString())}");
            log.Detail($"Diagnostics: {(diagnostics.Count == 0 ? "none" : string.Join(", ", counts))}");
        }
    }

    public void WriteDiagnostics(DatabaseModel model, CliLog log)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(log);

        if (log.IsQuiet)
        {
            foreach (var diagnostic in model.Diagnostics.Where(static d => d.Severity == SemanticDiagnosticSeverity.Error))
            {
                log.Error(FormatDiagnostic(diagnostic));
            }

            return;
        }

        WriteDiagnostics(model, log.InfoWriter);
        if (log.IsVerbose)
        {
            var counts = model.Diagnostics.GroupBy(static d => d.Severity).OrderBy(static g => g.Key).Select(static g => $"{g.Count()} {g.Key}");
            log.Detail($"Diagnostics: {(model.Diagnostics.Count == 0 ? "none" : string.Join(", ", counts))}");
        }
    }

    public void WriteDiagnostics(DatabaseModel model, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(writer);

        // One warning per missing function, not one per object that calls it (G-285).
        foreach (var diagnostic in DiagnosticGrouping.ForOutput(model.Diagnostics))
        {
            writer.WriteLine(FormatDiagnostic(diagnostic));
        }

        if (model.Diagnostics.Count == 0)
        {
            writer.WriteLine("No diagnostics.");
        }
    }

    /// <summary>
    /// One diagnostic as a line of output (G-123, user-chosen shape): compiler style, <c>file:line:column: [Severity] message</c>, so
    /// editors and terminals link to the spot; <c>file:line:</c> or <c>file:</c> when less is known. When the end is known too (G-308,
    /// the user's choice) the place is a range, <c>file:line:column-endLine:endColumn:</c>, whose end is just past the last character.
    /// The file is relative to the current directory when it is under it. A diagnostic from no file keeps the earlier
    /// <c>[Severity] message line N</c>.
    /// </summary>
    public static string FormatDiagnostic(SemanticDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        return FormatDiagnostic(EngineDiagnostic.From(diagnostic));
    }

    /// <summary><see cref="FormatDiagnostic(SemanticDiagnostic)"/> for the engine's diagnostic (G-309a): the CLI writes the result a build returns.</summary>
    public static string FormatDiagnostic(EngineDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        // The engine's severities are spelled as the model's were (Info, Warning, Error), since this is the text users and scripts have.
        var text = $"[{(diagnostic.Severity == EngineSeverity.Information ? "Info" : diagnostic.Severity.ToString())}] {diagnostic.Message}";
        if (diagnostic.File is not { Length: > 0 } filePath)
        {
            return diagnostic.Line is int onlyLine ? $"{text} line {onlyLine}" : text;
        }

        var position = diagnostic.Line is not int line
            ? string.Empty
            : diagnostic.Column is not int column
                ? $":{line}"
                : diagnostic.EndLine is int endLine && diagnostic.EndColumn is int endColumn
                    ? $":{line}:{column}-{endLine}:{endColumn}"
                    : $":{line}:{column}";
        return $"{DisplayPath(filePath)}{position}: {text}";
    }

    /// <summary><paramref name="filePath"/> relative to the current directory when it is under it, otherwise as it is.</summary>
    internal static string DisplayPath(string filePath)
    {
        // A path in a source database's in-memory project is shown as source:/..., as the engine names it (G-309b, G-318).
        if (filePath.StartsWith("source:/", StringComparison.Ordinal))
        {
            return filePath;
        }

        var relative = Path.GetRelativePath(Environment.CurrentDirectory, filePath);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? filePath : relative;
    }
}
