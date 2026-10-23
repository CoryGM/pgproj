using System.Globalization;
using System.Text.Json;

namespace PgProj.Parser;

public sealed partial class PgQueryParser
{
    private static ObjectCommentNode? TryCreateObjectCommentNode(JsonElement element)
    {
        if (!TryGetDirectProperty(element, "objtype", out var objTypeElement) || objTypeElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var objType = objTypeElement.GetString();

        var commentText = TryGetDirectProperty(element, "comment", out var commentElement) && commentElement.ValueKind == JsonValueKind.String
            ? commentElement.GetString()
            : null;

        switch (objType)
        {
            case "OBJECT_TABLE":
            case "OBJECT_COLUMN":
            {
                var qualifiedNameParts = TryGetQualifiedNameParts(element, "object");
                if (qualifiedNameParts is null || qualifiedNameParts.Count == 0)
                {
                    return null;
                }

                if (objType == "OBJECT_TABLE")
                {
                    var tableName = qualifiedNameParts[^1];
                    var schemaName = qualifiedNameParts.Count >= 2 ? qualifiedNameParts[^2] : null;
                    return new ObjectCommentNode("Table", schemaName, tableName, ColumnName: null, commentText);
                }

                // OBJECT_COLUMN: [schema, table, column] or [table, column]
                if (qualifiedNameParts.Count < 2)
                {
                    return null;
                }

                var columnName = qualifiedNameParts[^1];
                var columnTableName = qualifiedNameParts[^2];
                var columnSchemaName = qualifiedNameParts.Count >= 3 ? qualifiedNameParts[^3] : null;
                return new ObjectCommentNode("Column", columnSchemaName, columnTableName, columnName, commentText);
            }

            case "OBJECT_VIEW":
            case "OBJECT_MATVIEW":
            case "OBJECT_INDEX":
            case "OBJECT_SEQUENCE":
            case "OBJECT_COLLATION":
            case "OBJECT_STATISTIC_EXT":
            case "OBJECT_TSDICTIONARY":
            case "OBJECT_TSCONFIGURATION":
            {
                var qualifiedNameParts = TryGetQualifiedNameParts(element, "object");
                if (qualifiedNameParts is null || qualifiedNameParts.Count == 0)
                {
                    return null;
                }

                var kind = objType switch
                {
                    "OBJECT_VIEW" or "OBJECT_MATVIEW" => "View",
                    "OBJECT_INDEX" => "Index",
                    "OBJECT_SEQUENCE" => "Sequence",
                    "OBJECT_COLLATION" => "Collation",
                    "OBJECT_STATISTIC_EXT" => "Statistics",
                    "OBJECT_TSDICTIONARY" => "TextSearchDictionary",
                    "OBJECT_TSCONFIGURATION" => "TextSearchConfiguration",
                    _ => objType,
                };

                var name = qualifiedNameParts[^1];
                var schema = qualifiedNameParts.Count >= 2 ? qualifiedNameParts[^2] : null;
                return new ObjectCommentNode(kind, schema, name, ColumnName: null, commentText);
            }

            case "OBJECT_TABCONSTRAINT":
            case "OBJECT_TRIGGER":
            case "OBJECT_POLICY":
            {
                // `COMMENT ON CONSTRAINT|TRIGGER|POLICY name ON [schema.]table`: the object list is [schema, table, name] or [table, name] (a domain's constraint is OBJECT_DOMCONSTRAINT, not modeled) (G-161, G-163, G-164).
                var ownedParts = TryGetQualifiedNameParts(element, "object");
                if (ownedParts is null || ownedParts.Count < 2)
                {
                    return null;
                }

                var ownerTable = ownedParts[^2];
                var ownerSchema = ownedParts.Count >= 3 ? ownedParts[^3] : null;
                return new ObjectCommentNode(objType == "OBJECT_TRIGGER" ? "Trigger" : objType == "OBJECT_POLICY" ? "Policy" : "Constraint", ownerSchema, ownerTable, ColumnName: null, commentText) { ObjectName = ownedParts[^1] };
            }

            case "OBJECT_SCHEMA":
            {
                // `COMMENT ON SCHEMA name`: the object is the bare name, not a qualified list (G-160). The node carries the name as TableName, as the other kinds carry theirs, and as SchemaName.
                var schemaName = TryGetStringPropertyRecursive(element, "sval");
                return string.IsNullOrWhiteSpace(schemaName) ? null : new ObjectCommentNode("Schema", schemaName, schemaName, ColumnName: null, commentText);
            }

            case "OBJECT_EXTENSION":
            {
                // `COMMENT ON EXTENSION name` (G-213): an extension has no schema; the node carries its name as TableName, as the other kinds carry theirs.
                var extensionName = TryGetStringPropertyRecursive(element, "sval");
                return string.IsNullOrWhiteSpace(extensionName) ? null : new ObjectCommentNode("Extension", null, extensionName, ColumnName: null, commentText);
            }

            case "OBJECT_TYPE":
            case "OBJECT_DOMAIN":
            {
                // `COMMENT ON DOMAIN d` is `COMMENT ON TYPE d` to PostgreSQL (one pg_type row, one comment), so it is read as the same statement (G-302).
                if (!TryGetDirectProperty(element, "object", out var typeObjectElement)
                    || !TryFindPropertyRecursive(typeObjectElement, "names", out var namesElement)
                    || namesElement.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                var typeNameParts = new List<string>();
                foreach (var namePart in namesElement.EnumerateArray())
                {
                    var value = TryGetStringPropertyRecursive(namePart, "sval") ?? TryGetStringPropertyRecursive(namePart, "str");
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        typeNameParts.Add(value);
                    }
                }

                if (typeNameParts.Count == 0)
                {
                    return null;
                }

                var typeName = typeNameParts[^1];
                var typeSchema = typeNameParts.Count >= 2 ? typeNameParts[^2] : null;
                return new ObjectCommentNode("Type", typeSchema, typeName, ColumnName: null, commentText);
            }

            case "OBJECT_FUNCTION":
            case "OBJECT_PROCEDURE":
            {
                if (!TryGetDirectProperty(element, "object", out var objectElement)
                    || !TryFindPropertyRecursive(objectElement, "objname", out var objNameElement)
                    || objNameElement.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                var routineNameParts = new List<string>();
                foreach (var item in objNameElement.EnumerateArray())
                {
                    var part = TryGetStringPropertyRecursive(item, "sval") ?? TryGetStringPropertyRecursive(item, "str");
                    if (!string.IsNullOrWhiteSpace(part))
                    {
                        routineNameParts.Add(part);
                    }
                }

                if (routineNameParts.Count == 0)
                {
                    return null;
                }

                var routineName = routineNameParts[^1];
                var routineSchema = routineNameParts.Count >= 2 ? routineNameParts[^2] : null;

                var argumentTypeSignature = TryExtractCommentRoutineArgumentSignature(objectElement);

                var routineKind = objType == "OBJECT_FUNCTION" ? "Function" : "Procedure";
                return new ObjectCommentNode(routineKind, routineSchema, routineName, ColumnName: null, commentText)
                {
                    ArgumentTypeSignature = argumentTypeSignature,
                };
            }

            default:
                // Other object kinds (schemas, extensions, constraints, ...) remain unimplemented.
                return null;
        }
    }

    /// <summary>
    /// Extracts the comma-joined argument-type signature from a <c>COMMENT ON FUNCTION/PROCEDURE
    /// name(argtypes)</c> statement's <c>ObjectWithArgs</c> node, matching the format produced by
    /// <see cref="RoutineSignature.RenderArgumentTypeSignature"/> so the comment can be matched
    /// against the correct overload.
    /// </summary>
    private static string? TryExtractCommentRoutineArgumentSignature(JsonElement objectWithArgsElement)
    {
        if (!TryFindPropertyRecursive(objectWithArgsElement, "objargs", out var objArgsElement) || objArgsElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var argumentTypes = new List<string>();
        foreach (var argElement in objArgsElement.EnumerateArray())
        {
            var typeName = TryFindQualifiedNameUnderProperty(argElement, "names");
            if (!string.IsNullOrWhiteSpace(typeName))
            {
                argumentTypes.Add(typeName);
            }
        }

        return argumentTypes.Count == 0 ? null : string.Join(", ", argumentTypes);
    }
}
