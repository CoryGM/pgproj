using PgProj.Core;

namespace PgProj.Engine;

/// <summary>The formats <c>pgproj document</c> writes.</summary>
public enum DocFormat
{
    /// <summary>One HTML page, <c>database-documentation.html</c>, with the dictionary and the diagram.</summary>
    Html,
    /// <summary>Markdown, <c>database-documentation.md</c>, with the diagram in a <c>mermaid</c> block.</summary>
    Markdown,
    /// <summary>JSON, <c>database-documentation.json</c>, the canonical form.</summary>
    Json,
}

/// <summary>
/// What <see cref="PgProjEngine.DocumentAsync"/> documents (G-309d): a project, named as for <see cref="BuildRequest"/> (by its file or by its settings), or a live
/// database (<see cref="SourceConnectionString"/>), whose objects and comments are read in place of the project's files.
/// </summary>
public sealed record DocumentRequest
{
    /// <summary>Where the project's files are read from. The disk by default.</summary>
    public IProjectFileSource Files { get; init; } = DiskFileSource.Instance;

    /// <summary>The path of the project file in <see cref="Files"/>. Not read when <see cref="SourceConnectionString"/> is given.</summary>
    public string? ProjectFilePath { get; init; }

    /// <summary>The project's settings, for a caller with no <c>pgproj.json</c>. Needs <see cref="RootDirectory"/>.</summary>
    public ProjectSettings? Settings { get; init; }

    /// <summary>The directory relative paths in <see cref="Settings"/> are resolved against.</summary>
    public string? RootDirectory { get; init; }

    /// <summary>A live database to document in place of a project, as the shell resolved its connection string; null documents the project.</summary>
    public string? SourceConnectionString { get; init; }

    /// <summary>The formats to render, in the order the result lists them (a repeat is rendered once). HTML and JSON by default.</summary>
    public IReadOnlyList<DocFormat> Formats { get; init; } = [DocFormat.Html, DocFormat.Json];

    /// <summary>
    /// The schemas to document (and draw), or to leave out; <see cref="ErdFilter.None"/> documents them all. A schema it names that has no documented object is
    /// <see cref="DocumentStatus.Refused"/>.
    /// </summary>
    public ErdFilter Filter { get; init; } = ErdFilter.None;

    /// <summary>The time the documents say they were generated; now when null.</summary>
    public DateTime? GeneratedUtc { get; init; }

    /// <summary>Told about the run as it goes (the database connected to and read, the documentation built). Called on the thread that runs it, in order.</summary>
    public IProgress<EngineEvent>? Progress { get; init; }

    /// <summary>
    /// Called once the project is read and built, before anything is documented: with the build result, whatever it holds (errors too). Not called for a database.
    /// A shell uses it to show what was read and the diagnostics, as the CLI does.
    /// </summary>
    public Func<BuildResult, CancellationToken, Task>? AfterBuild { get; init; }
}
