using PgProj.Parser;

namespace PgProj.Semantics;

/// <summary>
/// Turns the privilege statements the parser read (<see cref="GrantNode"/>) into <see cref="GrantDefinition"/>s (G-65). A
/// statement that names several grantees, several objects, or several <c>FOR ROLE</c>/<c>IN SCHEMA</c> targets is several
/// grants, one per combination, so a grant's identity is one object, one grantee, and one set of privileges.
/// </summary>
internal static class GrantDeclarations
{
    private const string AllPrivileges = "ALL PRIVILEGES";

    /// <summary>
    /// <paramref name="excludeGrants"/>/<paramref name="excludeRoleMemberships"/> (G-87) short-circuit before any
    /// diagnostic is added: an excluded statement produces no <see cref="GrantDefinition"/> and no diagnostic at
    /// all, identical to deleting it from source. Role membership (<see cref="GrantForm.RoleMembership"/>) is
    /// gated independently from every other grant form.
    /// </summary>
    public static IReadOnlyList<GrantDefinition> Declare(
        GrantNode node,
        ICollection<SemanticDiagnostic> diagnostics,
        bool excludeGrants = false,
        bool excludeRoleMemberships = false,
        SemanticLocation? location = null)
    {
        if (node.Form == GrantForm.RoleMembership ? excludeRoleMemberships : excludeGrants)
        {
            return [];
        }

        if (!node.IsGrant)
        {
            diagnostics.Add(SemanticDiagnostics.Warning(
                "REVOKE is not modeled and is ignored: a project states the privileges it wants with GRANT, and pgproj revokes the rest.",
                location,
                nameof(DeclarationPass)));
            return [];
        }

        if (UnmodeledStatement(node) is { } unmodeled)
        {
            diagnostics.Add(SemanticDiagnostics.Warning($"{unmodeled} is recognized but not modeled; the statement is ignored.", location, nameof(DeclarationPass)));
            return [];
        }

        foreach (var part in node.UnsupportedParts)
        {
            diagnostics.Add(SemanticDiagnostics.Warning(
                $"A {Describe(node)} statement uses {part}, which is not modeled; that part is ignored.",
                location,
                nameof(DeclarationPass)));
        }

        return node.Form switch
        {
            GrantForm.RoleMembership => DeclareRoleMemberships(node),
            GrantForm.AllInSchema => DeclareAllInSchema(node),
            GrantForm.DefaultPrivileges => DeclareDefaultPrivileges(node),
            _ => DeclareObjectGrants(node),
        };
    }

    private static readonly string[] ObjectKinds = ["schema", "table", "sequence", "function", "procedure"];
    private static readonly string[] AllInSchemaKinds = ["table", "sequence", "function"];
    private static readonly string[] DefaultPrivilegeKinds = ["table", "sequence", "function", "type", "schema"];

    /// <summary>What the statement is, in words, when the model has no place for it (a grant on a database, a type, a language, ...); null when it is modeled.</summary>
    private static string? UnmodeledStatement(GrantNode node)
    {
        var kind = node.ObjectKind.ToUpperInvariant();
        return node.Form switch
        {
            GrantForm.Object when !ObjectKinds.Contains(node.ObjectKind) => $"GRANT ... ON {kind}",
            GrantForm.AllInSchema when !AllInSchemaKinds.Contains(node.ObjectKind) => $"GRANT ... ON ALL {kind}S IN SCHEMA",
            GrantForm.DefaultPrivileges when !DefaultPrivilegeKinds.Contains(node.ObjectKind) => $"ALTER DEFAULT PRIVILEGES ... ON {kind}S",
            _ => null,
        };
    }

    private static List<GrantDefinition> DeclareObjectGrants(GrantNode node)
    {
        var grants = new List<GrantDefinition>();
        foreach (var target in node.Targets)
        {
            var schemaName = target.SchemaName;
            string? objectName = null;
            string objectKey;

            switch (node.ObjectKind)
            {
                case "schema":
                    objectKey = $"schema:{target.Name}";
                    schemaName = target.Name;
                    break;
                case "table":
                case "sequence":
                    objectName = target.Name;
                    objectKey = $"{node.ObjectKind}:{DatabaseModel.CreateKey(schemaName, target.Name)}";
                    break;
                default:
                    // function or procedure
                    objectName = GrantExpansion.RoutineObjectName(target.Name, target.ArgumentTypes ?? []);
                    objectKey = $"{node.ObjectKind}:{DatabaseModel.CreateKey(schemaName, objectName)}";
                    break;
            }

            grants.AddRange(Create(node, schemaName, objectKey, objectName, forRole: null)
                .Select(grant => grant with { ObjectArgumentTypes = target.ArgumentTypes ?? [] }));
        }

        return grants;
    }

    private static List<GrantDefinition> DeclareAllInSchema(GrantNode node)
    {
        var plural = node.ObjectKind + "s";
        var grants = new List<GrantDefinition>();
        foreach (var target in node.Targets)
        {
            grants.AddRange(Create(node, target.Name, $"all {plural} in schema:{target.Name}", objectName: null, forRole: null));
        }

        return grants;
    }

