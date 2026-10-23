using PgProj.Parser;

namespace PgProj.Semantics;

public enum SemanticDiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

public sealed record SemanticLocation(string? FilePath, int? Line)
{
    /// <summary>The 1-based column on <see cref="Line"/>, in characters, when known (G-123).</summary>
    public int? Column { get; init; }

    /// <summary>
    /// The 1-based line where the diagnostic ends, and <see cref="EndColumn"/> the 1-based column just past its last character (an exclusive
    /// end, as an editor's range has it), when known (G-308). Set together, and only when <see cref="Line"/> and <see cref="Column"/> are.
    /// </summary>
    public int? EndLine { get; init; }

    /// <inheritdoc cref="EndLine"/>
    public int? EndColumn { get; init; }

    /// <summary>
    /// The place of <paramref name="range"/> in the file <paramref name="filePath"/> (G-309e); null when either is unknown, as for a model read from a
    /// database, whose text is no file a person wrote.
    /// </summary>
    public static SemanticLocation? Of(string? filePath, SourceRange? range)
        => filePath is null || range is null
            ? null
            : new SemanticLocation(filePath, range.Line) { Column = range.Column, EndLine = range.EndLine, EndColumn = range.EndColumn };

    /// <summary>The place of a parser diagnostic in the file <paramref name="filePath"/>, its end included when it has one (G-308).</summary>
    public static SemanticLocation FromSql(string? filePath, SqlDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        return new SemanticLocation(filePath, diagnostic.LineNumber)
        {
            Column = diagnostic.Column,
            EndLine = diagnostic.EndLine,
            EndColumn = diagnostic.EndColumn,
        };
    }
}

public sealed record SemanticDiagnostic(
    SemanticDiagnosticSeverity Severity,
    string Message,
    SemanticLocation? Location = null,
    string? Source = null);

public static class SemanticDiagnostics
{
    public static SemanticDiagnostic Info(string message, SemanticLocation? location = null, string? source = null)
        => new(SemanticDiagnosticSeverity.Info, message, location, source);

    public static SemanticDiagnostic Warning(string message, SemanticLocation? location = null, string? source = null)
        => new(SemanticDiagnosticSeverity.Warning, message, location, source);

    public static SemanticDiagnostic Error(string message, SemanticLocation? location = null, string? source = null)
        => new(SemanticDiagnosticSeverity.Error, message, location, source);

    /// <summary>
    /// Gives the diagnostics added to <paramref name="diagnostics"/> from <paramref name="start"/> on that have no place the place
    /// <paramref name="location"/> (G-309e): for a pass whose helpers report about an object they were given only the name of.
    /// </summary>
    public static void PlaceFrom(IList<SemanticDiagnostic> diagnostics, int start, SemanticLocation? location)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (location is null)
        {
            return;
        }

        for (var index = start; index < diagnostics.Count; index++)
        {
            if (diagnostics[index].Location is null)
            {
                diagnostics[index] = diagnostics[index] with { Location = location };
            }
        }
    }
}
