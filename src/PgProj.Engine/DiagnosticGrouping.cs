using System.Text.RegularExpressions;
using PgProj.Semantics;

namespace PgProj.Engine;

/// <summary>
/// The diagnostics as they are written (G-285): a warning that several objects reference one missing function (a function of a declared extension, say) is written
/// once, with how many objects reference it and the first few, at the place of the first; the model keeps one per object. An error is never merged, since each
/// one names something to fix.
/// </summary>
public static partial class DiagnosticGrouping
{
    /// <summary>How many of the objects that reference the function a merged warning names.</summary>
    internal const int NamedReferences = 3;

    /// <summary>The diagnostics as they are written: warnings that several objects reference one missing function are merged into one; errors are kept as they are.</summary>
    public static IReadOnlyList<SemanticDiagnostic> ForOutput(IReadOnlyList<SemanticDiagnostic> diagnostics)
        => Group(
            diagnostics,
            static diagnostic => diagnostic.Severity == SemanticDiagnosticSeverity.Warning,
            static diagnostic => diagnostic.Message,
            static (diagnostic, message) => diagnostic with { Message = message });

    /// <summary><see cref="ForOutput(IReadOnlyList{SemanticDiagnostic})"/> for the engine's diagnostics (G-309a).</summary>
    public static IReadOnlyList<EngineDiagnostic> ForOutput(IReadOnlyList<EngineDiagnostic> diagnostics)
        => Group(
            diagnostics,
            static diagnostic => diagnostic.Severity == EngineSeverity.Warning,
            static diagnostic => diagnostic.Message,
            static (diagnostic, message) => diagnostic with { Message = message });

    private static IReadOnlyList<T> Group<T>(IReadOnlyList<T> diagnostics, Func<T, bool> isWarning, Func<T, string> messageOf, Func<T, string, T> withMessage)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        var groups = diagnostics
            .Select(diagnostic => (Diagnostic: diagnostic, Match: isWarning(diagnostic) ? MissingFunction().Match(messageOf(diagnostic)) : Match.Empty))
            .Where(static item => item.Match.Success)
            .GroupBy(static item => item.Match.Groups["function"].Value, StringComparer.Ordinal)
            .Select(static group => (Function: group.Key, Owners: group.Select(static item => item.Match.Groups["owner"].Value).Distinct(StringComparer.Ordinal).ToArray()))
            .Where(static group => group.Owners.Length > 1)
            .ToDictionary(static group => group.Function, static group => group.Owners, StringComparer.Ordinal);
        if (groups.Count == 0)
        {
            return diagnostics;
        }

        var written = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<T>(diagnostics.Count);
        foreach (var diagnostic in diagnostics)
        {
            var match = isWarning(diagnostic) ? MissingFunction().Match(messageOf(diagnostic)) : Match.Empty;
            if (!match.Success || !groups.TryGetValue(match.Groups["function"].Value, out var owners))
            {
                result.Add(diagnostic);
                continue;
            }

            if (!written.Add(match.Groups["function"].Value))
            {
                continue;
            }

            // Two passes can warn about one object, so the objects are counted, not the warnings.
            var rest = owners.Length > NamedReferences ? $", and {owners.Length - NamedReferences} more" : string.Empty;
            result.Add(withMessage(
                diagnostic,
                $"{owners.Length} objects reference missing function '{match.Groups["function"].Value}': {string.Join(", ", owners.Take(NamedReferences))}{rest}."));
        }

        return result;
    }

    [GeneratedRegex(@"^(?<owner>.+ '.+') references missing function '(?<function>.+)'\.$", RegexOptions.Singleline)]
    private static partial Regex MissingFunction();
}
