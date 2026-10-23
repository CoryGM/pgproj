
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
        || (Grantee == "public"
            && Privilege == "USAGE"
            && !WithGrantOption
            && Target == "schema:public");

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

        if (string.IsNullOrWhiteSpace(grant.Grantee) || string.IsNullOrWhiteSpace(grant.ObjectKey))
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
