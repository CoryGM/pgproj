using Npgsql;
using PgProj.Deploy;
using PgProj.Diff;
using PgProj.Semantics;

namespace PgProj.Engine;

/// <summary>A statement of a deployment script that PostgreSQL lets only a superuser run, the object it is for, and why (G-265).</summary>
internal sealed record SuperuserStatement(ChangeNodeKind NodeKind, string NodeId, string Reason);

/// <summary>
/// The statements of a script that PostgreSQL lets only a superuser run (G-265, signed off by the user on 2026-10-13). <c>publish --execute</c> runs them only with
/// <c>--allow-superuser-statements</c>, which is consent for this run, not a role switch: without it the script is refused before anything runs, even for a superuser, and with
/// it a role that is not a superuser is refused up front instead of partway through. Known from the statement, not from the server's answer: any statement on an event trigger
/// (an event trigger's owner must be a superuser, G-127), a routine created or replaced <c>LEAKPROOF</c> (G-197), and a routine created or replaced in an untrusted language
/// (<c>c</c>, <c>internal</c>, or one the server marks untrusted, such as <c>plpython3u</c>; G-211). G-261 (a table's internal triggers, <c>TRIGGER ALL</c>) is on it; G-267 adds to it when it is done.
/// </summary>
internal static class SuperuserStatements
{
    /// <summary>The <c>publish</c> option that consents to running them.</summary>
    public const string OptionName = "--allow-superuser-statements";

    /// <summary>The languages no server trusts; a server's others are read from <c>pg_language</c>.</summary>
    private static readonly string[] AlwaysUntrusted = ["c", "internal"];

    /// <summary>
    /// Every statement of <paramref name="batch"/> that needs a superuser. <paramref name="untrustedLanguages"/> are the server's languages that are not trusted
    /// (<see cref="ReadUntrustedLanguagesAsync"/>); <c>c</c> and <c>internal</c> are untrusted whatever it says.
    /// </summary>
    public static IReadOnlyList<SuperuserStatement> Find(OperationBatch batch, IReadOnlyCollection<string> untrustedLanguages)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(untrustedLanguages);

        var untrusted = new HashSet<string>(untrustedLanguages.Concat(AlwaysUntrusted), StringComparer.OrdinalIgnoreCase);
        var found = new List<SuperuserStatement>();
        var seen = new HashSet<(ChangeNodeKind, string, string)>();
        void Add(ChangeNodeKind kind, string nodeId, string reason)
        {
            if (seen.Add((kind, nodeId, reason)))
            {
                found.Add(new SuperuserStatement(kind, nodeId, reason));
            }
        }

        foreach (var operation in batch.Operations)
        {
            if (operation.NodeKind == ChangeNodeKind.EventTrigger)
            {
                Add(operation.NodeKind, operation.NodeId, "an event trigger is created, changed, or dropped only by a superuser");
                continue;
            }

            if (operation is CreateOperation { Node: OwnerDefinition { ObjectKind: "event trigger" } owner })
            {
                Add(operation.NodeKind, owner.ObjectKey, "an event trigger's owner must be a superuser");
                continue;
            }

            if (operation is InternalTriggerStateOperation)
            {
                Add(operation.NodeKind, operation.NodeId, "only a superuser can enable or disable a table's internal (foreign-key) triggers (TRIGGER ALL)");
                continue;
            }

            if (operation is not CreateOperation create)
            {
                continue;
            }

            var (language, signature) = create.Node switch
            {
                FunctionDefinition function => (function.Language, function.Signature),
                ProcedureDefinition procedure => (procedure.Language, procedure.Signature),
                _ => (null, null),
            };

            if (signature?.IsLeakproof == true)
            {
                Add(operation.NodeKind, operation.NodeId, "only a superuser can make a routine LEAKPROOF");
            }

            if (language is { Length: > 0 } && untrusted.Contains(language.Trim()))
            {
                Add(operation.NodeKind, operation.NodeId, $"only a superuser can create a routine in the untrusted language {language.Trim()}");
            }
        }

        return found;
    }

    /// <summary>The server's languages that are not trusted (<c>pg_language.lanpltrusted</c> false), so that a routine in one needs a superuser.</summary>
    public static async Task<IReadOnlyList<string>> ReadUntrustedLanguagesAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var languages = new List<string>();
        await using var command = new NpgsqlCommand("select lanname from pg_catalog.pg_language where not lanpltrusted order by lanname", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            languages.Add(reader.GetString(0));
        }

        return languages;
    }

    /// <summary>True when the connected role is a superuser (<c>rolsuper</c>).</summary>
    public static async Task<bool> IsSuperuserAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var command = new NpgsqlCommand("select rolsuper from pg_catalog.pg_roles where rolname = current_user", connection);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    /// <summary>The statements, one per line, at most ten and a count of the rest.</summary>
    public static IReadOnlyList<string> Describe(IReadOnlyList<SuperuserStatement> statements)
    {
        ArgumentNullException.ThrowIfNull(statements);

        var lines = statements.Take(10).Select(static statement => $"[{statement.NodeKind}] {DatabaseModel.DisplayKey(statement.NodeId)}: {statement.Reason}").ToList();
        if (statements.Count > 10)
        {
            lines.Add($"... and {statements.Count - 10} more");
        }

        return lines;
    }

    /// <summary>
    /// The comment a written or printed script starts with when it has such statements, since a DBA may run it under another account: the statements, and that they need a
    /// superuser.
    /// </summary>
    public static string ScriptHeader(IReadOnlyList<SuperuserStatement> statements)
    {
        ArgumentNullException.ThrowIfNull(statements);

        return statements.Count == 0
            ? string.Empty
            : "-- pgproj: this script has statements that only a superuser can run (pgproj publish --execute runs them only with " + OptionName + "):"
                + Environment.NewLine
                + string.Join(Environment.NewLine, Describe(statements).Select(static line => "--   " + line))
                + Environment.NewLine + Environment.NewLine;
    }
}
