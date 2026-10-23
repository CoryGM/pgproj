using PgProj.Engine;
using EngineFiles = PgProj.Engine.Documentation.DocumentationFiles;

namespace PgProj.Cli.Documentation;

/// <summary>Chooses and writes the files of <c>pgproj document</c> (G-159); the engine renders them (G-309d).</summary>
public static class DocumentationFiles
{
    /// <summary>The text every file <c>document</c> writes carries near its top, which is how a later run tells a file it wrote from one a person wrote.</summary>
    public const string Marker = EngineFiles.Marker;

    private static readonly FilePlacement.Wording Wording = new("document", "--existing", "documentation file", "The documentation");

    /// <summary>Reads a <c>--format</c> value: <c>html</c>, <c>markdown</c> (or <c>md</c>), and <c>json</c>, without regard to case, or null for one that is none of them.</summary>
    public static DocFormat? ParseFormat(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "html" => DocFormat.Html,
            "markdown" or "md" => DocFormat.Markdown,
            "json" => DocFormat.Json,
            _ => null,
        };

    /// <summary>
    /// Writes each of the engine's files to <paramref name="directory"/>, applying <paramref name="existing"/> to files already there, and says so. Returns the files
    /// written.
    /// </summary>
    public static async Task<IReadOnlyList<string>> WriteAsync(string directory, IReadOnlyList<DocumentFile> files, DateTime generatedUtc, ErdExisting existing, CliLog log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(log);

        var decision = FilePlacement.Decide(files.Select(file => Path.Combine(directory, file.FileName)).ToArray(), existing, generatedUtc, log, Wording);
        var contents = files.Select((file, index) => (decision.Paths[index], file.Text)).ToArray();
        await FilePlacement.CommitAsync(contents, decision, log, Wording.Noun, cancellationToken);
        foreach (var (path, _) in contents)
        {
            log.Info($"Wrote documentation: {path}");
        }

        return contents.Select(static content => content.Item1).ToArray();
    }
}
