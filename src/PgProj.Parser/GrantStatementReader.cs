using System.Text.Json;

namespace PgProj.Parser;

/// <summary>
/// Reads <c>GrantStmt</c>, <c>GrantRoleStmt</c>, and <c>AlterDefaultPrivilegesStmt</c> from pg_query's JSON AST (G-65).
/// Grants used to be found with regular expressions over the SQL text; the AST is the only place SQL is read.
/// </summary>
internal static class GrantStatementReader
{
    public static GrantNode? ReadGrantStatement(JsonElement element)
        => ReadPrivilegeStatement(element, defaultPrivilegeRoles: [], defaultPrivilegeSchemas: []);

    public static GrantNode? ReadDefaultPrivilegesStatement(JsonElement element)
    {
        if (!element.TryGetProperty("action", out var action) || action.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var roles = new List<string>();
        var schemas = new List<string>();
        if (element.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array)
        {
            foreach (var option in options.EnumerateArray())
            {
                if (!option.TryGetProperty("DefElem", out var definition)
                    || !definition.TryGetProperty("defname", out var nameElement)
                    || !definition.TryGetProperty("arg", out var argument)
                    || !argument.TryGetProperty("List", out var list)
                    || !list.TryGetProperty("items", out var items))
                {
                    continue;
                }

                var target = nameElement.GetString() switch
                {
                    "roles" => roles,
                    "schemas" => schemas,
                    _ => null,
                };

                foreach (var item in items.EnumerateArray())
                {
                    var value = item.TryGetProperty("RoleSpec", out var role)
                        ? ReadRoleName(role)
                        : ReadString(item);
                    if (value is not null)
                    {
                        target?.Add(value);
                    }
                }
            }
        }

        return ReadPrivilegeStatement(action, roles, schemas);
    }

    public static GrantNode? ReadRoleStatement(JsonElement element)
    {
        var unsupported = new List<string>();
        var granted = new List<string>();
        if (element.TryGetProperty("granted_roles", out var grantedRoles) && grantedRoles.ValueKind == JsonValueKind.Array)
        {
            foreach (var grantedRole in grantedRoles.EnumerateArray())
            {
                if (grantedRole.TryGetProperty("AccessPriv", out var privilege)
                    && privilege.TryGetProperty("priv_name", out var roleName)
                    && roleName.GetString() is { } name)
                {
                    granted.Add(name);
                }
            }
        }

        var withAdminOption = false;
        if (element.TryGetProperty("opt", out var options) && options.ValueKind == JsonValueKind.Array)
        {
            foreach (var option in options.EnumerateArray())
            {
                if (!option.TryGetProperty("DefElem", out var definition))
                {
                    continue;
                }

                var optionName = definition.TryGetProperty("defname", out var nameElement) ? nameElement.GetString() : null;
                if (optionName == "admin"
                    && definition.TryGetProperty("arg", out var argument)
                    && argument.TryGetProperty("Boolean", out var boolean)
                    && boolean.TryGetProperty("boolval", out var value)
                    && value.ValueKind == JsonValueKind.True)
                {
                    withAdminOption = true;
                }
                else
                {
                    unsupported.Add($"option '{optionName}'");
                }
            }
        }

        if (element.TryGetProperty("grantor", out _))
        {
            unsupported.Add("GRANTED BY");
        }

        var grantees = ReadGrantees(element, "grantee_roles", unsupported);

        return new GrantNode(GrantForm.RoleMembership, "role")
        {
            IsGrant = IsGrant(element),
            Roles = granted,
            Grantees = grantees,
            WithGrantOption = withAdminOption,
            UnsupportedParts = unsupported,
        };
    }

    private static GrantNode? ReadPrivilegeStatement(JsonElement statement, IReadOnlyList<string> defaultPrivilegeRoles, IReadOnlyList<string> defaultPrivilegeSchemas)
    {
        var targetType = statement.TryGetProperty("targtype", out var targetTypeElement) ? targetTypeElement.GetString() : null;
        var objectType = statement.TryGetProperty("objtype", out var objectTypeElement) ? objectTypeElement.GetString() : null;
        if (targetType is null || objectType is null)
        {
            return null;
        }

        var form = targetType switch
        {
            "ACL_TARGET_ALL_IN_SCHEMA" => GrantForm.AllInSchema,
            "ACL_TARGET_DEFAULTS" => GrantForm.DefaultPrivileges,
            _ => GrantForm.Object,
        };

        var unsupported = new List<string>();
        var targets = new List<GrantTarget>();
        if (statement.TryGetProperty("objects", out var objects) && objects.ValueKind == JsonValueKind.Array)
        {
            foreach (var obj in objects.EnumerateArray())
            {
                var target = ReadTarget(obj);
                if (target is null)
                {
                    unsupported.Add("an object the parser cannot name");
                    continue;
                }

                targets.Add(target);
            }
        }

        var privileges = new List<GrantPrivilege>();
        if (statement.TryGetProperty("privileges", out var privilegeList) && privilegeList.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in privilegeList.EnumerateArray())
            {
                if (!entry.TryGetProperty("AccessPriv", out var access))
                {
                    continue;
                }

                var name = access.TryGetProperty("priv_name", out var nameElement) && nameElement.GetString() is { } privilegeName
                    ? privilegeName.ToUpperInvariant()
                    : "ALL PRIVILEGES";
                var columns = new List<string>();
                if (access.TryGetProperty("cols", out var columnList) && columnList.ValueKind == JsonValueKind.Array)
                {
                    columns.AddRange(columnList.EnumerateArray().Select(ReadString).OfType<string>());
                }

                privileges.Add(new GrantPrivilege(name, columns));
            }
        }

        return new GrantNode(form, ReadObjectKind(objectType))
        {
            IsGrant = IsGrant(statement),
            Targets = targets,
            Privileges = privileges,
            Grantees = ReadGrantees(statement, "grantees", unsupported),
            WithGrantOption = statement.TryGetProperty("grant_option", out var grantOption) && grantOption.ValueKind == JsonValueKind.True,
            Roles = defaultPrivilegeRoles,
            Schemas = defaultPrivilegeSchemas,
            UnsupportedParts = unsupported,
        };
    }

