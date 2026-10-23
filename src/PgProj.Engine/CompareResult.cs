using System.Text;
using System.Text.Json;
using PgProj.Diff;

namespace PgProj.Engine;

/// <summary>How a comparison ended.</summary>
public enum CompareStatus
{
    /// <summary>The project was compared with the database; <see cref="CompareResult.Changes"/> and the rest are filled.</summary>
    Succeeded,

    /// <summary>The project was read and its model has errors, so nothing was compared: <see cref="CompareResult.Diagnostics"/> says what.</summary>
    Failed,

    /// <summary>There is no project file at <see cref="CompareResult.ProjectFilePath"/>.</summary>
    ProjectNotFound,

    /// <summary>The project file or settings are not valid: <see cref="CompareResult.Message"/> says why.</summary>
    ProjectInvalid,

    /// <summary>The request cannot be run as it stands (no target connection string, a bad one, the source being the target, <c>--tenants</c> on a project without tenants, a tenant query that returns bad rows): <see cref="CompareResult.Message"/> says why.</summary>
    Refused,

    /// <summary>A database could not be connected to: <see cref="CompareResult.Message"/> is what the driver said.</summary>
    ConnectionFailed,
}


/// <summary>
/// The result of a comparison (G-309b): the build part (as <see cref="BuildResult"/> has it), then what the project differs from the database in. A problem the
/// user caused is a <see cref="Status"/>, never an exception. <see cref="ToJson"/> is its serialization, the document <c>pgproj diff --format json</c> writes.
/// </summary>
public sealed record CompareResult
{
    /// <summary>The version of the JSON document's shape; it changes only when a property is removed or changes meaning.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>The version of this result's shape, <see cref="CurrentSchemaVersion"/>; a reader checks it before relying on the members.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>How the comparison ended: <see cref="CompareStatus.Succeeded"/> when it compared, whatever it found.</summary>
    public CompareStatus Status { get; init; }

    /// <summary>Why the run did not complete (not valid, refused, could not connect); null otherwise.</summary>
    public string? Message { get; init; }

    // The build part.

    /// <summary>The full path of the project file that was looked for, when one was named.</summary>
    public string? ProjectFilePath { get; init; }

    /// <summary>The project's <c>name</c> from <c>pgproj.json</c>, when it was read.</summary>
    public string? ProjectName { get; init; }

    /// <summary>The folder the schema files were read from.</summary>
    public string? SourcePath { get; init; }

    /// <summary>The schema files read, with their versions.</summary>
    public IReadOnlyList<DocumentInfo> Documents { get; init; } = [];

    /// <summary>The tenant template's files read, with their versions; empty for a project without tenants.</summary>
    public IReadOnlyList<DocumentInfo> TemplateDocuments { get; init; } = [];

    /// <summary>The diagnostics of the model that was compared, in the order the pipeline raised them.</summary>
    public IReadOnlyList<EngineDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>How many of <see cref="Diagnostics"/> are errors.</summary>
    public int ErrorCount { get; init; }

    /// <summary>How many of <see cref="Diagnostics"/> are warnings.</summary>
    public int WarningCount { get; init; }

    /// <summary>How many of <see cref="Diagnostics"/> are information.</summary>
    public int InformationCount { get; init; }

    // The compare part.

    /// <summary>In source mode (G-304b), the database that is the desired state; null otherwise.</summary>
    public DatabaseEnd? Source { get; init; }

    /// <summary>In source mode, the database that is changed to match it; null otherwise.</summary>
    public DatabaseEnd? Target { get; init; }

    /// <summary>What differs: for a project with tenants, in the shared schemas (the tenants' are in <see cref="Tenants"/>).</summary>
    public ChangeSet Changes { get; init; } = new() { Added = [], Removed = [], Modified = [] };

    /// <summary>The added, removed, and modified objects, counting every tenant.</summary>
    public int AddedCount { get; init; }

    /// <inheritdoc cref="AddedCount"/>
    public int RemovedCount { get; init; }

    /// <inheritdoc cref="AddedCount"/>
    public int ModifiedCount { get; init; }

    /// <summary>What <c>--exit-code</c> counts as differences: the added, removed, and modified objects (G-282).</summary>
    public int DifferenceCount => AddedCount + RemovedCount + ModifiedCount;

    /// <summary>Things a change drops, renames, or retypes that a PL/pgSQL body names (G-303b).</summary>
    public IReadOnlyList<PlpgsqlBodyNote> BodyNotes { get; init; } = [];

    /// <summary>The schemas a publish would leave behind, since pgproj never drops a schema (G-166).</summary>
    public IReadOnlyList<string> UndroppedSchemas { get; init; } = [];

    /// <summary>The tenants' side of a project with tenants; null otherwise.</summary>
    public TenantsComparison? Tenants { get; init; }

    /// <summary>
    /// The result as the JSON document <c>diff --format json</c> writes (camelCase, indented). <paramref name="displayPath"/> rewrites each file path in it; the
    /// CLI makes them relative to the current directory when they are under it.
    /// </summary>
    public string ToJson(Func<string, string>? displayPath = null) => CompareResultJson.Write(this, displayPath ?? (static path => path));
}

