using PgProj.Deploy;
using PgProj.Diff;
using PgProj.Semantics;

namespace PgProj.Engine;

/// <summary>Why a statement of a script gives <c>PUBLIC</c> a privilege it does not have now.</summary>
internal enum PublicGrantReason
{
    /// <summary>PostgreSQL's default on a type, domain, function, procedure, or the public schema is given back: the database has it revoked and the project does not (G-337, G-338, G-341).</summary>
    DefaultGivenBack,

    /// <summary>A rule for new types or functions is given its default back: the database revokes it and the project does not (G-338, part (i)).</summary>
    RuleGivenBack,

    /// <summary>The project grants it on something that exists.</summary>
    Granted,
}

/// <summary>
/// One statement that gives <c>PUBLIC</c> a privilege: what it is on, in words (<paramref name="Subject"/>), what every role can then do (<paramref name="Effect"/>),
/// and, for a default given back, the statement that keeps it revoked (<paramref name="KeepRevoked"/>).
/// </summary>
internal sealed record PublicGrant(string Statement, PublicGrantReason Reason, string Subject, string Effect, string? KeepRevoked);

/// <summary>
/// The statements of a script that give <c>PUBLIC</c>, every role, a privilege it does not have now (G-337 and G-338, the user's decisions of 2026-11-09 and
/// 2026-11-10): a grant to <c>PUBLIC</c> the project adds on an object the database already has; PostgreSQL's default on a type, domain, function, procedure, or the public schema (G-341) given
/// back because the project does not revoke it; and a rule for new types or functions given its default back. None takes anything away, so <c>--allow-destructive</c>
/// does not cover them, but each widens access, which the warning spells out (the user: "make sure the warning is VERY explicit"). A grant on an object the script
/// creates, one the object already had, and one put back on an object dropped and created again are not named.
/// </summary>
internal static class PublicGrants
{
    public static IReadOnlyList<PublicGrant> Find(OperationBatch batch, DatabaseModel live)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(live);

        var found = new List<PublicGrant>();
        foreach (var operation in batch.Operations)
        {
            var grant = operation switch
            {
                DropOperation { Node: GrantDefinition { RevokesDefault: true } revoked } => revoked,
                CreateOperation { Node: GrantDefinition { RevokesDefault: false } added, IsRestored: false }
                    when IsPublic(added.Grantee) && !live.Grants.ContainsKey(added.Key) && Exists(added, live) => added,
                _ => null,
            };

            if (grant is null)
            {
                continue;
            }

            var statement = operation.GenerateSql().Trim();
            if (!grant.RevokesDefault)
            {
                found.Add(new PublicGrant(statement, PublicGrantReason.Granted, Subject(grant), $"every role gets {string.Join(", ", grant.Privileges)}{(grant.WithGrantOption ? ", and can grant it to others" : string.Empty)}", null));
                continue;
            }

            var keepRevoked = new CreateOperation { NodeId = grant.Key, NodeKind = ChangeNodeKind.Grant, Node = grant }.GenerateSql().Trim();
            if (grant.ObjectName is null)
            {
                var kind = GrantExpansion.DefaultPrivilegeKind(grant.ObjectKey ?? string.Empty);
                var forRole = grant.ForRole is null ? string.Empty : $" by {grant.ForRole}";
                found.Add(new PublicGrant(statement, PublicGrantReason.RuleGivenBack, $"every {(kind == "types" ? "type and domain" : "function and procedure")} created{forRole} from now on",
                    kind == "types" ? "every role can use it" : "every role can execute it", keepRevoked));
                continue;
            }

            var objectKey = grant.ObjectKey ?? string.Empty;
            var effect = objectKey.StartsWith("type:", StringComparison.Ordinal) ? "every role can use it"
                // The public schema's USAGE (G-341): what is in it becomes reachable by name, each object still behind its own privileges.
                : objectKey.StartsWith("schema:", StringComparison.Ordinal) ? "every role can look up the objects in it, and use each one its own privileges allow"
                : "every role can execute it";
            var subject = Subject(grant) + (IsSecurityDefiner(grant, live) ? " (SECURITY DEFINER: it runs with its owner's privileges, whoever calls it)" : string.Empty);
            found.Add(new PublicGrant(statement, PublicGrantReason.DefaultGivenBack, subject, effect, keepRevoked));
        }

