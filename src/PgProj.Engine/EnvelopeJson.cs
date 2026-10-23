using System.Text.Json;

namespace PgProj.Engine;

/// <summary>
/// The head of the engine's JSON document (decision 3 on #213), one envelope for every command: <c>schemaVersion</c>, <c>status</c>, <c>message</c>, then the build
/// part. <see cref="BuildResult.ToJson"/> writes only this (G-309f); <see cref="CompareResult.ToJson"/> follows it with the comparison (G-309b).
/// </summary>
internal static class EnvelopeJson
{
    public static void WriteHead(
        Utf8JsonWriter json,
        int schemaVersion,
        string status,
        string? message,
        string? projectName,
        string? projectFilePath,
        string? sourcePath,
        IReadOnlyList<DocumentInfo> documents,
        IReadOnlyList<DocumentInfo> templateDocuments,
        IReadOnlyList<EngineDiagnostic> diagnostics,
        (int Errors, int Warnings, int Information) counts,
        Func<string, string> displayPath)
    {
        json.WriteNumber("schemaVersion", schemaVersion);
        json.WriteString("status", char.ToLowerInvariant(status[0]) + status[1..]);
        if (message is not null)
        {
            json.WriteString("message", message);
        }

        json.WriteStartObject("project");
        json.WriteString("name", projectName);
        if (projectFilePath is not null)
        {
            json.WriteString("file", displayPath(projectFilePath));
        }

        json.WriteString("sourcePath", sourcePath is null ? null : displayPath(sourcePath));
        json.WriteEndObject();

        WriteDocuments(json, "documents", documents, displayPath);
        if (templateDocuments.Count > 0)
        {
            WriteDocuments(json, "templateDocuments", templateDocuments, displayPath);
        }

        WriteDiagnostics(json, diagnostics, displayPath);
        json.WriteStartObject("counts");
        json.WriteNumber("errors", counts.Errors);
        json.WriteNumber("warnings", counts.Warnings);
        json.WriteNumber("information", counts.Information);
        json.WriteEndObject();
    }

    /// <summary>A database a run read or changed: <c>source</c> and <c>target</c> of a comparison (G-304b), <c>source</c> of a build (G-318).</summary>
    public static void WriteEnd(Utf8JsonWriter json, string name, DatabaseEnd end)
    {
        json.WriteStartObject(name);
        json.WriteString("database", end.Database);
        json.WriteString("host", end.Host);
        json.WriteNumber("port", end.Port);
        json.WriteEndObject();
    }

    private static void WriteDocuments(Utf8JsonWriter json, string name, IReadOnlyList<DocumentInfo> documents, Func<string, string> displayPath)
    {
        json.WriteStartArray(name);
        foreach (var document in documents)
        {
            json.WriteStartObject();
            json.WriteString("path", displayPath(document.Path));
            if (document.Version is int version)
            {
                json.WriteNumber("version", version);
            }

            json.WriteEndObject();
        }

        json.WriteEndArray();
    }

    /// <summary>The <c>diagnostics</c> array: grouped as the text output groups them (G-285), each with its place when known (G-123, G-308, G-309e).</summary>
    private static void WriteDiagnostics(Utf8JsonWriter json, IReadOnlyList<EngineDiagnostic> diagnostics, Func<string, string> displayPath)
    {
        json.WriteStartArray("diagnostics");
        foreach (var diagnostic in DiagnosticGrouping.ForOutput(diagnostics))
        {
            json.WriteStartObject();
            json.WriteString("severity", diagnostic.Severity == EngineSeverity.Information ? "Info" : diagnostic.Severity.ToString());
            json.WriteString("message", diagnostic.Message);

            // Where it is, when known; a diagnostic from no file has none of these.
            if (diagnostic.File is { Length: > 0 } filePath)
            {
                json.WriteString("file", displayPath(filePath));
                if (diagnostic.Line is int line)
                {
                    json.WriteNumber("line", line);
                }

                if (diagnostic.Column is int column)
                {
                    json.WriteNumber("column", column);

                    // The end, just past the last character (G-308); only with a start, and both or neither.
                    if (diagnostic.EndLine is int endLine && diagnostic.EndColumn is int endColumn)
                    {
                        json.WriteNumber("endLine", endLine);
                        json.WriteNumber("endColumn", endColumn);
                    }
                }
            }

            json.WriteEndObject();
        }

        json.WriteEndArray();
    }
}
