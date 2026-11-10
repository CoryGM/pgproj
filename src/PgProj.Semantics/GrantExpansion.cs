
namespace PgProj.Semantics;

/// <summary>
/// One privilege on one object for one grantee, the smallest thing a grant can say (G-65). Grants are written at several
/// granularities (<c>ALL TABLES IN SCHEMA</c>, one table, one column) and PostgreSQL records them at the finest, so two grants
/// are the same when they expand to the same atoms. Names are compared exactly, as PostgreSQL compares them (G-206), like the rest of the model; <c>PUBLIC</c> is a keyword.
/// </summary>
public readonly record struct GrantAtom(string Target, string Grantee, string Privilege, bool WithGrantOption)
{
    /// <summary>
    /// <c>EXECUTE</c> for <c>PUBLIC</c> on a function or procedure, and <c>USAGE</c> for <c>PUBLIC</c> on the <c>public</c> schema
    /// specifically, are PostgreSQL's own defaults: present on every routine, and on every database's <c>public</c> schema, whether
    /// or not a project mentions them, so they are neither wanted nor removable (G-61, G-86). Unlike the routine case, the schema
    /// default applies only to the schema literally named <c>public</c> — a project-created schema gets no such default, so a grant
    /// on any other schema is a real, removable grant.
    /// </summary>
    public bool IsImplicit
        => (Grantee == "public"
            && Privilege == "EXECUTE"
            && !WithGrantOption
            && (Target.StartsWith("function:", StringComparison.Ordinal) || Target.StartsWith("procedure:", StringComparison.Ordinal)))
        // USAGE for PUBLIC on a type or domain is PostgreSQL's default too (G-337), but one a project can take away: whether a type has it is decided by a
        // GrantDefinition.RevokesDefault definition, compared by its key, not by atoms.
        || (Grantee == "public"
            && Privilege == "USAGE"
            && !WithGrantOption
            && Target.StartsWith("type:", StringComparison.Ordinal))
        || (Grantee == "public"
            && Privilege == "USAGE"
            && !WithGrantOption
            && Target == "schema:public")
        // PUBLIC's USAGE in a rule on types and its EXECUTE in one on functions, with no IN SCHEMA, are what PostgreSQL gives every new type and routine
        // anyway: a stated one asks for nothing, and the live read leaves the entry out of the list PostgreSQL stores (G-340). In one schema it is a rule.
        || (Grantee == "public"
            && !WithGrantOption
            && ((Privilege == "USAGE" && IsGlobalDefault(Target, "default types")) || (Privilege == "EXECUTE" && IsGlobalDefault(Target, "default functions"))));

    private static bool IsGlobalDefault(string target, string kind)
        => target == kind || target.StartsWith(kind + " for role:", StringComparison.Ordinal);

    /// <summary>Membership of one role in another. It is only ever added: removing it is a destructive change that is not made.</summary>
    public bool IsRoleMembership => Target.StartsWith("role:", StringComparison.Ordinal);
}

/// <summary>Expands <see cref="GrantDefinition"/>s into <see cref="GrantAtom"/>s against the objects of a model (G-65).</summary>
public static class GrantExpansion
{
    private const string DefaultSchema = "public";
    private const string AllPrivileges = "ALL PRIVILEGES";

    private static readonly string[] TablePrivileges = ["DELETE", "INSERT", "REFERENCES", "SELECT", "TRIGGER", "TRUNCATE", "UPDATE"];
    private static readonly string[] ColumnPrivileges = ["INSERT", "REFERENCES", "SELECT", "UPDATE"];
    private static readonly string[] SequencePrivileges = ["SELECT", "UPDATE", "USAGE"];
    private static readonly string[] RoutinePrivileges = ["EXECUTE"];
    private static readonly string[] SchemaPrivileges = ["CREATE", "USAGE"];
    private static readonly string[] TypePrivileges = ["USAGE"];
    private static readonly string[] MembershipPrivileges = ["MEMBER"];


    /// <summary>The name of a routine as its grants and its atoms identify it: <c>f(int4,text)</c>.</summary>
    public static string RoutineObjectName(string name, IEnumerable<string?> argumentTypes)
        => $"{name}({string.Join(",", argumentTypes.Select(RoutineTypeNames.Canonicalize))})";

