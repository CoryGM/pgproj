using System.Text.RegularExpressions;
using PgProj.Deploy;
using PgProj.Semantics;

namespace PgProj.Engine;

/// <summary>One operation of a script that rewrites, scans, or blocks writes to a table that already exists, for as long as it takes (G-270).</summary>
internal sealed record LockWarning(string TableKey, string Operation, string Effect);

/// <summary>
/// The operations of a script that hold a heavy lock on a table the database already has for as long as they take, which on a large table can be a long time
/// (G-270, the user's decision of 2026-10-13). <c>publish</c> names them before anything runs, in <c>--dry-run</c> and <c>--output</c> and before <c>--execute</c>; nothing
/// is refused. A table created by the same script has no rows yet, so nothing on it is named. Found from the operations, by PostgreSQL's documented rules:
/// <list type="bullet">
/// <item><description>Rewrites the table and its indexes under <c>ACCESS EXCLUSIVE</c>: a column type change that is not a known binary-coercible widening, <c>SET ACCESS METHOD</c>,
/// <c>SET LOGGED|UNLOGGED</c>, and an added column that is an identity, a serial, a stored generated column, or has a default that calls a volatile function.</description></item>
/// <item><description>Scans the whole table under its lock: <c>SET NOT NULL</c> (<c>ACCESS EXCLUSIVE</c>), an added <c>CHECK</c> (<c>ACCESS EXCLUSIVE</c>), and an added foreign key
/// (<c>SHARE ROW EXCLUSIVE</c> on both tables), unless written <c>NOT VALID</c>.</description></item>
/// <item><description>Blocks writes while an index is built: a new index (<c>SHARE</c>), and a new primary key, unique, or exclusion constraint.</description></item>
/// </list>
/// </summary>
internal static partial class LockWarnings
{
    /// <summary>Built-in functions that are volatile, so a default that calls one gives every existing row its own value, which means a rewrite.</summary>
    private static readonly HashSet<string> VolatileBuiltIns = new(StringComparer.OrdinalIgnoreCase)
    {
        "random", "random_normal", "clock_timestamp", "timeofday", "gen_random_uuid", "uuidv4", "uuidv7", "uuid_generate_v1", "uuid_generate_v1mc", "uuid_generate_v4",
        "nextval", "setval", "txid_current", "pg_current_xact_id", "statement_timestamp",
    };

    /// <summary>Every operation of <paramref name="batch"/> that rewrites, scans, or blocks writes to a table <paramref name="live"/> already has.</summary>
    public static IReadOnlyList<LockWarning> Find(OperationBatch batch, DatabaseModel live, DatabaseModel project)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(project);

        var warnings = new List<LockWarning>();
        foreach (var operation in batch.Operations)
        {
            switch (operation)
            {
                case AlterOperation { Change: { OldNode: TableDefinition oldTable, NewNode: TableDefinition newTable } }:
                    AddTableChanges(warnings, oldTable, newTable, project);
                    break;
                case CreateOperation { Node: IndexDefinition index, OnlyParent: false } when Exists(live, index.SchemaName, index.TableName):
                    warnings.Add(new LockWarning(Key(index.SchemaName, index.TableName), $"CREATE INDEX {index.Name}", "blocks writes to the table while the index is built"));
                    break;
                case CreateOperation { Node: ConstraintDefinition constraint } when Exists(live, constraint.SchemaName, constraint.TableName):
                    AddConstraint(warnings, constraint);
                    break;
            }
        }

