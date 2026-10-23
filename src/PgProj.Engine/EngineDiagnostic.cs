using PgProj.Semantics;

namespace PgProj.Engine;

/// <summary>How serious a diagnostic is.</summary>
public enum EngineSeverity
{
    /// <summary>Something worth knowing; nothing to fix.</summary>
    Information,
    /// <summary>Something that may be wrong; it does not stop <c>diff</c> or <c>publish</c>.</summary>
    Warning,
    /// <summary>Something that must be fixed; <c>diff</c> and <c>publish</c> stop.</summary>
    Error,
}

/// <summary>
/// One problem the engine found, with its place: the same data in every shell (G-309a). A shell writes it as it likes; the CLI writes
/// <c>file:line:column-endLine:endColumn: [Severity] message</c>. Line and column are 1-based and count characters; the end is just past the
/// last character. A diagnostic about the project as a whole, or from a pass that has no position yet (G-309e), has no place.
/// </summary>
/// <param name="Severity">How serious it is.</param>
/// <param name="Message">What is wrong, in words.</param>
/// <param name="File">The full path of the file it is in, or null.</param>
/// <param name="Line">The 1-based line it starts on, or null.</param>
/// <param name="Column">The 1-based column it starts at, or null.</param>
/// <param name="EndLine">The 1-based line it ends on, or null.</param>
/// <param name="EndColumn">The 1-based column just past its last character, or null.</param>
/// <param name="Source">The part of pgproj that raised it (<c>PgQueryParser</c>, <c>DeployScripts</c>, ...), or null.</param>
public sealed record EngineDiagnostic(
    EngineSeverity Severity,
    string Message,
    string? File = null,
    int? Line = null,
    int? Column = null,
    int? EndLine = null,
    int? EndColumn = null,
    string? Source = null)
{
    /// <summary>The diagnostic the pipeline raised, as the engine reports it.</summary>
    public static EngineDiagnostic From(SemanticDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        return new EngineDiagnostic(
            diagnostic.Severity switch
            {
                SemanticDiagnosticSeverity.Error => EngineSeverity.Error,
                SemanticDiagnosticSeverity.Warning => EngineSeverity.Warning,
                _ => EngineSeverity.Information,
            },
            diagnostic.Message,
            diagnostic.Location?.FilePath,
            diagnostic.Location?.Line,
            diagnostic.Location?.Column,
            diagnostic.Location?.EndLine,
            diagnostic.Location?.EndColumn,
            diagnostic.Source);
    }
}
