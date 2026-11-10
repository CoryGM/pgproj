using PgProj.Deploy;
using PgProj.Semantics;

namespace PgProj.Engine;

/// <summary>
/// The statements of a script that give <c>PUBLIC</c>, every role, a privilege on an object the database already has (G-337, the user's decision of 2026-11-09):
/// a grant to <c>PUBLIC</c> the project adds, and a type's or domain's <c>USAGE</c> given back because the project does not revoke PostgreSQL's default. Neither
/// takes anything away, so <c>--allow-destructive</c> does not cover them, but each widens access to something that exists, which a warning names. A grant on an
/// object the script creates, one the object already had, and one put back on an object dropped and created again are not named.
/// </summary>
internal static class PublicGrants
{
    public static IReadOnlyList<string> Find(OperationBatch batch, DatabaseModel live)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(live);

        var found = new List<string>();
        foreach (var operation in batch.Operations)
        {
            var widens = operation switch
            {
                DropOperation { Node: GrantDefinition { RevokesDefault: true } } => true,
                CreateOperation { Node: GrantDefinition { RevokesDefault: false } grant, IsRestored: false }
                    => IsPublic(grant.Grantee) && !live.Grants.ContainsKey(grant.Key) && Exists(grant, live),
                _ => false,
            };

            if (widens)
            {
                found.Add(operation.GenerateSql().Trim());
            }
        }

        return found;
    }

    public static string Describe(IReadOnlyList<string> statements)
        => $"Warning: this publish gives PUBLIC (every role) privileges on objects the database already has:{Environment.NewLine}"
            + string.Join(Environment.NewLine, statements.Select(static statement => "  " + statement))
            + $"{Environment.NewLine}A type or domain gets PUBLIC's USAGE back when the project does not revoke it; to keep it revoked, write REVOKE USAGE ON TYPE ... FROM PUBLIC in the project.";

    private static bool IsPublic(string? grantee) => string.Equals(grantee, "PUBLIC", StringComparison.OrdinalIgnoreCase);

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
