using System.Globalization;
using System.Text.Json;

namespace PgProj.Parser;

public sealed partial class PgQueryParser
{
    private static IReadOnlyList<string> TryGetStringListFromDirectProperty(JsonElement element, string propertyName)
    {
        if (!TryGetDirectProperty(element, propertyName, out var listElement) || listElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var values = new List<string>();
        foreach (var item in listElement.EnumerateArray())
        {
            var value = TryGetStringPropertyRecursive(item, "sval")
                ?? TryGetStringPropertyRecursive(item, "str")
                ?? (item.ValueKind == JsonValueKind.String ? item.GetString() : null);

            if (!string.IsNullOrWhiteSpace(value))
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static string? TryFindQualifiedNameUnderProperty(JsonElement element, string namesPropertyName)
    {
        if (!TryFindPropertyRecursive(element, namesPropertyName, out var namesElement) || namesElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var namePart in namesElement.EnumerateArray())
        {
            var value = TryGetStringPropertyRecursive(namePart, "sval") ?? TryGetStringPropertyRecursive(namePart, "str");
            if (!string.IsNullOrWhiteSpace(value))
            {
                parts.Add(value);
            }
        }

        return parts.Count == 0 ? null : string.Join('.', parts);
    }

    private static List<string>? TryGetQualifiedNameParts(JsonElement element, string propertyName)
    {
        if (!TryGetDirectProperty(element, propertyName, out var objectElement))
        {
            return null;
        }

        if (!TryFindPropertyRecursive(objectElement, "items", out var itemsElement) || itemsElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var item in itemsElement.EnumerateArray())
        {
            var namePart = TryGetStringPropertyRecursive(item, "sval") ?? TryGetStringPropertyRecursive(item, "str");
            if (!string.IsNullOrWhiteSpace(namePart))
            {
                parts.Add(namePart);
            }
        }

        return parts;
    }

    private static bool TryGetRelationIdentity(JsonElement element, string propertyName, out string? relationName, out string? schemaName)
    {
        relationName = null;
        schemaName = null;

        if (!TryFindPropertyRecursive(element, propertyName, out var relationElement))
        {
            return false;
        }

        relationName = TryGetStringPropertyRecursive(relationElement, "relname");
        schemaName = TryGetStringPropertyRecursive(relationElement, "schemaname");
        return !string.IsNullOrWhiteSpace(relationName);
    }

    private static string? TryGetQualifiedName(JsonElement element, string propertyName)
    {
        if (!TryFindPropertyRecursive(element, propertyName, out var nameElement) || nameElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var part in nameElement.EnumerateArray())
        {
            var namePart = TryGetStringPropertyRecursive(part, "sval")
                ?? TryGetStringPropertyRecursive(part, "str")
                ?? (part.ValueKind == JsonValueKind.String ? part.GetString() : null);

            if (!string.IsNullOrWhiteSpace(namePart))
            {
                parts.Add(namePart);
            }
        }

        return parts.Count == 0 ? null : string.Join('.', parts);
    }

    private static string? TryGetQualifiedNameFromDirectProperty(JsonElement element, string propertyName)
    {
        if (!TryGetDirectProperty(element, propertyName, out var nameElement) || nameElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var part in nameElement.EnumerateArray())
        {
            var namePart = TryGetStringPropertyRecursive(part, "sval")
                ?? TryGetStringPropertyRecursive(part, "str")
                ?? (part.ValueKind == JsonValueKind.String ? part.GetString() : null);

            if (!string.IsNullOrWhiteSpace(namePart))
            {
                parts.Add(namePart);
            }
        }

        return parts.Count == 0 ? null : string.Join('.', parts);
    }

    /// <summary>The name parts of a pg_query <c>List</c> of <c>String</c>s joined by dots, or null when it holds none.</summary>
    private static string? JoinNameList(JsonElement element)
    {
        if (!TryGetDirectProperty(element, "List", out var list))
        {
            return null;
        }

        var parts = TryGetStringListFromDirectProperty(list, "items");
        return parts.Count == 0 ? null : string.Join('.', parts);
    }

    private static void SplitSchemaQualifiedName(string qualifiedName, out string? schemaName, out string? objectName)
    {
        var lastSeparatorIndex = qualifiedName.LastIndexOf('.');
        if (lastSeparatorIndex < 0)
        {
            schemaName = null;
            objectName = qualifiedName;
            return;
        }

        schemaName = qualifiedName[..lastSeparatorIndex];
        objectName = qualifiedName[(lastSeparatorIndex + 1)..];
    }

    // TODO: Expand the JSON walker to cover more PostgreSQL statement kinds and preserve source spans.
    // TODO: Promote the shallow AST mapping into a richer semantic model once downstream consumers are defined.
}