    /// <summary>The <see cref="RoutineObjectName(string, IEnumerable{string?})"/> of a declared function.</summary>
    public static string RoutineObjectName(FunctionDefinition function)
        => RoutineObjectName(function.Name, ArgumentTypes(function.Signature));

    /// <summary>The <see cref="RoutineObjectName(string, IEnumerable{string?})"/> of a declared procedure.</summary>
    public static string RoutineObjectName(ProcedureDefinition procedure)
        => RoutineObjectName(procedure.Name, ArgumentTypes(procedure.Signature));

    /// <summary>
    /// The atoms <paramref name="grant"/> stands for in <paramref name="model"/>, which supplies the objects an <c>ALL ... IN SCHEMA</c>
    /// grant covers. Default privileges without <c>FOR ROLE</c> are the connected role's, so <c>FOR ROLE</c> naming
    /// <paramref name="connectedRole"/> is the same grant as none.
    /// </summary>
    public static IReadOnlyList<GrantAtom> Expand(GrantDefinition grant, DatabaseModel model, string? connectedRole = null)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(model);

        // A revoked default (G-337) gives nothing: it is compared by its key.
        if (string.IsNullOrWhiteSpace(grant.Grantee) || string.IsNullOrWhiteSpace(grant.ObjectKey) || grant.RevokesDefault)
        {
            return [];
        }

        // A role is named exactly, as PostgreSQL names it (G-206); PUBLIC is a keyword, so it is one spelling however it was written.
        var grantee = string.Equals(grant.Grantee, "public", StringComparison.OrdinalIgnoreCase) ? "public" : grant.Grantee;
        var atoms = new List<GrantAtom>();
        foreach (var (target, fullSet) in Targets(grant, model, connectedRole))
        {
            foreach (var privilege in PrivilegesFor(grant.Privileges, fullSet))
            {
                atoms.Add(new GrantAtom(target, grantee, privilege, grant.WithGrantOption));
            }
        }

