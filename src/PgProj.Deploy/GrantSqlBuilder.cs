using System.Text.RegularExpressions;
using PgProj.Semantics;

namespace PgProj.Deploy;

/// <summary>
/// The <c>GRANT</c> and <c>REVOKE</c> text for a <see cref="GrantDefinition"/>, in one place so a grant and the statement that
/// undoes it are always spelled from the same parts (G-65). The forms that existed before G-65 (schema, <c>ALL ... IN SCHEMA</c>,
/// default privileges) keep the SQL they always had; grants on named objects, column privileges, <c>WITH GRANT OPTION</c>,
/// <c>FOR ROLE</c>, and role membership are new.
/// </summary>
internal static class GrantSqlBuilder
{
    private const string InSchema = " in schema:";

    private static readonly Regex SimpleIdentifier = new("^[a-z_][a-z0-9_$]*$", RegexOptions.Compiled);

    public static string Grant(GrantDefinition grant) => Build(grant, isGrant: true);

    public static string Revoke(GrantDefinition grant) => Build(grant, isGrant: false);

    private static string Build(GrantDefinition grant, bool isGrant)
    {
        var verb = isGrant ? "GRANT" : "REVOKE";
        if (string.IsNullOrWhiteSpace(grant.Grantee) || string.IsNullOrWhiteSpace(grant.ObjectKey))
        {
            return $"-- TODO: Insufficient metadata to emit {verb} for '{grant.Key}'.";
        }

        var direction = isGrant ? "TO" : "FROM";
        var grantee = FormatGrantee(grant.Grantee);
        var objectKey = grant.ObjectKey;

        if (objectKey.StartsWith("role:", StringComparison.OrdinalIgnoreCase))
        {
            var option = isGrant && grant.WithGrantOption ? " WITH ADMIN OPTION" : string.Empty;
            return $"{verb} {QuoteIdentifier(objectKey["role:".Length..])} {direction} {grantee}{option};";
        }

        var grantOption = isGrant && grant.WithGrantOption ? " WITH GRANT OPTION" : string.Empty;
        var privileges = FormatPrivileges(grant);

        if (objectKey.StartsWith("schema:", StringComparison.OrdinalIgnoreCase))
        {
            return $"{verb} {privileges} ON SCHEMA {FormatQualifiedName(null, objectKey["schema:".Length..])} {direction} {grantee}{grantOption};";
        }

        if (objectKey.StartsWith("all ", StringComparison.OrdinalIgnoreCase) && objectKey.Contains(InSchema, StringComparison.OrdinalIgnoreCase))
        {
            var afterAll = objectKey["all ".Length..];
            var separatorIndex = afterAll.IndexOf(InSchema, StringComparison.OrdinalIgnoreCase);
            var objectKind = afterAll[..separatorIndex];
            var schemaName = afterAll[(separatorIndex + InSchema.Length)..];
            return $"{verb} {privileges} ON ALL {objectKind.ToUpperInvariant()} IN SCHEMA {FormatQualifiedName(null, schemaName)} {direction} {grantee}{grantOption};";
        }

        if (objectKey.StartsWith("default ", StringComparison.OrdinalIgnoreCase))
        {
            var afterDefault = objectKey["default ".Length..];
            var separatorIndex = afterDefault.IndexOf(InSchema, StringComparison.OrdinalIgnoreCase);
            var objectKind = separatorIndex < 0 ? afterDefault : afterDefault[..separatorIndex];
            var roleClause = string.IsNullOrWhiteSpace(grant.ForRole) ? string.Empty : $" FOR ROLE {QuoteIdentifier(grant.ForRole)}";
            var schemaClause = separatorIndex < 0
                ? string.Empty
                : $" IN SCHEMA {FormatQualifiedName(null, afterDefault[(separatorIndex + InSchema.Length)..])}";
            return $"ALTER DEFAULT PRIVILEGES{roleClause}{schemaClause} {verb} {privileges} ON {objectKind.ToUpperInvariant()} {direction} {grantee}{grantOption};";
        }

        foreach (var kind in new[] { "table", "sequence", "function", "procedure" })
        {
            if (objectKey.StartsWith(kind + ":", StringComparison.OrdinalIgnoreCase))
            {
                return $"{verb} {privileges} ON {kind.ToUpperInvariant()} {FormatObject(grant, kind)} {direction} {grantee}{grantOption};";
            }
        }

        return $"-- TODO: Unrecognized grant object key '{objectKey}' for '{grant.Key}'.";
    }

