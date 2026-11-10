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
            if (DeclareRevokedDefaults(node, diagnostics, location) is { } revokedDefaults)
            {
                return revokedDefaults;
            }

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

    private static readonly string[] ObjectKinds = ["schema", "table", "sequence", "function", "procedure", "type", "domain"];

    /// <summary>
    /// <c>REVOKE ... FROM PUBLIC</c> of a privilege PostgreSQL gives <c>PUBLIC</c> by default, read as "without the default": one <see
    /// cref="GrantDefinition.RevokesDefault"/> definition per object or rule. Null for every other <c>REVOKE</c>, which stays a warning.
    /// <list type="bullet">
    /// <item><c>REVOKE USAGE|ALL ON TYPE|DOMAIN t FROM PUBLIC</c> (G-337, option (B), approved by the user on 2026-11-09), the form <c>pg_dump</c> writes.</item>
    /// <item><c>REVOKE EXECUTE|ALL ON FUNCTION|PROCEDURE f(...) FROM PUBLIC</c> (G-338, the same option for routines, approved on 2026-11-10).</item>
    /// <item><c>ALTER DEFAULT PRIVILEGES [FOR ROLE r] REVOKE EXECUTE|ALL ON FUNCTIONS|ROUTINES FROM PUBLIC</c>, and <c>... USAGE|ALL ON TYPES ...</c> (G-338,
    /// part (i)): a rule that new routines (or types) are created without the default. With <c>IN SCHEMA</c> PostgreSQL makes no rule of it (a per-schema rule only
    /// adds to the global one), so it is a warning that says so.</item>
    /// </list>
    /// A grantee other than <c>PUBLIC</c> in the same statement is not read (a project states what it grants, and pgproj revokes the rest), and says so.
    /// </summary>
    private static List<GrantDefinition>? DeclareRevokedDefaults(GrantNode node, ICollection<SemanticDiagnostic> diagnostics, SemanticLocation? location)
    {
        var defaultPrivilege = node.ObjectKind switch
        {
            "type" or "domain" => "USAGE",
            "function" or "procedure" => "EXECUTE",
            _ => null,
        };
        var revokesDefault = defaultPrivilege is not null
            && node.Form is GrantForm.Object or GrantForm.DefaultPrivileges
            && (node.Form == GrantForm.Object || node.ObjectKind is "type" or "function")
            && !node.WithGrantOption
            && node.Privileges.All(privilege => privilege.Columns.Count == 0
                && (privilege.Name == defaultPrivilege || privilege.Name is "ALL" or AllPrivileges));
        if (!revokesDefault || !node.Grantees.Any(IsPublic))
        {
            return null;
        }

        var others = node.Grantees.Where(static grantee => !IsPublic(grantee)).Distinct(StringComparer.Ordinal).ToArray();
        if (others.Length > 0)
        {
            diagnostics.Add(SemanticDiagnostics.Warning(
                $"REVOKE ... FROM {string.Join(", ", others)} is not modeled and is ignored: a project states the privileges it wants with GRANT, and pgproj revokes the rest. Only REVOKE ... FROM PUBLIC is read on a type, domain, function, or procedure, and in ALTER DEFAULT PRIVILEGES on types and functions.",
                location,
                nameof(DeclarationPass)));
        }

        if (node.Form == GrantForm.DefaultPrivileges)
        {
            if (node.Schemas.Count > 0)
            {
                diagnostics.Add(SemanticDiagnostics.Warning(
                    "ALTER DEFAULT PRIVILEGES IN SCHEMA ... REVOKE ... FROM PUBLIC has no effect and is ignored: a rule in one schema only adds to the rule for every schema, so PostgreSQL keeps PUBLIC's default. Write it without IN SCHEMA.",
                    location,
                    nameof(DeclarationPass)));
                return [];
            }

            IReadOnlyList<string?> roles = node.Roles.Count == 0 ? [null] : node.Roles.Cast<string?>().ToArray();
            return roles
                .Select(role => CreateDefinition(null, $"default {node.ObjectKind}s", null, "PUBLIC", [AllPrivileges], columns: [], withGrantOption: false, forRole: role, revokesDefault: true))
                .ToList();
        }

        return node.Targets
            .Select(target =>
            {
                // A domain is a type: ON TYPE names one too, and both are one revoke (G-337).
                if (defaultPrivilege == "USAGE")
                {
                    return CreateDefinition(target.SchemaName, $"type:{DatabaseModel.CreateKey(target.SchemaName, target.Name)}", target.Name, "PUBLIC", [AllPrivileges], columns: [], withGrantOption: false, forRole: null, revokesDefault: true);
                }

                var routineName = GrantExpansion.RoutineObjectName(target.Name, target.ArgumentTypes ?? []);
                return CreateDefinition(target.SchemaName, $"{node.ObjectKind}:{DatabaseModel.CreateKey(target.SchemaName, routineName)}", routineName, "PUBLIC", [AllPrivileges], columns: [], withGrantOption: false, forRole: null, revokesDefault: true)
                    with { ObjectArgumentTypes = target.ArgumentTypes ?? [] };
            })
            .ToList();
    }

    private static bool IsPublic(string grantee) => string.Equals(grantee, "PUBLIC", StringComparison.OrdinalIgnoreCase);
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
                case "type":
                case "domain":
                    // A domain is a type: ON TYPE names one too, and both are one grant (G-337).
                    objectName = target.Name;
                    objectKey = $"type:{DatabaseModel.CreateKey(schemaName, target.Name)}";
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
        string? forRole,
        bool revokesDefault = false)
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

        if (revokesDefault)
        {
            keyParts.Add("revokes_default");
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
            RevokesDefault = revokesDefault,
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
