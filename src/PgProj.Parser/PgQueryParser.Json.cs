using System.Globalization;
using System.Text.Json;

namespace PgProj.Parser;

public sealed partial class PgQueryParser
{
    private static string? TryGetStringPropertyRecursive(JsonElement element, string propertyName)
    {
        if (!TryFindPropertyRecursive(element, propertyName, out var result))
        {
            return null;
        }

        return result.ValueKind == JsonValueKind.String ? result.GetString() : null;
    }

    private static string? TryGetDirectStringProperty(JsonElement element, string propertyName)
    {
        if (!TryGetDirectProperty(element, propertyName, out var result))
        {
            return null;
        }

        return result.ValueKind == JsonValueKind.String ? result.GetString() : null;
    }

    private static bool TryGetDirectBooleanProperty(JsonElement element, string propertyName, out bool value)
    {
        value = default;

        if (!TryGetDirectProperty(element, propertyName, out var propertyValue))
        {
            return false;
        }

        switch (propertyValue.ValueKind)
        {
            case JsonValueKind.True:
                value = true;
                return true;
            case JsonValueKind.False:
                value = false;
                return true;
            case JsonValueKind.Number when propertyValue.TryGetInt32(out var intValue):
                value = intValue != 0;
                return true;
            case JsonValueKind.String when bool.TryParse(propertyValue.GetString(), out var boolValue):
                value = boolValue;
                return true;
            default:
                return false;
        }
    }

    private static bool TryGetDirectProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private static bool TryGetDirectInt32Property(JsonElement element, string propertyName, out int value)
    {
        value = 0;
        return TryGetDirectProperty(element, propertyName, out var propertyValue)
            && propertyValue.ValueKind == JsonValueKind.Number
            && propertyValue.TryGetInt32(out value);
    }

    private static bool TryFindPropertyRecursive(JsonElement element, string propertyName, out JsonElement result)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, propertyName, StringComparison.Ordinal))
                    {
                        result = property.Value;
                        return true;
                    }

                    if (TryFindPropertyRecursive(property.Value, propertyName, out result))
                    {
                        return true;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray())
                {
                    if (TryFindPropertyRecursive(child, propertyName, out result))
                    {
                        return true;
                    }
                }

                break;
        }

        result = default;
        return false;
    }
}
