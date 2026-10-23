using PgProj.Engine;
using System.CommandLine;
using System.Globalization;
using PgProj.Semantics;

namespace PgProj.Cli;

/// <summary>The files <c>--erd</c> writes (G-158).</summary>
/// <param name="DiagramPath">The bare Mermaid file (<c>.mmd</c>).</param>
/// <param name="CompanionPath">The Markdown file beside it that links the diagram and shows it, or <see langword="null"/> when the command writes only the diagram.</param>
/// <param name="HtmlPath">The web page beside it that draws the diagram in a browser (<c>--erd-html</c>), or <see langword="null"/>.</param>
/// <param name="Placement">What was done about a file already at the path, and why; <see langword="null"/> until the files have been placed.</param>
public sealed record ErdOutput(string DiagramPath, string? CompanionPath, string? HtmlPath = null, ErdPlacement? Placement = null);

/// <summary>
/// Chooses, writes, and reports the entity relationship diagram files of <c>--erd [path]</c> (G-158). The diagram comes from the project's parsed SQL
/// (<see cref="ErdDiagram"/>), so it is only written for a project without errors.
/// </summary>
public static class ErdFiles
{
    /// <summary>The <c>--erd</c> option every command takes: its value is optional, and a flag with no value means the default file name.</summary>
    public static Option<string?> CreateOption()
        => new("--erd")
        {
            HelpName = "path",
            Description = "Also write an entity relationship diagram of the project's tables as Mermaid, built from the project's SQL files (not from a database): " +
                "a bare .mmd file, and for build, diff, and publish a companion .md file of the same name that links and shows it. The optional value is the file's path, " +
                "relative to the directory of pgproj.json unless absolute; a .mmd or .md extension is replaced, any other is kept and .mmd is added. " +
                "Without a value the file is erd-<yyyy-MM-dd-HH-mm-ss>.mmd (the time in UTC) in the directory of pgproj.json. Nothing is written if the project has errors. " +
                "The diagram comes from the project, not a database, so publish --dry-run writes it too.",
            Arity = ArgumentArity.ZeroOrOne,
        };

    /// <summary>True when <c>--erd</c> was given, with or without a value.</summary>
    public static bool IsRequested(ParseResult parseResult, Option<string?> option)
    {
        ArgumentNullException.ThrowIfNull(parseResult);
        ArgumentNullException.ThrowIfNull(option);
        return parseResult.GetResult(option) is not null;
    }

    /// <summary>
    /// The files for <c>--erd</c>'s <paramref name="value"/> (null or blank for the default name): a path is relative to <paramref name="projectDirectory"/> unless it is absolute.
    /// </summary>
    public static ErdOutput Resolve(string? value, string projectDirectory, DateTime startedUtc, bool companion, bool html = false)
    {
        ArgumentNullException.ThrowIfNull(projectDirectory);

        var path = string.IsNullOrWhiteSpace(value)
            ? Path.Combine(projectDirectory, $"erd-{startedUtc.ToUniversalTime().ToString("yyyy-MM-dd-HH-mm-ss", CultureInfo.InvariantCulture)}")
            : Path.GetFullPath(value.Trim(), projectDirectory);
        var extension = Path.GetExtension(path);
        var withoutExtension = extension.Equals(".mmd", StringComparison.OrdinalIgnoreCase) || extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
            ? path[..^extension.Length]
            : path;
        return new ErdOutput(withoutExtension + ".mmd", companion ? withoutExtension + ".md" : null, html ? withoutExtension + ".html" : null);
    }

