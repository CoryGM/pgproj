using System.Text.Json;

namespace PgProj.Parser;

/// <summary>
/// Reads the statements that state an object's owner from pg_query's JSON AST (G-146): <c>AlterOwnerStmt</c> (schemas, routines, aggregates,
/// operators, types, domains, event triggers, publications), the <c>OWNER TO</c> action of <c>AlterTableStmt</c> (tables, views, materialized
/// views, sequences), and the <c>AUTHORIZATION</c> of <c>CreateSchemaStmt</c>. Other kinds of object (collations, foreign data wrappers, and so
/// on) have an <c>ALTER ... OWNER TO</c> that is still reported as not modeled. A collation, extended statistics object, and text search dictionary and configuration are
/// named like a type (G-154).
/// </summary>
internal static class OwnerStatementReader
{
    /// <summary>The <c>objectType</c>s of an <c>AlterOwnerStmt</c> that are read, with the kind of <see cref="OwnerNode"/> each makes.</summary>
    private static readonly Dictionary<string, string> AlterOwnerKinds = new(StringComparer.Ordinal)
    {
        ["OBJECT_SCHEMA"] = "Schema",
        ["OBJECT_FUNCTION"] = "Function",
        ["OBJECT_PROCEDURE"] = "Procedure",
        ["OBJECT_AGGREGATE"] = "Aggregate",
        ["OBJECT_OPERATOR"] = "Operator",
        ["OBJECT_TYPE"] = "Type",
        ["OBJECT_DOMAIN"] = "Domain",
        ["OBJECT_EVENT_TRIGGER"] = "EventTrigger",
        ["OBJECT_PUBLICATION"] = "Publication",
        ["OBJECT_COLLATION"] = "Collation",
        ["OBJECT_STATISTIC_EXT"] = "Statistics",
        ["OBJECT_TSDICTIONARY"] = "TextSearchDictionary",
        ["OBJECT_TSCONFIGURATION"] = "TextSearchConfiguration",
    };

    /// <summary>The <c>objtype</c>s of an <c>AlterTableStmt</c> whose <c>OWNER TO</c> is read, with the kind of <see cref="OwnerNode"/> each makes.</summary>
    private static readonly Dictionary<string, string> AlterTableKinds = new(StringComparer.Ordinal)
    {
        ["OBJECT_TABLE"] = "Table",
        ["OBJECT_VIEW"] = "View",
        ["OBJECT_MATVIEW"] = "MaterializedView",
        ["OBJECT_SEQUENCE"] = "Sequence",
    };

    /// <summary>Whether <paramref name="alterOwner"/> (an <c>AlterOwnerStmt</c>) is one the model reads: a supported kind of object, owned by a named role.</summary>
    public static bool IsReadAlterOwner(JsonElement alterOwner)
        => AlterOwnerKinds.ContainsKey(ReadString(alterOwner, "objectType") ?? string.Empty)
            && alterOwner.TryGetProperty("newowner", out var role)
            && ReadRoleName(role) is not null;

    /// <summary>
    /// Whether an <c>AlterTableStmt</c> on something other than a table (a view, materialized view, or sequence) does nothing but change the owner to a
    /// named role, which is all of it the model reads. A table's actions are read one by one.
    /// </summary>
    public static bool IsReadOwnerOnlyAlterTable(JsonElement alterTable)
    {
        if (!AlterTableKinds.ContainsKey(ReadString(alterTable, "objtype") ?? string.Empty)
            || !alterTable.TryGetProperty("cmds", out var commands)
            || commands.ValueKind != JsonValueKind.Array
            || commands.GetArrayLength() == 0)
        {
            return false;
        }

        return commands.EnumerateArray().All(static wrapper =>
            wrapper.TryGetProperty("AlterTableCmd", out var command)
            && ReadString(command, "subtype") == "AT_ChangeOwner"
            && command.TryGetProperty("newowner", out var role)
            && ReadRoleName(role) is not null);
    }

    /// <summary>Whether a <c>CreateSchemaStmt</c> names an owner (<c>AUTHORIZATION role</c>) that is a named role.</summary>
    public static bool IsReadCreateSchemaOwner(JsonElement createSchema)
        => createSchema.TryGetProperty("authrole", out var role) && ReadRoleName(role) is not null;

    public static OwnerNode? ReadAlterOwner(JsonElement alterOwner)
    {
        if (!AlterOwnerKinds.TryGetValue(ReadString(alterOwner, "objectType") ?? string.Empty, out var kind)
            || !alterOwner.TryGetProperty("object", out var target)
            || !alterOwner.TryGetProperty("newowner", out var newOwner))
        {
            return null;
        }

        var role = ReadRoleName(newOwner);
        switch (kind)
        {
            case "Schema" or "EventTrigger" or "Publication":
                return ReadString(target) is { } name ? new OwnerNode(kind, null, name, role) : null;

            case "Type" or "Domain" or "Collation" or "Statistics" or "TextSearchDictionary" or "TextSearchConfiguration":
                return ReadQualifiedName(target) is { } typeName ? new OwnerNode(kind, typeName.Schema, typeName.Name, role) : null;

            case "Operator":
                return ReadOperator(target, role);

            default:
                return ReadRoutine(kind, target, role);
        }
    }