    private static bool IsGrant(JsonElement statement)
        => statement.TryGetProperty("is_grant", out var isGrant) && isGrant.ValueKind == JsonValueKind.True;

    private static string ReadObjectKind(string objectType)
        => (objectType.StartsWith("OBJECT_", StringComparison.Ordinal) ? objectType["OBJECT_".Length..] : objectType)
            .ToLowerInvariant()
            .Replace('_', ' ');

    private static List<string> ReadGrantees(JsonElement statement, string propertyName, ICollection<string> unsupported)
    {
        var grantees = new List<string>();
        if (!statement.TryGetProperty(propertyName, out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return grantees;
        }

        foreach (var entry in list.EnumerateArray())
        {
            if (!entry.TryGetProperty("RoleSpec", out var role))
            {
                continue;
            }

            var name = ReadRoleName(role);
            if (name is null)
            {
                unsupported.Add(role.TryGetProperty("roletype", out var type) ? $"grantee {type.GetString()}" : "a grantee");
                continue;
            }

            grantees.Add(name);
        }

        return grantees;
    }

    /// <summary>The role's name, <c>PUBLIC</c>, or null for <c>CURRENT_USER</c> and friends (they name whoever runs the script).</summary>
    private static string? ReadRoleName(JsonElement role)
    {
        var type = role.TryGetProperty("roletype", out var typeElement) ? typeElement.GetString() : null;
        return type switch
        {
            "ROLESPEC_PUBLIC" => "PUBLIC",
            "ROLESPEC_CSTRING" when role.TryGetProperty("rolename", out var name) => name.GetString(),
            _ => null,
        };
    }

    private static GrantTarget? ReadTarget(JsonElement element)
    {
        if (element.TryGetProperty("RangeVar", out var relation))
        {
            var name = relation.TryGetProperty("relname", out var relationName) ? relationName.GetString() : null;
            var schema = relation.TryGetProperty("schemaname", out var schemaName) ? schemaName.GetString() : null;
            return name is null ? null : new GrantTarget(schema, name);
        }

        if (element.TryGetProperty("String", out _))
        {
            return ReadString(element) is { } name ? new GrantTarget(null, name) : null;
        }

        if (element.TryGetProperty("TypeName", out var typeName))
        {
            return ReadQualifiedName(typeName, "names") is { } parts ? new GrantTarget(parts.Schema, parts.Name) : null;
        }

        if (element.TryGetProperty("ObjectWithArgs", out var routine))
        {
            if (ReadQualifiedName(routine, "objname") is not { } parts)
            {
                return null;
            }

            var argumentTypes = new List<string>();
            if (routine.TryGetProperty("objargs", out var arguments) && arguments.ValueKind == JsonValueKind.Array)
            {
                foreach (var argument in arguments.EnumerateArray())
                {
                    if (!argument.TryGetProperty("TypeName", out var argumentType)
                        || ReadQualifiedName(argumentType, "names") is not { } typeParts)
                    {
                        return null;
                    }

                    var arrayBounds = argumentType.TryGetProperty("arrayBounds", out var bounds) && bounds.ValueKind == JsonValueKind.Array
                        ? bounds.GetArrayLength()
                        : 0;
                    var typeText = typeParts.Schema is null ? typeParts.Name : $"{typeParts.Schema}.{typeParts.Name}";
                    argumentTypes.Add(typeText + string.Concat(Enumerable.Repeat("[]", arrayBounds)));
                }
            }

            return new GrantTarget(parts.Schema, parts.Name, argumentTypes);
        }

        return null;
    }

    private static (string? Schema, string Name)? ReadQualifiedName(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var names) || names.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var parts = names.EnumerateArray().Select(ReadString).OfType<string>().ToArray();
        return parts.Length == 0
            ? null
            : (parts.Length >= 2 ? parts[^2] : null, parts[^1]);
    }

    private static string? ReadString(JsonElement element)
        => element.TryGetProperty("String", out var text) && text.TryGetProperty("sval", out var value) ? value.GetString() : null;
}
