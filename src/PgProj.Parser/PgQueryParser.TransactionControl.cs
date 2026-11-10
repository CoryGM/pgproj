using System.Text.Json;

namespace PgProj.Parser;

public sealed partial class PgQueryParser
{
    /// <summary>The <see cref="SqlDiagnostic.Source"/> of what <see cref="FindTransactionControl"/> reports.</summary>
    public const string TransactionControlSource = "TransactionControl";

    /// <summary>
    /// The statements of a deploy script that do not belong in the transaction pgproj runs a publish in (G-335, the user's decisions of 2026-11-09), each at
    /// its statement: one that ends the transaction (<c>COMMIT</c>, <c>END</c>, <c>ROLLBACK</c>, <c>ABORT</c>, <c>PREPARE TRANSACTION</c>,
    /// <c>COMMIT PREPARED</c>, <c>ROLLBACK PREPARED</c>), so what follows would run outside it, is an error; one PostgreSQL refuses inside a transaction
    /// block (as checked on PostgreSQL 14 and 18) is an error; <c>BEGIN</c> or <c>START TRANSACTION</c>, which the server only warns about there, is a
    /// warning. A savepoint (<c>SAVEPOINT</c>, <c>RELEASE</c>, <c>ROLLBACK TO</c>) is not reported, nor anything inside a <c>DO</c> block or a routine
    /// (the server refuses a <c>COMMIT</c> there itself). Only the top-level statements are read; a script that does not parse reports nothing here (its
    /// syntax error is reported where every script's is).
    /// </summary>
    public static IReadOnlyList<SqlDiagnostic> FindTransactionControl(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);

        if (TryParseStatementsJson(sql) is not { } json)
        {
            return [];
        }

        using var document = PgQueryJson.Parse(json);
        var found = new List<SqlDiagnostic>();
        foreach (var statement in document.RootElement.EnumerateArray())
        {
            if (!TryGetDirectProperty(statement, "stmt", out var body) || body.ValueKind != JsonValueKind.Object || Classify(body) is not { } finding)
            {
                continue;
            }

            var location = TryGetDirectInt32Property(statement, "stmt_location", out var statementLocation) ? statementLocation : 0;
            var length = TryGetDirectInt32Property(statement, "stmt_len", out var statementLength) ? statementLength : 0;
            var range = UnmodeledStatementReader.DescribeWithRange(sql, location, length);
            found.Add(new SqlDiagnostic(finding.Severity, $"{finding.Message}: {range.Snippet}", TransactionControlSource, range.Line)
            {
                Column = range.Column,
                EndLine = range.EndLine,
                EndColumn = range.EndColumn,
            });
        }

        return found;
    }

    private static (SqlDiagnosticSeverity Severity, string Message)? Classify(JsonElement body)
    {
        var (kind, node) = body.EnumerateObject().First() is var property ? (property.Name, property.Value) : default;
        string? Text(string name) => TryGetDirectProperty(node, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        bool Flag(string name) => TryGetDirectProperty(node, name, out var value) && value.ValueKind == JsonValueKind.True;

        static (SqlDiagnosticSeverity, string) Ends(string what)
            => (SqlDiagnosticSeverity.Error, $"{what} in a deploy script ends the transaction pgproj publishes in, so what comes after it would run outside the transaction and stay applied when a later statement fails; remove it (pgproj commits at the end)");
        static (SqlDiagnosticSeverity, string) Refused(string what)
            => (SqlDiagnosticSeverity.Error, $"{what} cannot run inside a transaction block, and pgproj runs the deploy scripts in one, so the publish would fail and be rolled back; run it separately, outside pgproj");

        switch (kind)
        {
            case "TransactionStmt":
                return Text("kind") switch
                {
                    "TRANS_STMT_COMMIT" => Ends("COMMIT (or END)"),
                    "TRANS_STMT_ROLLBACK" => Ends("ROLLBACK (or ABORT)"),
                    "TRANS_STMT_PREPARE" => Ends("PREPARE TRANSACTION"),
                    "TRANS_STMT_COMMIT_PREPARED" => Ends("COMMIT PREPARED"),
                    "TRANS_STMT_ROLLBACK_PREPARED" => Ends("ROLLBACK PREPARED"),
                    "TRANS_STMT_BEGIN" or "TRANS_STMT_START" => (SqlDiagnosticSeverity.Warning,
                        "BEGIN (or START TRANSACTION) in a deploy script starts a transaction inside the one pgproj publishes in; PostgreSQL only warns and goes on, so it does nothing: remove it"),
                    _ => null, // a savepoint: SAVEPOINT, RELEASE, ROLLBACK TO
                };
            case "VacuumStmt" when Flag("is_vacuumcmd"):
                return Refused("VACUUM");
            case "CreatedbStmt":
                return Refused("CREATE DATABASE");
            case "DropdbStmt":
                return Refused("DROP DATABASE");
            case "CreateTableSpaceStmt":
                return Refused("CREATE TABLESPACE");
            case "DropTableSpaceStmt":
                return Refused("DROP TABLESPACE");
            case "AlterSystemStmt":
                return Refused("ALTER SYSTEM");
            case "IndexStmt" when Flag("concurrent"):
                return Refused("CREATE INDEX CONCURRENTLY");
            case "DropStmt" when Flag("concurrent"):
                return Refused("DROP INDEX CONCURRENTLY");
            case "ReindexStmt" when HasDefElem(node, "params", "concurrently"):
                return Refused("REINDEX ... CONCURRENTLY");
            case "ReindexStmt" when Text("kind") is "REINDEX_OBJECT_DATABASE" or "REINDEX_OBJECT_SYSTEM":
                return Refused(Text("kind") == "REINDEX_OBJECT_DATABASE" ? "REINDEX DATABASE" : "REINDEX SYSTEM");
            case "ClusterStmt" when !TryGetDirectProperty(node, "relation", out _):
                return Refused("CLUSTER without a table");
            case "DiscardStmt" when Text("target") == "DISCARD_ALL":
                return Refused("DISCARD ALL");
            default:
                return null;
        }
    }

    private static bool HasDefElem(JsonElement node, string listName, string defname)
        => TryGetDirectProperty(node, listName, out var list)
            && list.ValueKind == JsonValueKind.Array
            && list.EnumerateArray().Any(item => TryGetDirectProperty(item, "DefElem", out var element)
                && TryGetDirectProperty(element, "defname", out var name)
                && name.ValueKind == JsonValueKind.String
                && string.Equals(name.GetString(), defname, StringComparison.Ordinal));
}