    /// <summary>The owner an <c>ALTER TABLE|VIEW|MATERIALIZED VIEW|SEQUENCE name OWNER TO role</c> action states; null for <c>CURRENT_USER</c>.</summary>
    public static OwnerNode? ReadAlterTableOwner(JsonElement alterTable, JsonElement command, string? name, string? schemaName)
    {
        if (!AlterTableKinds.TryGetValue(ReadString(alterTable, "objtype") ?? string.Empty, out var kind)
            || !command.TryGetProperty("newowner", out var newOwner)
            || ReadRoleName(newOwner) is not { } role
            || name is null)
        {
            return null;
        }

        return new OwnerNode(kind, schemaName, name, role);
    }

    /// <summary><c>CREATE SCHEMA name AUTHORIZATION role</c>; without a name, the schema is named after the role.</summary>
    public static OwnerNode? ReadCreateSchema(JsonElement createSchema)
    {
        if (!createSchema.TryGetProperty("authrole", out var authRole) || ReadRoleName(authRole) is not { } role)
        {
            return null;
        }

        var name = ReadString(createSchema, "schemaname") ?? role;
        return new OwnerNode("Schema", null, name, role);
    }

    /// <summary>
    /// The schema a <c>CREATE SCHEMA</c> declares (G-284): the name written, or the role's for <c>CREATE SCHEMA AUTHORIZATION role</c>; null for
    /// <c>AUTHORIZATION CURRENT_USER</c> with no name, which names whoever runs the script.
    /// </summary>
    public static SchemaNode? ReadCreateSchemaName(JsonElement createSchema)
        => (ReadString(createSchema, "schemaname") ?? (createSchema.TryGetProperty("authrole", out var authRole) ? ReadRoleName(authRole) : null)) is { } name
            ? new SchemaNode(name)
            : null;

    private static OwnerNode? ReadRoutine(string kind, JsonElement target, string? role)
    {
        if (!target.TryGetProperty("ObjectWithArgs", out var routine) || ReadQualifiedNameList(routine, "objname") is not { } name)
        {
            return null;
        }

        // `ALTER FUNCTION f OWNER TO r` (no parentheses) says args_unspecified; `f()` and `agg(*)` have an empty list.
        IReadOnlyList<string>? argumentTypes = null;
        if (!(routine.TryGetProperty("args_unspecified", out var unspecified) && unspecified.ValueKind == JsonValueKind.True))
        {
            var types = new List<string>();
            if (routine.TryGetProperty("objargs", out var arguments) && arguments.ValueKind == JsonValueKind.Array)
            {
                foreach (var argument in arguments.EnumerateArray())
                {
                    if (ReadTypeText(argument) is not { } typeText)
                    {
                        return null;
                    }

                    types.Add(typeText);
                }
            }

            argumentTypes = types;
        }

        return new OwnerNode(kind, name.Schema, name.Name, role) { ArgumentTypes = argumentTypes };
    }

    private static OwnerNode? ReadOperator(JsonElement target, string? role)
    {
        if (!target.TryGetProperty("ObjectWithArgs", out var routine) || ReadQualifiedNameList(routine, "objname") is not { } name)
        {
            return null;
        }

        string?[] operands = [null, null];
        if (routine.TryGetProperty("objargs", out var arguments) && arguments.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var argument in arguments.EnumerateArray())
            {
                if (index < 2)
                {
                    operands[index] = ReadTypeText(argument);
                }

                index++;
            }
        }

        return new OwnerNode("Operator", name.Schema, name.Name, role) { LeftType = operands[0], RightType = operands[1] };
    }

    /// <summary>A type as written: <c>schema.name</c> or <c>name</c>, with <c>[]</c> for each array bound. Null when the element is not a type (an operator's missing operand).</summary>
    private static string? ReadTypeText(JsonElement element)
    {
        if (!element.TryGetProperty("TypeName", out var typeName) || ReadQualifiedNameList(typeName, "names") is not { } parts)
        {
            return null;
        }

        var arrayBounds = typeName.TryGetProperty("arrayBounds", out var bounds) && bounds.ValueKind == JsonValueKind.Array ? bounds.GetArrayLength() : 0;
        var text = parts.Schema is null ? parts.Name : $"{parts.Schema}.{parts.Name}";
        return text + string.Concat(Enumerable.Repeat("[]", arrayBounds));
    }

    /// <summary>The role's name when it is written as one; null for <c>CURRENT_USER</c>, <c>SESSION_USER</c>, <c>CURRENT_ROLE</c>, and <c>PUBLIC</c>.</summary>
    private static string? ReadRoleName(JsonElement role)
        => ReadString(role, "roletype") == "ROLESPEC_CSTRING" ? ReadString(role, "rolename") : null;

    /// <summary>A name written as a list (<c>app.ty</c>): the last item is the name and the one before it the schema.</summary>
    private static (string? Schema, string Name)? ReadQualifiedName(JsonElement element)
    {
        if (!element.TryGetProperty("List", out var list) || !list.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var parts = items.EnumerateArray().Select(static item => ReadString(item)).OfType<string>().ToArray();
        return parts.Length == 0 ? null : (parts.Length >= 2 ? parts[^2] : null, parts[^1]);
    }

    private static (string? Schema, string Name)? ReadQualifiedNameList(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var names) || names.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var parts = names.EnumerateArray().Select(static item => ReadString(item)).OfType<string>().ToArray();
        return parts.Length == 0 ? null : (parts.Length >= 2 ? parts[^2] : null, parts[^1]);
    }

    /// <summary>The text of a <c>{"String":{"sval":...}}</c> wrapper.</summary>
    private static string? ReadString(JsonElement element)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty("String", out var text) && text.TryGetProperty("sval", out var value)
            ? value.GetString()
            : null;

    private static string? ReadString(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