        return warnings.Distinct().ToArray();
    }

    /// <summary>The warning text: a heading, then one line per table and operation.</summary>
    public static string Describe(IReadOnlyList<LockWarning> warnings)
    {
        ArgumentNullException.ThrowIfNull(warnings);

        return "Warning: these changes rewrite, scan, or block writes to a table that already exists, for as long as they take on a large table:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, warnings.Select(static warning => $"  {DatabaseModel.DisplayKey(warning.TableKey)}: {warning.Operation} {warning.Effect}"));
    }

    private static void AddTableChanges(List<LockWarning> warnings, TableDefinition oldTable, TableDefinition newTable, DatabaseModel project)
    {
        var key = Key(newTable.SchemaName, newTable.Name);
        const string rewrites = "rewrites the table and its indexes under an ACCESS EXCLUSIVE lock";

        if (oldTable.IsUnlogged != newTable.IsUnlogged)
        {
            warnings.Add(new LockWarning(key, newTable.IsUnlogged ? "SET UNLOGGED" : "SET LOGGED", rewrites));
        }

        if (newTable.AccessMethod is not null && !string.Equals(oldTable.AccessMethod, newTable.AccessMethod, StringComparison.Ordinal))
        {
            warnings.Add(new LockWarning(key, $"SET ACCESS METHOD {newTable.AccessMethod}", rewrites));
        }

        var oldColumns = oldTable.Columns.ToDictionary(static column => column.Name, StringComparer.Ordinal);
        foreach (var column in newTable.Columns.Where(static column => column.InheritedFromTable is null && column.CopiedByLike is null))
        {
            if (!oldColumns.TryGetValue(column.Name, out var oldColumn))
            {
                if (AddedColumnRewrites(column, project) is { } why)
                {
                    warnings.Add(new LockWarning(key, $"ADD COLUMN {column.Name} ({why})", rewrites));
                }

                continue;
            }

            if (!string.Equals(oldColumn.GetComparableTypeSignature(), column.GetComparableTypeSignature(), StringComparison.OrdinalIgnoreCase)
                && !IsBinaryCoercibleWidening(oldColumn, column))
            {
                warnings.Add(new LockWarning(key, $"ALTER COLUMN {column.Name} TYPE", rewrites));
            }

            if (oldColumn.AllowsNull && !column.AllowsNull && !column.NotNullIsNotValid)
            {
                warnings.Add(new LockWarning(key, $"ALTER COLUMN {column.Name} SET NOT NULL", "scans the whole table under an ACCESS EXCLUSIVE lock"));
            }
        }
    }

    private static void AddConstraint(List<LockWarning> warnings, ConstraintDefinition constraint)
    {
        var key = Key(constraint.SchemaName, constraint.TableName);
        var type = constraint.ConstraintType?.Trim().Replace('_', ' ').ToUpperInvariant();
        switch (type)
        {
            case "C" or "CHECK" or "CONSTR CHECK" when !constraint.IsNotValid:
                warnings.Add(new LockWarning(key, $"ADD CONSTRAINT {constraint.Name} CHECK", "scans the whole table under an ACCESS EXCLUSIVE lock (NOT VALID would not)"));
                break;
            case "F" or "FOREIGN KEY" or "CONSTR FOREIGN" when !constraint.IsNotValid:
                warnings.Add(new LockWarning(key, $"ADD CONSTRAINT {constraint.Name} FOREIGN KEY", "scans the whole table under a SHARE ROW EXCLUSIVE lock on it and the table it references (NOT VALID would not)"));
                break;
            case "P" or "PK" or "PRIMARY KEY" or "CONSTR PRIMARY" or "U" or "UNIQUE" or "CONSTR UNIQUE" or "X" or "EXCLUDE" or "CONSTR EXCLUSION":
                warnings.Add(new LockWarning(key, $"ADD CONSTRAINT {constraint.Name}", "builds an index under an ACCESS EXCLUSIVE lock"));
                break;
        }
    }

    /// <summary>Why an added column rewrites the table, or null when it does not (a constant or stable default, or none).</summary>
    private static string? AddedColumnRewrites(ColumnDefinition column, DatabaseModel project)
    {
        if (!string.IsNullOrWhiteSpace(column.IdentityGeneration))
        {
            return "an identity";
        }

        if (column.IsSerial)
        {
            return "a serial";
        }

        if (column.IsGenerated && !column.IsVirtual)
        {
            return "a stored generated column";
        }

        if (column.IsGenerated || string.IsNullOrWhiteSpace(column.RawDefaultExpression))
        {
            return null;
        }

        foreach (Match call in FunctionCall().Matches(column.RawDefaultExpression))
        {
            var name = call.Groups["name"].Value;
            var bare = name[(name.LastIndexOf('.') + 1)..].Trim('"');
            if (VolatileBuiltIns.Contains(bare))
            {
                return $"a default calling {bare}()";
            }

            // A project function is volatile unless it says otherwise.
            if (project.Functions.Values.FirstOrDefault(function => string.Equals(function.Name, bare, StringComparison.Ordinal)) is { } projectFunction
                && projectFunction.Signature?.Volatility is not ("IMMUTABLE" or "STABLE" or "immutable" or "stable" or "i" or "s"))
            {
                return $"a default calling {bare}(), which is volatile";
            }
        }

        return null;
    }

    /// <summary>
    /// A type change PostgreSQL makes without rewriting the table: a <c>varchar</c> made longer or unlimited or turned into <c>text</c>, a <c>varbit</c> made longer,
    /// and a <c>numeric</c> given more precision with the same scale, or none.
    /// </summary>
    private static bool IsBinaryCoercibleWidening(ColumnDefinition oldColumn, ColumnDefinition newColumn)
    {
        if (oldColumn.ArrayDimensions != newColumn.ArrayDimensions)
        {
            return false;
        }

        var oldBase = BaseType(oldColumn.DataType);
        var newBase = BaseType(newColumn.DataType);
        var oldModifiers = oldColumn.TypeModifiers.Select(ToInt).ToArray();
        var newModifiers = newColumn.TypeModifiers.Select(ToInt).ToArray();

        if (oldBase is "varchar" && newBase is "text")
        {
            return true;
        }

        if (oldBase != newBase || oldBase is not ("varchar" or "varbit" or "numeric"))
        {
            return false;
        }

        if (newModifiers.Length == 0)
        {
            return true;
        }

        if (oldModifiers.Length == 0 || oldModifiers.Any(static value => value is null) || newModifiers.Any(static value => value is null))
        {
            return false;
        }

        return oldBase == "numeric"
            ? newModifiers[0] >= oldModifiers[0] && (oldModifiers.ElementAtOrDefault(1) ?? 0) == (newModifiers.ElementAtOrDefault(1) ?? 0)
            : newModifiers[0] >= oldModifiers[0];
    }

    private static int? ToInt(string value) => int.TryParse(value.Trim(), out var number) ? number : null;

    private static string BaseType(string? dataType)
    {
        var name = (dataType ?? string.Empty).Trim().ToLowerInvariant();
        if (name.StartsWith("pg_catalog.", StringComparison.Ordinal))
        {
            name = name["pg_catalog.".Length..];
        }

        return name switch
        {
            "character varying" => "varchar",
            "bit varying" => "varbit",
            "decimal" => "numeric",
            _ => name,
        };
    }

    private static bool Exists(DatabaseModel live, string? schemaName, string? tableName)
        => tableName is not null && live.Tables.ContainsKey(Key(schemaName, tableName));

    private static string Key(string? schemaName, string? tableName) => DatabaseModel.CreateKey(schemaName, tableName ?? string.Empty);

    [GeneratedRegex(@"(?<name>(?:""[^""]+""|[A-Za-z_][\w$]*)(?:\.(?:""[^""]+""|[A-Za-z_][\w$]*))?)\s*\(")]
    private static partial Regex FunctionCall();
}