    /// <summary>
    /// Writes the diagram of <paramref name="model"/> to <paramref name="output"/>, and says so through <paramref name="log"/>. A project with errors gets no diagram, only a
    /// warning that says why. Returns the files written, or <see langword="null"/> when none was. The new files are written to temporary names first and moved into place only
    /// when all of them were written, so a failure never leaves a new diagram beside an old page (or a half-written file at the path).
    /// </summary>
    public static async Task<ErdOutput?> WriteAsync(DatabaseModel model, string projectName, ErdOutput output, DateTime generatedUtc, CliLog log, ErdFilter filter, ErdExisting existing, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(log);

        if (model.HasErrors)
        {
            log.Warning("No entity relationship diagram was written: the project has errors (see the diagnostics).");
            return null;
        }

        CheckSchemas(filter, model.Tables.Values.Select(static table => table.SchemaName ?? string.Empty).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        var diagram = ErdDiagram.Build(model, filter);
        var diagramText = WithMarker(diagram, generatedUtc);
        var plan = Plan(output, existing, generatedUtc, log);
        output = plan.Output;

        var files = new List<(string Path, string Text)> { (output.DiagramPath, diagramText) };
        if (output.CompanionPath is not null)
        {
            files.Add((output.CompanionPath, Companion(projectName, Path.GetFileName(output.DiagramPath), diagram, generatedUtc)));
        }

        if (output.HtmlPath is not null)
        {
            files.Add((output.HtmlPath, HtmlPage(projectName, Path.GetFileName(output.DiagramPath), diagramText, generatedUtc)));
        }

        await FilePlacement.CommitAsync(files, plan.Decision, log, Wording.Noun, cancellationToken);

        log.Info($"Wrote ERD: {output.DiagramPath}");
        if (output.CompanionPath is not null)
        {
            log.Info($"Wrote ERD page: {output.CompanionPath}");
        }

        if (output.HtmlPath is not null)
        {
            log.Info($"Wrote ERD web page: {output.HtmlPath}");
        }

        if (diagramText.Length > ViewerLimit)
        {
            log.Warning($"The diagram is {diagramText.Length.ToString("N0", CultureInfo.InvariantCulture)} characters; some viewers (GitHub's Mermaid rendering stops at about {ViewerLimit.ToString("N0", CultureInfo.InvariantCulture)}) may not render it. " +
                "It was written anyway; --erd-schemas or --erd-exclude-schemas draw fewer tables.");
        }

        return output;
    }

    /// <summary>The size above which some Mermaid viewers refuse to render a diagram (Mermaid's default <c>maxTextSize</c>, which GitHub keeps).</summary>
    internal const int ViewerLimit = 50_000;

    /// <summary>The text every file <c>--erd</c> writes carries near its top, which is how a later run tells a file it wrote from one a person wrote.</summary>
    internal const string Marker = "Generated by pgproj --erd";

    /// <summary>Whether <paramref name="path"/> is a file an earlier <c>--erd</c> wrote: it carries <see cref="Marker"/> within its first few lines.</summary>
    internal static bool IsGenerated(string path)
        => FilePlacement.IsGenerated(path);

    /// <summary>The diagram with a Mermaid comment line after <c>erDiagram</c> that marks it as generated (Mermaid ignores <c>%%</c> lines).</summary>
    internal static string WithMarker(string diagram, DateTime generatedUtc)
    {
        var comment = $"    %% {Marker} on {generatedUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} UTC{Environment.NewLine}";
        var firstLineEnd = diagram.IndexOf(Environment.NewLine, StringComparison.Ordinal);
        return firstLineEnd < 0 ? diagram : diagram.Insert(firstLineEnd + Environment.NewLine.Length, comment);
    }

    /// <summary>What <see cref="Plan"/> decided: where to write, and the existing files to rename first.</summary>
    internal sealed record ErdPlan(ErdOutput Output, FilePlacement.Decision Decision);

    private static readonly FilePlacement.Wording Wording = new("--erd", "--erd-existing", "ERD file", "The diagram");

    /// <summary>
    /// <see cref="Plan"/> and then <see cref="Apply"/>: makes room for the files at <paramref name="output"/> and returns where to write.
    /// </summary>
    internal static ErdOutput MakeRoom(ErdOutput output, ErdExisting existing, DateTime generatedUtc, CliLog log)
    {
        var plan = Plan(output, existing, generatedUtc, log);
        Apply(plan, log);
        return plan.Output;
    }

    /// <summary>Renames the existing files a plan says to move, and says so.</summary>
    internal static void Apply(ErdPlan plan, CliLog log) => FilePlacement.Apply(plan.Decision, log, Wording.Noun);

    /// <summary>
    /// Decides what <paramref name="existing"/> means for the files that already exist at <paramref name="output"/>, without touching them: the same paths after an overwrite or a backup
    /// (a backup renames what is there to <c>name.&lt;UTC time&gt;.ext</c> first), or new time-stamped paths when the existing files are kept. The diagram, its page, and its web page are
    /// treated as one set (<see cref="FilePlacement"/>). Left to the default, files that are generated are backed up and any file a person wrote is kept.
    /// </summary>
    internal static ErdPlan Plan(ErdOutput output, ErdExisting existing, DateTime generatedUtc, CliLog log)
    {
        var paths = new[] { output.DiagramPath, output.CompanionPath, output.HtmlPath }.OfType<string>().ToArray();
        var decision = FilePlacement.Decide(paths, existing, generatedUtc, log, Wording);
        var next = 0;
        var placed = new ErdOutput(
            decision.Paths[next++],
            output.CompanionPath is null ? null : decision.Paths[next++],
            output.HtmlPath is null ? null : decision.Paths[next++],
            decision.Placement);
        return new ErdPlan(placed, decision);
    }

    /// <summary>A schema named by <c>--erd-schemas</c> or <c>--erd-exclude-schemas</c> that is not among <paramref name="schemas"/> (the project's) is an error that names it.</summary>
    public static void CheckSchemas(ErdFilter filter, IReadOnlyCollection<string> schemas)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(schemas);

        var missing = filter.Names.Where(name => !schemas.Contains(name, PgProj.Parser.SqlIdentifiers.NameComparer)).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException($"{filter.OptionName} names {(missing.Length == 1 ? "a schema" : "schemas")} the project has no table in: {string.Join(", ", missing)}.");
        }
    }

    /// <summary>The CDN script the web page of <c>--erd-html</c> loads Mermaid from (a pinned major version); the engine's, which <c>document</c>'s page loads too (G-309d).</summary>
    internal const string MermaidScript = ErdDiagram.MermaidScript;

    /// <summary>
    /// The web page of <c>--erd-html</c>: the diagram in a <c>pre.mermaid</c> block and a module script that loads Mermaid from <see cref="MermaidScript"/> and draws it, so opening the file in a
    /// browser shows the diagram (it needs internet access to reach the CDN). The page lifts Mermaid's 50,000 character limit, which only exists to protect pages that show text someone else wrote.
    /// </summary>
    internal static string HtmlPage(string projectName, string diagramFileName, string diagramText, DateTime generatedUtc)
    {
        var title = System.Net.WebUtility.HtmlEncode($"Entity relationship diagram: {projectName}");
        return $$"""
            <!doctype html>
            <!-- {{Marker}} -->
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{title}}</title>
            <style>
            body { font-family: system-ui, sans-serif; margin: 1.5rem; }
            .mermaid { overflow: auto; }
            </style>
            </head>
            <body>
            <h1>{{title}}</h1>
            <p>Diagram source: <a href="{{System.Net.WebUtility.HtmlEncode(Uri.EscapeDataString(diagramFileName))}}">{{System.Net.WebUtility.HtmlEncode(diagramFileName)}}</a>. Generated {{generatedUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}} UTC from the project's SQL files by <code>pgproj</code>. The page loads Mermaid from a CDN, so it needs internet access to draw the diagram.</p>
            <pre class="mermaid">
            {{System.Net.WebUtility.HtmlEncode(diagramText.TrimEnd())}}
            </pre>
            <noscript><p>Turn on JavaScript to see the diagram, or open the source file above in a Mermaid viewer.</p></noscript>
            <script type="module">
            import mermaid from "{{MermaidScript}}";
            mermaid.initialize({ startOnLoad: true, maxTextSize: 10000000 });
            </script>
            </body>
            </html>

            """;
    }

    /// <summary>The companion Markdown page: a title, a link to the <c>.mmd</c> file beside it, and the diagram itself in a <c>mermaid</c> block so a viewer that renders Mermaid shows it.</summary>
    internal static string Companion(string projectName, string diagramFileName, string diagram, DateTime generatedUtc)
        => $"""
            <!-- {Marker} -->
            # Entity relationship diagram: {projectName}

            Diagram source: [{diagramFileName}]({Uri.EscapeDataString(diagramFileName)}). Generated {generatedUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} UTC from the project's SQL files by `pgproj`.

            ```mermaid
            {diagram.TrimEnd()}
            ```

            """;
}
