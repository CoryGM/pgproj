using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PgProj.Core;
using PgProj.Semantics;

namespace PgProj.Engine;

/// <summary>How a build ended.</summary>
public enum BuildStatus
{
    /// <summary>The project was read and its model has no errors (it may have warnings).</summary>
    Succeeded,

    /// <summary>The project was read and its model has errors: <see cref="BuildResult.Diagnostics"/> says what.</summary>
    Failed,

    /// <summary>There is no project file at <see cref="BuildResult.ProjectFilePath"/>.</summary>
    ProjectNotFound,

    /// <summary>The project file or settings are not valid, or a file they name is missing: <see cref="BuildResult.Message"/> says why.</summary>
    ProjectInvalid,

    /// <summary>
    /// A request the engine will not run (G-318): with <see cref="BuildRequest.SourceConnectionString"/>, a malformed connection string or a project with tenants.
    /// <see cref="BuildResult.Message"/> says why.
    /// </summary>
    Refused,

    /// <summary>The source database (<see cref="BuildRequest.SourceConnectionString"/>) could not be reached (G-318): <see cref="BuildResult.Message"/> is the driver's error.</summary>
    ConnectionFailed,
}

/// <summary>
/// The result of a build (G-309a): everything a shell needs to show it, as data. A problem the user caused is a <see cref="Status"/>, never an
/// exception. Serializes with <c>System.Text.Json</c> (camelCase), except the model, which is large and not a promised shape.
/// </summary>
public sealed record BuildResult
{
    /// <summary>The version of this document's shape; it changes only when a property is removed or changes meaning.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>The version of this result's shape, <see cref="CurrentSchemaVersion"/>; a reader checks it before relying on the members.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>How the build ended: <see cref="BuildStatus.Succeeded"/> when the model has no errors.</summary>
    public BuildStatus Status { get; init; }

    /// <summary>Why the project is not valid, or that the project file was not found; null otherwise.</summary>
    public string? Message { get; init; }

    /// <summary>The full path of the project file that was looked for, when a project file was named.</summary>
    public string? ProjectFilePath { get; init; }

    /// <summary>The project's <c>name</c> from <c>pgproj.json</c>, when it was read.</summary>
    public string? ProjectName { get; init; }

    /// <summary>The full path of the folder the schema files are read from.</summary>
    public string? SourcePath { get; init; }

    /// <summary>The schema files read, with their versions.</summary>
    public IReadOnlyList<DocumentInfo> Documents { get; init; } = [];

    /// <summary>The tenant template's files read (G-290), with their versions; empty for a project without tenants.</summary>
    public IReadOnlyList<DocumentInfo> TemplateDocuments { get; init; } = [];

    /// <summary>Every diagnostic, in the order the pipeline raised them. A shell that writes them groups them with <see cref="DiagnosticGrouping"/>.</summary>
    public IReadOnlyList<EngineDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>How many of <see cref="Diagnostics"/> are errors.</summary>
    public int ErrorCount { get; init; }

    /// <summary>How many of <see cref="Diagnostics"/> are warnings.</summary>
    public int WarningCount { get; init; }

    /// <summary>How many of <see cref="Diagnostics"/> are information.</summary>
    public int InformationCount { get; init; }

    /// <summary>The lines a header adds for a project with tenants (G-290): the template, and where the tenants come from.</summary>
    public IReadOnlyList<string> TenantLines { get; init; } = [];

    /// <summary>The database that was built in place of the project's files (<see cref="BuildRequest.SourceConnectionString"/>, G-318); null for a project's files.</summary>
    public DatabaseEnd? Source { get; init; }

    /// <summary>How long reading, parsing, and building took.</summary>
    public long ElapsedMilliseconds { get; init; }

    /// <summary>The semantic model, in memory; null unless the project was read. Not serialized.</summary>
    [JsonIgnore]
    public DatabaseModel? Model { get; init; }

    /// <summary>The loaded project; null unless the project was read. Not serialized.</summary>
    [JsonIgnore]
    internal PgProject? Project { get; init; }

    /// <summary>The files read; null unless the project was read. Not serialized.</summary>
    [JsonIgnore]
    internal Workspace? Workspace { get; init; }

    /// <summary>
    /// The result as the engine's JSON document (camelCase, indented), the document <c>pgproj build --format json</c> writes (G-309f): the envelope
    /// <see cref="CompareResult.ToJson"/> starts with, with only its build part, then <c>source</c> when a database was built (G-318). <paramref name="displayPath"/>
    /// rewrites each file path in it; the CLI makes them relative to the current directory when they are under it.
    /// </summary>
    public string ToJson(Func<string, string>? displayPath = null)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            EnvelopeJson.WriteHead(
                json,
                SchemaVersion,
                Status.ToString(),
                Message,
                ProjectName,
                ProjectFilePath,
                SourcePath,
                Documents,
                TemplateDocuments,
                Diagnostics,
                (ErrorCount, WarningCount, InformationCount),
                displayPath ?? (static path => path));
            if (Source is not null)
            {
                EnvelopeJson.WriteEnd(json, "source", Source);
            }

            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