    private static List<GrantDefinition> DeclareDefaultPrivileges(GrantNode node)
    {
        var plural = node.ObjectKind + "s";
        var grants = new List<GrantDefinition>();
        IReadOnlyList<string?> roles = node.Roles.Count == 0 ? [null] : node.Roles.Cast<string?>().ToArray();
        IReadOnlyList<string?> schemas = node.Schemas.Count == 0 ? [null] : node.Schemas.Cast<string?>().ToArray();
        foreach (var role in roles)
        {
            foreach (var schema in schemas)
            {
                var objectKey = schema is null ? $"default {plural}" : $"default {plural} in schema:{schema}";
                grants.AddRange(Create(node, schema, objectKey, objectName: null, role));
            }
        }

        return grants;
    }

    private static List<GrantDefinition> DeclareRoleMemberships(GrantNode node)
    {
        var grants = new List<GrantDefinition>();
        foreach (var role in node.Roles)
        {
            foreach (var grantee in node.Grantees)
            {
                grants.Add(CreateDefinition(
                    schemaName: null,
                    objectKey: $"role:{role}",
                    objectName: null,
                    grantee,
                    ["MEMBER"],
                    columns: [],
                    node.WithGrantOption,
                    forRole: null));
            }
        }

        return grants;
    }

    private static IEnumerable<GrantDefinition> Create(GrantNode node, string? schemaName, string objectKey, string? objectName, string? forRole)
    {
        // Privileges that are limited to columns are separate grants from the ones on the whole object, and each column list is its own.
        var groups = new List<(IReadOnlyList<string> Columns, List<string> Privileges)>();
        var wholeObject = new List<string>();
        if (node.Privileges.Count == 0)
        {
            wholeObject.Add(AllPrivileges);
        }

        foreach (var privilege in node.Privileges)
        {
            if (privilege.Columns.Count == 0)
            {
                wholeObject.Add(privilege.Name);
            }
            else
            {
                // Kept in the order written, for the GRANT a publish emits; the grant's key sorts them (G-112).
                groups.Add((privilege.Columns.Distinct(StringComparer.Ordinal).ToArray(), [privilege.Name]));
            }
        }

        if (wholeObject.Count > 0)
        {
            groups.Insert(0, ([], wholeObject));
        }

        foreach (var (columns, privileges) in groups)
        {
            foreach (var grantee in node.Grantees.Distinct(StringComparer.Ordinal))
            {
                yield return CreateDefinition(schemaName, objectKey, objectName, grantee, NormalizePrivileges(privileges), columns, node.WithGrantOption, forRole);
            }
        }
    }

    private static GrantDefinition CreateDefinition(
        string? schemaName,
        string objectKey,
        string? objectName,
        string grantee,
        IReadOnlyList<string> privileges,
        IReadOnlyList<string> columns,
        bool withGrantOption,
        string? forRole)
    {
        // The full set of privileges for the object is ALL PRIVILEGES however it is written (G-61). A column list has its own
        // full set, which is not compared here: a column grant keeps the privileges as written.
        if (columns.Count == 0)
        {
            privileges = GrantPrivilegeSets.Canonicalize(objectKey, privileges);
        }

        // The privileges and columns stay in the order written, which is what a publish emits (G-112); the key uses them sorted, so a
        // grant's identity does not depend on how its lists were ordered (the same key as before G-112).
        var name = $"{objectKey}->{grantee}";
        var keyParts = new List<string>
        {
            string.Join(",", privileges.Order(StringComparer.OrdinalIgnoreCase).Select(static privilege => privilege.Replace(' ', '_'))),
        };
        if (columns.Count > 0)
        {
            keyParts.Add($"columns={string.Join(",", columns.Order(StringComparer.Ordinal))}");
        }

        if (withGrantOption)
        {
            keyParts.Add("grant_option");
        }

        if (forRole is not null)
        {
            keyParts.Add($"for_role={forRole}");
        }

        return new GrantDefinition(
            DatabaseModel.CreateKey(schemaName, $"{name}:{string.Join(":", keyParts)}"),
            name,
            schemaName,
            objectKey,
            grantee,
            privileges,
            ReferencedTables: [],
            ReferencedColumns: [],
            ReferencedTypes: [],
            ReferencedFunctions: [],
            ResolvedTables: [],
            ResolvedColumns: [],
            ResolvedTypes: [],
            ResolvedFunctions: [])
        {
            ObjectName = objectName,
            Columns = columns,
            WithGrantOption = withGrantOption,
            ForRole = forRole,
        };
    }

    private static IReadOnlyList<string> NormalizePrivileges(IEnumerable<string> rawPrivileges)
    {
        var privileges = rawPrivileges
            .Select(static privilege => string.Equals(privilege, "ALL", StringComparison.OrdinalIgnoreCase)
                || string.Equals(privilege, AllPrivileges, StringComparison.OrdinalIgnoreCase)
                    ? AllPrivileges
                    : privilege.ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // In the order written (G-112); CreateDefinition sorts them for the key.
        return privileges.Length == 0 || privileges.Contains(AllPrivileges) ? [AllPrivileges] : privileges;
    }

    private static string Describe(GrantNode node)
        => node.Form switch
        {
            GrantForm.RoleMembership => "GRANT role",
            GrantForm.DefaultPrivileges => "ALTER DEFAULT PRIVILEGES",
            _ => "GRANT",
        };
}