internal static class CompareResultJson
{
    public static string Write(CompareResult result, Func<string, string> displayPath)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            EnvelopeJson.WriteHead(
                json,
                result.SchemaVersion,
                result.Status.ToString(),
                result.Message,
                result.ProjectName,
                result.ProjectFilePath,
                result.SourcePath,
                result.Documents,
                result.TemplateDocuments,
                result.Diagnostics,
                (result.ErrorCount, result.WarningCount, result.InformationCount),
                displayPath);

            // A run that did not compare (the build has errors, the request was refused, a database could not be reached) has only its status, message, and build part (G-316).
            if (result.Status == CompareStatus.Succeeded)
            {
                WriteComparison(json, result);
            }

            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteComparison(Utf8JsonWriter json, CompareResult result)
    {
        if (result.Source is not null && result.Target is not null)
        {
            EnvelopeJson.WriteEnd(json, "source", result.Source);
            EnvelopeJson.WriteEnd(json, "target", result.Target);
        }

        WriteChangeArrays(json, result.Changes);
        if (result.Tenants is { } tenants)
        {
            WriteTenants(json, tenants);
        }

        json.WriteStartArray("bodyNotes");
        foreach (var note in result.BodyNotes)
        {
            json.WriteStartObject();
            json.WriteString("routineKind", note.RoutineKind);
            json.WriteString("routine", note.RoutineKey);
            json.WriteString("what", note.What);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        WriteNames(json, "undroppedSchemas", result.UndroppedSchemas);
        json.WriteStartObject("summary");
        json.WriteNumber("added", result.AddedCount);
        json.WriteNumber("removed", result.RemovedCount);
        json.WriteNumber("modified", result.ModifiedCount);
        json.WriteEndObject();
    }

    private static void WriteTenants(Utf8JsonWriter json, TenantsComparison tenants)
    {
        json.WriteString("standInSchema", tenants.StandInSchema);
        json.WriteStartArray("tenantGroups");
        foreach (var group in tenants.Groups)
        {
            json.WriteStartObject();
            WriteNames(json, "tenants", group.Members.Select(static member => member.Tenant.Schema));
            WriteChangeArrays(json, group.Members[0].Changes, TenantsComparison.StandInDisplay(group.Members[0].Tenant.Schema, tenants.StandInSchema));
            json.WriteEndObject();
        }

        json.WriteEndArray();

        json.WriteStartArray("newTenants");
        foreach (var tenant in tenants.NewTenants)
        {
            json.WriteStartObject();
            json.WriteString("schema", tenant.Tenant.Schema);
            json.WriteString("identifier", tenant.Tenant.Identifier);
            json.WriteNumber("objects", tenant.Changes.Added.Count);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        WriteNames(json, "upToDate", tenants.UpToDate.Select(static tenant => tenant.Tenant.Schema));
        WriteNames(json, "formerTenants", tenants.List.FormerSchemas);
        if (tenants.List.Unavailable is { } unavailable)
        {
            json.WriteString("tenantsNotRead", unavailable);
        }
    }

    /// <summary>The <c>added</c>, <c>removed</c>, and <c>modified</c> arrays; <paramref name="display"/> rewrites each id and value, as for the text list.</summary>
    private static void WriteChangeArrays(Utf8JsonWriter json, ChangeSet changeSet, Func<string, string>? display = null)
    {
        display ??= static text => text;
        json.WriteStartArray("added");
        foreach (var change in changeSet.Added)
        {
            WriteObject(json, change.Node.Kind, display(change.NodeId));
        }

        json.WriteEndArray();

        json.WriteStartArray("removed");
        foreach (var change in changeSet.Removed)
        {
            WriteObject(json, change.Node.Kind, display(change.NodeId));
        }

        json.WriteEndArray();

        json.WriteStartArray("modified");
        foreach (var change in changeSet.Modified)
        {
            json.WriteStartObject();
            json.WriteString("kind", change.NewNode.Kind);
            json.WriteString("id", display(change.NodeId));
            json.WriteStartArray("changes");
            foreach (var detail in change.Details)
            {
                json.WriteStartObject();
                json.WriteString("property", DiffPropertyLabels.For(detail.PropertyName));
                json.WriteString("old", detail.OldValue is null ? null : display(detail.OldValue));
                json.WriteString("new", detail.NewValue is null ? null : display(detail.NewValue));
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        json.WriteEndArray();
    }

    private static void WriteObject(Utf8JsonWriter json, string kind, string id)
    {
        json.WriteStartObject();
        json.WriteString("kind", kind);
        json.WriteString("id", id);
        json.WriteEndObject();
    }

    private static void WriteNames(Utf8JsonWriter json, string property, IEnumerable<string> names)
    {
        json.WriteStartArray(property);
        foreach (var name in names)
        {
            json.WriteStringValue(name);
        }

        json.WriteEndArray();
    }
}
