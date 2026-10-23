using PgProj.Diff;
using PgProj.Engine;
using PgProj.Semantics;

namespace PgProj.Cli.Tests;

/// <summary>The JSON <c>diff --format json</c> writes (the engine's result document, G-309b) for a change set and diagnostics, as the CLI writes it.</summary>
internal static class CompareJson
{
    public static void Write(ChangeSet changeSet, IEnumerable<SemanticDiagnostic> diagnostics, TextWriter writer)
    {
        var result = new CompareResult
        {
            Status = CompareStatus.Succeeded,
            Changes = changeSet,
            AddedCount = changeSet.Added.Count,
            RemovedCount = changeSet.Removed.Count,
            ModifiedCount = changeSet.Modified.Count,
            Diagnostics = [.. diagnostics.Select(EngineDiagnostic.From)],
        };
        writer.WriteLine(result.ToJson(CliContext.DisplayPath));
    }
}