        return atoms;
    }

    /// <summary>The kind of object a default-privilege rule's key names (<c>default tables in schema:s</c> is <c>tables</c>; G-339).</summary>
    public static string DefaultPrivilegeKind(string objectKey)
    {
        ArgumentNullException.ThrowIfNull(objectKey);

        var rest = objectKey.StartsWith("default ", StringComparison.Ordinal) ? objectKey["default ".Length..] : objectKey;
        var inSchema = rest.IndexOf(" in schema:", StringComparison.Ordinal);
        return inSchema < 0 ? rest : rest[..inSchema];
    }

    /// <summary>
    /// Whether <paramref name="project"/> has a rule that creates objects like the one <paramref name="objectKey"/> names (<c>type:</c>, <c>function:</c>, or
    /// <c>procedure:</c>) without <c>PUBLIC</c>'s default (<c>ALTER DEFAULT PRIVILEGES [FOR ROLE r] REVOKE ... ON TYPES|FUNCTIONS FROM PUBLIC</c>; G-338, part (i),
    /// tolerant like G-339's option (c)): then an object without the default is allowed, not required, since PostgreSQL applies the rule only to what is created after
    /// it. A rule of any <c>FOR ROLE</c> counts.
    /// </summary>
    public static bool RevokesDefaultByRule(DatabaseModel project, string objectKey)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(objectKey);

        var kind = objectKey.StartsWith("type:", StringComparison.Ordinal) ? "default types"
            : objectKey.StartsWith("function:", StringComparison.Ordinal) || objectKey.StartsWith("procedure:", StringComparison.Ordinal) ? "default functions"
            : null;
        return kind is not null && project.Grants.Values.Any(grant => grant.RevokesDefault && grant.ObjectKey == kind);
    }

    /// <summary>
    /// What the default-privilege rules of <paramref name="project"/> may give an object (G-339, option (c), the user's decision of 2026-11-09): a privilege a rule
    /// would give an object of its kind, globally or in the object's schema, is allowed and not required, since PostgreSQL applies a rule only to what is created
    /// after it and pgproj cannot know which existing objects were. A rule of any <c>FOR ROLE</c> counts. <c>TABLES</c> covers tables and views (whose grants are
    /// <c>table:</c>), <c>FUNCTIONS</c> functions and procedures, <c>TYPES</c> types and domains, <c>SCHEMAS</c> schemas.
    /// </summary>
    public static Func<GrantAtom, bool> DefaultPrivilegeTolerance(DatabaseModel project)
    {
        ArgumentNullException.ThrowIfNull(project);

        var given = new HashSet<(string Kind, string? Schema, string Grantee, string Privilege, bool WithGrantOption)>();
        foreach (var rule in project.Grants.Values.Where(static grant => grant.ObjectKey?.StartsWith("default ", StringComparison.Ordinal) == true && !grant.RevokesDefault && !string.IsNullOrWhiteSpace(grant.Grantee)))
        {
            var kind = DefaultPrivilegeKind(rule.ObjectKey!);
            var fullSet = kind switch
            {
                "sequences" => SequencePrivileges,
                "functions" => RoutinePrivileges,
                "types" => TypePrivileges,
                "schemas" => SchemaPrivileges,
                _ => TablePrivileges,
            };
            var grantee = string.Equals(rule.Grantee, "public", StringComparison.OrdinalIgnoreCase) ? "public" : rule.Grantee!;
            foreach (var privilege in PrivilegesFor(rule.Privileges, fullSet))
            {
                given.Add((kind, rule.SchemaName, grantee, privilege, rule.WithGrantOption));
            }
        }

        if (given.Count == 0)
        {
            return static _ => false;
        }

        return atom =>
        {
            var separator = atom.Target.IndexOf(':', StringComparison.Ordinal);
            if (separator < 0)
            {
                return false;
            }

            var kind = atom.Target[..separator] switch
            {
                "table" => "tables",
                "sequence" => "sequences",
                "function" or "procedure" => "functions",
                "type" => "types",
                "schema" => "schemas",
                _ => null,
            };
            if (kind is null)
            {
                return false;
            }

            // A schema's own grant names the schema; any other object's target is "kind:schema.name".
            var rest = atom.Target[(separator + 1)..];
            var dot = rest.IndexOf('.', StringComparison.Ordinal);
            var schema = kind == "schemas" ? null : dot < 0 ? null : rest[..dot];
            return given.Contains((kind, null, atom.Grantee, atom.Privilege, atom.WithGrantOption))
                || (schema is not null && given.Contains((kind, schema, atom.Grantee, atom.Privilege, atom.WithGrantOption)));
        };
    }

    private static IEnumerable<(string Target, IReadOnlyList<string> FullSet)> Targets(GrantDefinition grant, DatabaseModel model, string? connectedRole)
    {
        var objectKey = grant.ObjectKey!;
        var schemaName = grant.SchemaName ?? DefaultSchema;

        if (objectKey.StartsWith("schema:", StringComparison.OrdinalIgnoreCase))
        {
            yield return ($"schema:{objectKey["schema:".Length..]}", SchemaPrivileges);
        }
        else if (objectKey.StartsWith("all tables in schema:", StringComparison.OrdinalIgnoreCase))
        {
            // ALL TABLES covers tables, views, and materialized views alike.
            foreach (var relation in RelationTargets(model).Where(target => InSchema(target, "table", schemaName)))
            {
                yield return (relation, TablePrivileges);
            }
        }
        else if (objectKey.StartsWith("all sequences in schema:", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var sequence in model.Sequences.Values.Where(sequence => SameSchema(sequence.SchemaName, schemaName)))
            {
                yield return (ObjectTarget("sequence", sequence.SchemaName, sequence.Name), SequencePrivileges);
            }
        }
        else if (objectKey.StartsWith("all functions in schema:", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var function in model.Functions.Values.Where(function => SameSchema(function.SchemaName, schemaName)))
            {
                yield return (ObjectTarget("function", function.SchemaName, RoutineObjectName(function)), RoutinePrivileges);
            }
        }
        else if (objectKey.StartsWith("default ", StringComparison.OrdinalIgnoreCase))
        {
            var role = grant.ForRole is null || string.Equals(grant.ForRole, connectedRole, StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : $" for role:{grant.ForRole}";
            var fullSet = objectKey.StartsWith("default sequences", StringComparison.OrdinalIgnoreCase) ? SequencePrivileges
                : objectKey.StartsWith("default functions", StringComparison.OrdinalIgnoreCase) ? RoutinePrivileges
                : objectKey.StartsWith("default types", StringComparison.OrdinalIgnoreCase) ? TypePrivileges
                : objectKey.StartsWith("default schemas", StringComparison.OrdinalIgnoreCase) ? SchemaPrivileges
                : TablePrivileges;
            yield return (objectKey + role, fullSet);
        }
        else if (objectKey.StartsWith("role:", StringComparison.OrdinalIgnoreCase))
        {
            yield return (objectKey, MembershipPrivileges);
        }
        else if (objectKey.StartsWith("table:", StringComparison.OrdinalIgnoreCase) && grant.ObjectName is { } tableName)
        {
            if (grant.Columns.Count == 0)
            {
                yield return (ObjectTarget("table", grant.SchemaName, tableName), TablePrivileges);
                yield break;
            }

            foreach (var column in grant.Columns)
            {
                yield return ($"column:{schemaName}.{tableName}.{column}", ColumnPrivileges);
            }
        }
        else if (objectKey.StartsWith("sequence:", StringComparison.OrdinalIgnoreCase) && grant.ObjectName is { } sequenceName)
        {
            yield return (ObjectTarget("sequence", grant.SchemaName, sequenceName), SequencePrivileges);
        }
        else if (objectKey.StartsWith("function:", StringComparison.OrdinalIgnoreCase) && grant.ObjectName is { } functionName)
        {
            yield return (ObjectTarget("function", grant.SchemaName, functionName), RoutinePrivileges);
        }
        else if (objectKey.StartsWith("procedure:", StringComparison.OrdinalIgnoreCase) && grant.ObjectName is { } procedureName)
        {
            yield return (ObjectTarget("procedure", grant.SchemaName, procedureName), RoutinePrivileges);
        }
        else if (objectKey.StartsWith("type:", StringComparison.OrdinalIgnoreCase) && grant.ObjectName is { } typeName)
        {
            // A type or a domain (G-337).
            yield return (ObjectTarget("type", grant.SchemaName, typeName), TypePrivileges);
        }
    }

    private static IEnumerable<string> PrivilegesFor(IReadOnlyList<string> privileges, IReadOnlyList<string> fullSet)
    {
        var expanded = privileges.Count == 0 || privileges.Any(static privilege => string.Equals(privilege, AllPrivileges, StringComparison.OrdinalIgnoreCase))
            ? fullSet
            : privileges.Select(static privilege => privilege.ToUpperInvariant()).ToArray();

        // MAINTAIN exists from PostgreSQL 17 and is part of ALL there, so a project written for an earlier server and a server
        // that is 17 disagree about it; it is not compared (as in the other privilege sets).
        return expanded.Where(static privilege => privilege != "MAINTAIN").Distinct(StringComparer.Ordinal);
    }

    private static IEnumerable<string> RelationTargets(DatabaseModel model)
        => model.Tables.Values.Select(static table => ObjectTarget("table", table.SchemaName, table.Name))
            .Concat(model.Views.Values.Select(static view => ObjectTarget("table", view.SchemaName, view.Name)));

    private static string ObjectTarget(string kind, string? schemaName, string name)
        => $"{kind}:{schemaName ?? DefaultSchema}.{name}";

    private static bool InSchema(string target, string kind, string schemaName)
        => target.StartsWith($"{kind}:{schemaName}.", StringComparison.Ordinal);

    private static bool SameSchema(string? candidate, string schemaName)
        => string.Equals(candidate ?? DefaultSchema, schemaName, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string?> ArgumentTypes(PgProj.Parser.RoutineSignature? signature)
        => signature is null
            ? []
            : signature.Parameters
                .Where(static parameter => parameter.IsArgument)
                .Select(static parameter => parameter.DataType);
}
