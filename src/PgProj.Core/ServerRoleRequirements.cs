using System.Text.RegularExpressions;
using Npgsql;
using PgProj.Semantics;

namespace PgProj.Core;

/// <summary>A role the project names that the server does not have, and what names it (G-147).</summary>
public sealed record MissingRole(string RoleName, IReadOnlyList<string> NamedBy);

/// <summary>
/// The roles a project's grants name that the server it is published to lacks (G-147). A role is cluster-wide, so pgproj reports roles and does
/// not model them (the owner's rule): it never creates, alters, or drops one. A grant, a role membership, or a
/// default privilege that names a missing role would stop the deployment with <c>42704</c> halfway through; <c>publish --execute</c> asks first,
/// and names each role and what names it. Unlike a tablespace, a role can be created inside the deployment's transaction, which is what a
/// pre-deploy script is for, so a role that script creates is not reported.
/// </summary>
public static partial class ServerRoleRequirements
{
    /// <summary>The names of the server's roles (<c>pg_roles</c>, readable by every role), including the predefined <c>pg_*</c> ones.</summary>
    public static async Task<IReadOnlyList<string>> ReadServerRolesAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var command = new NpgsqlCommand("select rolname from pg_catalog.pg_roles order by rolname", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var names = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    /// <summary>
    /// The roles named by <paramref name="model"/>'s grants (a grantee, either side of a role membership, the <c>FOR ROLE</c> of default
    /// privileges) that are not in <paramref name="serverRoles"/> and that <paramref name="preDeployScript"/> does not create, each with what
    /// names it, in name order. <c>PUBLIC</c> is not a role. A name is compared exactly, as PostgreSQL compares role names (G-206), as identifiers are everywhere else in
    /// the model, so a quoted mixed-case name that differs from the server's only in case is not reported (the server would still refuse it).
    /// A grant that <c>excludeGrants</c> or <c>excludeRoleMemberships</c> left out of the model is not looked at.
    /// </summary>
    public static IReadOnlyList<MissingRole> FindMissing(DatabaseModel model, IEnumerable<string> serverRoles, string? preDeployScript)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(serverRoles);

        var present = new HashSet<string>(serverRoles, PgProj.Parser.SqlIdentifiers.NameComparer);
        var namedBy = new Dictionary<string, SortedSet<string>>(PgProj.Parser.SqlIdentifiers.NameComparer);

        void Name(string? role, string by)
        {
            if (string.IsNullOrWhiteSpace(role) || string.Equals(role, "PUBLIC", StringComparison.OrdinalIgnoreCase) || present.Contains(role))
            {
                return;
            }

            if (!namedBy.TryGetValue(role, out var uses))
            {
                namedBy[role] = uses = new SortedSet<string>(StringComparer.Ordinal);
            }

            uses.Add(by);
        }

        foreach (var grant in model.Grants.Values)
        {
            var target = grant.ObjectKey ?? grant.Name;
            Name(grant.Grantee, target);
            Name(grant.ForRole, $"{target} (FOR ROLE)");
            if (target.StartsWith("role:", StringComparison.OrdinalIgnoreCase))
            {
                Name(target["role:".Length..], $"{target} (granted to {grant.Grantee})");
            }
        }

        // The role an object is to be owned by (G-146) must exist when the owner is set, like a grantee.
        foreach (var owner in model.Owners.Values)
        {
            Name(owner.OwnerRole, $"{owner.ObjectKind} {owner.ObjectKey} (owner)");
        }

        var script = StripComments(preDeployScript);
        var scriptCreatesRoles = script is not null && CreateRoleRegex().IsMatch(script);
        return namedBy
            .Where(pair => !(scriptCreatesRoles && Mentions(script!, pair.Key)))
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => new MissingRole(pair.Key, pair.Value.ToArray()))
            .ToArray();
    }

    /// <summary>
    /// Whether the script text names the role as a whole word, plain or in double quotes. Together with a <c>CREATE ROLE/USER/GROUP</c>
    /// anywhere in the script this is what "the pre-deploy script creates it" means: the script may build the statement from a list of names
    /// (<c>EXECUTE format('CREATE ROLE %I', r)</c>), so the name is looked for apart from the statement.
    /// </summary>
    private static bool Mentions(string script, string role)
        => Regex.IsMatch(script, $@"(?<![\w$]){Regex.Escape(role)}(?![\w$])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string? StripComments(string? script)
        => script is null ? null : BlockCommentRegex().Replace(LineCommentRegex().Replace(script, string.Empty), string.Empty);

    [GeneratedRegex(@"\bCREATE\s+(ROLE|USER|GROUP)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreateRoleRegex();

    [GeneratedRegex(@"--[^\r\n]*")]
    private static partial Regex LineCommentRegex();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockCommentRegex();
}
