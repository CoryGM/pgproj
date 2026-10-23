using System.Text.Json.Serialization;

namespace PgProj.Engine;

/// <summary>How a documentation run ended.</summary>
public enum DocumentStatus
{
    /// <summary>The documentation was rendered. It may still list problems with the tags in the comments (<see cref="DocumentResult.Problems"/>).</summary>
    Succeeded,

    /// <summary>The project has errors (its <see cref="DocumentResult.Build"/> says what), so nothing was documented.</summary>
    Failed,

    /// <summary>There is no project file at <see cref="DocumentResult.ProjectFilePath"/>.</summary>
    ProjectNotFound,

    /// <summary>The project file or settings are not valid: <see cref="DocumentResult.Message"/> says why.</summary>
    ProjectInvalid,

    /// <summary>The request cannot be run as it is (a schema filter that names nothing documented, a malformed connection string): <see cref="DocumentResult.Message"/> says why.</summary>
    Refused,

    /// <summary>The database could not be connected to: <see cref="DocumentResult.Message"/> is the driver's reason.</summary>
    ConnectionFailed,
}

/// <summary>One rendered file: its name (the shell chooses the folder) and its text.</summary>
public sealed record DocumentFile(DocFormat Format, string FileName, string Text);

/// <summary>
/// The result of <see cref="PgProjEngine.DocumentAsync"/> (G-309d): the documentation rendered in each format asked for, the problems found in the comments' tags, and
/// what was counted. The engine writes no file: a shell writes <see cref="Files"/> where it chooses.
/// </summary>
public sealed record DocumentResult
{
    /// <summary>The version of this document's shape; it changes only when a property is removed or changes meaning.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>The version of this result's shape, <see cref="CurrentSchemaVersion"/>; a reader checks it before relying on the members.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>How the run ended.</summary>
    public DocumentStatus Status { get; init; }

    /// <summary>Why the run was refused, the project is not valid, or the connection failed; null otherwise.</summary>
    public string? Message { get; init; }

    /// <summary>The full path of the project file that was looked for, when a project was documented.</summary>
    public string? ProjectFilePath { get; init; }

    /// <summary>The project's build, when a project was documented and read. Not serialized: its diagnostics are what a shell shows.</summary>
    [JsonIgnore]
    public BuildResult? Build { get; init; }

    /// <summary>The time the documents say they were generated.</summary>
    public DateTime GeneratedUtc { get; init; }

    /// <summary>The rendered files, one per format, in the order the request listed the formats.</summary>
    public IReadOnlyList<DocumentFile> Files { get; init; } = [];

    /// <summary>The problems with the tags in the comments: errors (a value that cannot be read) and warnings (a tag that does not apply to its object).</summary>
    public IReadOnlyList<DocProblem> Problems { get; init; } = [];

    /// <summary>What was documented, and how many problems of each severity; null unless the documentation was rendered.</summary>
    public DocCounts? Counts { get; init; }
}
