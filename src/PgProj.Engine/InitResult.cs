using System.Text.Json.Serialization;
using PgProj.Core;

namespace PgProj.Engine;

/// <summary>How a scaffold ended.</summary>
public enum InitStatus
{
    /// <summary>The database was read and the new project's files are in the result.</summary>
    Succeeded,

    /// <summary>The request cannot be run as it is (a schema the database lacks, no tenant with the prefix, a malformed connection string): <see cref="InitResult.Message"/> says why.</summary>
    Refused,

    /// <summary>The database could not be connected to: <see cref="InitResult.Message"/> is the driver's reason.</summary>
    ConnectionFailed,
}

/// <summary>A line the shell shows after it has written the files, in order: information, or a warning.</summary>
public sealed record InitMessage(EngineEventLevel Level, string Text);

/// <summary>A tenant that differs from the template, with how many objects a publish would add, remove, and change in it.</summary>
public sealed record InitTenantDifference(string Schema, int Added, int Removed, int Modified);

/// <summary>What <c>init</c> found in a database with a schema per tenant (G-290d).</summary>
/// <param name="Prefix">The prefix, folded as a name typed outside SQL is.</param>
/// <param name="TemplateSchema">The tenant's schema the template was scaffolded from.</param>
/// <param name="TenantSchemas">Every schema named <c>prefix_...</c>.</param>
/// <param name="TemplateFileCount">How many files the template has.</param>
/// <param name="Differing">The other tenants that differ from the template.</param>
/// <param name="LeftOut">Files of the tenant's scaffold that belong to no schema (an extension, a cast, ...), left out of the template.</param>
/// <param name="BodiesNamingSchema">Template files whose function bodies still name the tenant's schema.</param>
public sealed record ScaffoldedTenants(
    string Prefix,
    string TemplateSchema,
    IReadOnlyList<string> TenantSchemas,
    int TemplateFileCount,
    IReadOnlyList<InitTenantDifference> Differing,
    IReadOnlyList<string> LeftOut,
    IReadOnlyList<string> BodiesNamingSchema);

/// <summary>
/// The result of <see cref="PgProjEngine.ScaffoldAsync"/> (G-309d): a new project's files as text, and what the run found. The engine writes no file: a shell writes
/// <see cref="Files"/> under <see cref="OutputDirectory"/>, <see cref="ProjectFileText"/> at <see cref="ProjectFilePath"/>, and the tenant file and the report beside them.
/// </summary>
public sealed record InitResult
{
    /// <summary>The version of this document's shape; it changes only when a property is removed or changes meaning.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>The tenant file's name, beside <c>pgproj.json</c>.</summary>
    public const string TenantFileName = "tenants.csv";

    /// <summary>The version of this result's shape, <see cref="CurrentSchemaVersion"/>; a reader checks it before relying on the members.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>How the scaffold ended.</summary>
    public InitStatus Status { get; init; }

    /// <summary>Why the run was refused, or the connection failed; null otherwise.</summary>
    public string? Message { get; init; }

    /// <summary>The full path the new <c>pgproj.json</c> goes to.</summary>
    public string ProjectFilePath { get; init; } = string.Empty;

    /// <summary>The full path of the folder the SQL files go to.</summary>
    public string OutputDirectory { get; init; } = string.Empty;

    /// <summary>The server's version, as it reports it.</summary>
    public string? ServerVersion { get; init; }

    /// <summary>The database that was read.</summary>
    public string? Database { get; init; }

    /// <summary>The SQL files, each relative to <see cref="OutputDirectory"/> with <c>/</c> between folders: the shared schemas', then the tenant template's.</summary>
    public IReadOnlyList<ScaffoldedFile> Files { get; init; } = [];

    /// <summary>The text of the new <c>pgproj.json</c>.</summary>
    public string? ProjectFileText { get; init; }

    /// <summary>The text of <see cref="TenantFileName"/>, when the request asked for it; null otherwise.</summary>
    public string? TenantFileText { get; init; }

    /// <summary>The report's file name, to go in <see cref="OutputDirectory"/>, when the request asked for a report.</summary>
    public string? ReportFileName { get; init; }

    /// <summary>The report's text, when the request asked for one: made after <see cref="ScaffoldRequest.AfterScaffold"/>, so null while it runs.</summary>
    public string? ReportText { get; init; }

    /// <summary>The Mermaid diagram of the new project's tables, when the request asked for one and the project has no errors.</summary>
    public string? DiagramText { get; init; }

    /// <summary>The schemas <c>pgproj.json</c>'s <c>searchPath</c> lists before <c>public</c>, because objects in them use names the database found through its search path (G-251).</summary>
    public IReadOnlyList<string> SearchPath { get; init; } = [];

    /// <summary>The schemas scaffolded (the shared ones, for a database with tenants).</summary>
    public IReadOnlyList<string> ModeledSchemas { get; init; } = [];

    /// <summary>What was found and not scaffolded, or scaffolded with a caveat (an INVALID index, mixed internal trigger states).</summary>
    public IReadOnlyList<UnsupportedObject> UnsupportedObjects { get; init; } = [];

    /// <summary>The kinds of object pgproj does not read at all, so absent from the project even if the database has them.</summary>
    public IReadOnlyList<string> UnreadObjectKinds { get; init; } = [];

    /// <summary>The tenants, for a database with a schema per tenant; null otherwise.</summary>
    public ScaffoldedTenants? Tenants { get; init; }

    /// <summary>What to tell the person who ran it once the files are written, in order: the tenants found, the search path set, and the warnings.</summary>
    public IReadOnlyList<InitMessage> Messages { get; init; } = [];

    /// <summary>The new project as read back, when it was (to compare the tenants, or to draw it). Not serialized.</summary>
    [JsonIgnore]
    public BuildResult? Project { get; init; }
}