        return found;
    }

    /// <summary>The warning: each statement, why it is there, what every role can then do, and how to keep the privilege revoked.</summary>
    public static string Describe(IReadOnlyList<PublicGrant> grants)
    {
        ArgumentNullException.ThrowIfNull(grants);

        var nl = Environment.NewLine;
        var text = new System.Text.StringBuilder()
            .Append("WARNING: this publish gives PUBLIC privileges it does not have now. PUBLIC is every role, including roles created later:").Append(nl)
            .Append("after this publish, every role has the privileges listed below. Read each one before you run it.").Append(nl);

        var givenBack = grants.Where(static grant => grant.Reason == PublicGrantReason.DefaultGivenBack).ToArray();
        if (givenBack.Length > 0)
        {
            text.Append(nl).Append("PostgreSQL's default is given back, because the database has it revoked and the project does not revoke it:").Append(nl);
            AppendItems(text, givenBack);
            AppendKeepRevoked(text, givenBack, "  pgproj init writes these REVOKE statements when it scaffolds a project from the database.");
        }

        var rules = grants.Where(static grant => grant.Reason == PublicGrantReason.RuleGivenBack).ToArray();
        if (rules.Length > 0)
        {
            text.Append(nl).Append("A default-privileges rule that revokes PUBLIC's default is removed, because the project does not have it:").Append(nl);
            AppendItems(text, rules);
            AppendKeepRevoked(text, rules, null);
        }

        var granted = grants.Where(static grant => grant.Reason == PublicGrantReason.Granted).ToArray();
        if (granted.Length > 0)
        {
            text.Append(nl).Append("The project grants PUBLIC a privilege on something the database already has:").Append(nl);
            AppendItems(text, granted);
            text.Append("To not give it, remove the GRANT ... TO PUBLIC from the project.").Append(nl);
        }

        return text.Append(nl).Append("If any of this is not what you want, do not run this publish.").ToString();
    }

    private static void AppendItems(System.Text.StringBuilder text, IEnumerable<PublicGrant> grants)
    {
        foreach (var grant in grants)
        {
            text.Append("  - ").Append(grant.Subject).Append(": ").Append(grant.Effect).Append('.').Append(Environment.NewLine)
                .Append("      ").Append(grant.Statement).Append(Environment.NewLine);
        }
    }

    private static void AppendKeepRevoked(System.Text.StringBuilder text, IEnumerable<PublicGrant> grants, string? note)
    {
        text.Append("To keep them revoked, add to the project:").Append(Environment.NewLine);
        foreach (var statement in grants.Select(static grant => grant.KeepRevoked).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            text.Append("      ").Append(statement).Append(Environment.NewLine);
        }

        if (note is not null)
        {
            text.Append(note).Append(Environment.NewLine);
        }
    }

    private static bool IsPublic(string? grantee) => string.Equals(grantee, "PUBLIC", StringComparison.OrdinalIgnoreCase);

    /// <summary>What a grant is on, in words: <c>function app.f(int4)</c>, <c>all tables in schema app</c>.</summary>
    private static string Subject(GrantDefinition grant)
    {
        var objectKey = grant.ObjectKey ?? string.Empty;
        if (objectKey.StartsWith("all ", StringComparison.Ordinal))
        {
            return objectKey.Replace(" in schema:", " in schema ", StringComparison.Ordinal);
        }

        var separator = objectKey.IndexOf(':', StringComparison.Ordinal);
        return separator < 0 ? objectKey : $"{objectKey[..separator]} {objectKey[(separator + 1)..]}";
    }

    private static bool IsSecurityDefiner(GrantDefinition grant, DatabaseModel live)
    {
        var objectKey = grant.ObjectKey ?? string.Empty;
        bool Named(string? schemaName) => string.Equals(schemaName, grant.SchemaName, StringComparison.Ordinal);
        if (objectKey.StartsWith("function:", StringComparison.Ordinal))
        {
            return live.Functions.Values.Any(function => Named(function.SchemaName) && GrantExpansion.RoutineObjectName(function) == grant.ObjectName && function.Signature?.IsSecurityDefiner == true);
        }

        return objectKey.StartsWith("procedure:", StringComparison.Ordinal)
            && live.Procedures.Values.Any(procedure => Named(procedure.SchemaName) && GrantExpansion.RoutineObjectName(procedure) == grant.ObjectName && procedure.Signature?.IsSecurityDefiner == true);
    }

    /// <summary>True when the database already has the object (or, for <c>ALL ... IN SCHEMA</c>, an object of that kind in the schema) the grant is on.</summary>
    private static bool Exists(GrantDefinition grant, DatabaseModel live)
    {
        var objectKey = grant.ObjectKey ?? string.Empty;
        var schemaName = grant.SchemaName;
        bool InSchema(string? candidate) => string.Equals(candidate, schemaName, StringComparison.Ordinal);
        string Key() => DatabaseModel.CreateKey(schemaName, grant.ObjectName!);

        if (objectKey.StartsWith("schema:", StringComparison.Ordinal))
        {
            var schema = objectKey["schema:".Length..];
            return live.SchemaDefinitions.Values.Any(definition => string.Equals(definition.Name, schema, StringComparison.Ordinal));
        }

        if (objectKey.StartsWith("all tables in schema:", StringComparison.Ordinal))
        {
            return live.Tables.Values.Any(table => InSchema(table.SchemaName)) || live.Views.Values.Any(view => InSchema(view.SchemaName));
        }

        if (objectKey.StartsWith("all sequences in schema:", StringComparison.Ordinal))
        {
            return live.Sequences.Values.Any(sequence => InSchema(sequence.SchemaName));
        }

        if (objectKey.StartsWith("all functions in schema:", StringComparison.Ordinal))
        {
            return live.Functions.Values.Any(function => InSchema(function.SchemaName));
        }

        if (grant.ObjectName is null)
        {
            // Default privileges and role memberships are on no existing object.
            return false;
        }

        return objectKey switch
        {
            _ when objectKey.StartsWith("table:", StringComparison.Ordinal) => live.Tables.ContainsKey(Key()) || live.Views.ContainsKey(Key()),
            _ when objectKey.StartsWith("sequence:", StringComparison.Ordinal) => live.Sequences.ContainsKey(Key()),
            _ when objectKey.StartsWith("type:", StringComparison.Ordinal) => live.Types.ContainsKey(Key()),
            _ when objectKey.StartsWith("function:", StringComparison.Ordinal)
                => live.Functions.Values.Any(function => InSchema(function.SchemaName) && GrantExpansion.RoutineObjectName(function) == grant.ObjectName),
            _ when objectKey.StartsWith("procedure:", StringComparison.Ordinal)
                => live.Procedures.Values.Any(procedure => InSchema(procedure.SchemaName) && GrantExpansion.RoutineObjectName(procedure) == grant.ObjectName),
            _ => false,
        };
    }
}