    /// <summary>The privileges, each limited to the grant's columns when it has any (<c>SELECT ("a", "b"), UPDATE ("a", "b")</c>).</summary>
    private static string FormatPrivileges(GrantDefinition grant)
    {
        if (grant.Privileges.Count == 0)
        {
            return grant.Columns.Count == 0 ? "ALL PRIVILEGES" : $"ALL PRIVILEGES ({FormatColumns(grant.Columns)})";
        }

        return grant.Columns.Count == 0
            ? string.Join(", ", grant.Privileges)
            : string.Join(", ", grant.Privileges.Select(privilege => $"{privilege} ({FormatColumns(grant.Columns)})"));
    }

    private static string FormatColumns(IReadOnlyList<string> columns)
        => string.Join(", ", columns.Select(QuoteIdentifier));

    /// <summary>The object a grant names, from its parts when it has them and from its key otherwise, as <c>"schema"."name"</c> (with the argument types for a routine).</summary>
    private static string FormatObject(GrantDefinition grant, string kind)
    {
        var isRoutine = kind is "function" or "procedure";
        string? schemaName = grant.SchemaName;
        var name = grant.ObjectName;

        if (name is null)
        {
            // Built without the parts: the key is "<kind>:<schema>.<name>", or "<kind>:<name>" without a schema.
            var qualified = grant.ObjectKey![(kind.Length + 1)..];
            var separatorIndex = qualified.IndexOf('.');
            var parenthesisIndex = qualified.IndexOf('(');
            if (separatorIndex >= 0 && (parenthesisIndex < 0 || separatorIndex < parenthesisIndex))
            {
                schemaName = qualified[..separatorIndex];
                name = qualified[(separatorIndex + 1)..];
            }
            else
            {
                schemaName = null;
                name = qualified;
            }
        }

        if (!isRoutine)
        {
            return FormatQualifiedName(schemaName, name);
        }

        var argumentStart = name.LastIndexOf('(');
        var routineName = argumentStart < 0 ? name : name[..argumentStart];
        var argumentTypes = grant.ObjectArgumentTypes.Count > 0 || argumentStart < 0
            ? grant.ObjectArgumentTypes
            : name[(argumentStart + 1)..].TrimEnd(')').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return $"{FormatQualifiedName(schemaName, routineName)}({string.Join(", ", argumentTypes.Select(FormatType))})";
    }

    /// <summary>A type as the statement spelled it: each name part quoted only when it has to be, and array marks kept.</summary>
    private static string FormatType(string type)
    {
        var arrayMarks = string.Empty;
        while (type.EndsWith("[]", StringComparison.Ordinal))
        {
            arrayMarks += "[]";
            type = type[..^2];
        }

        return string.Join('.', type.Split('.').Select(static part => SimpleIdentifier.IsMatch(part) ? part : QuoteIdentifier(part))) + arrayMarks;
    }

    private static string FormatGrantee(string grantee)
        => string.Equals(grantee, "PUBLIC", StringComparison.OrdinalIgnoreCase)
            ? "PUBLIC"
            : QuoteIdentifier(grantee);

    private static string FormatQualifiedName(string? schemaName, string name)
        => string.IsNullOrWhiteSpace(schemaName)
            ? QuoteIdentifier(name)
            : $"{QuoteIdentifier(schemaName)}.{QuoteIdentifier(name)}";

    private static string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
