using System.Text;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy;

/// <summary>A column moved to a rebuilt type: an enum's by <see cref="EnumSwapOperation"/> (G-191), a composite's by <see cref="CompositeSwapOperation"/> (G-119).</summary>
/// <param name="DefaultExpression">The project's default for the column, set again after the move; null when it has none.</param>
/// <param name="HasLiveDefault">True when the database's column has a default, which is dropped before the move (PostgreSQL cannot cast it automatically).</param>
public sealed record EnumSwapColumn(string? SchemaName, string TableName, string ColumnName, bool IsArray, string? DefaultExpression, bool HasLiveDefault);

/// <summary>
/// The first half of a type's swap, an enum's (G-191, the user's design of 2026-11-09) or a composite's (G-119): the type the database has is renamed out of the way, to
/// <see cref="OldName"/>, so the new type is created under its own name, with no rename after it. It stands where the type's drop would be, after what uses the columns is
/// dropped.
/// </summary>
public sealed record class EnumSwapRenameOperation : SqlOperation
{
    public required string? SchemaName { get; init; }

    public required string TypeName { get; init; }

    public override string GenerateSql()
        => $"ALTER TYPE {EnumSwapSql.Qualify(SchemaName, TypeName)} RENAME TO {EnumSwapSql.Quote(OldName(TypeName))};";

    /// <summary>
    /// The name the old type is kept under while its columns move: <c>&lt;type&gt;_pgproj_old</c> (decision 5 as revised on 2026-11-09), the type's name
    /// shortened, a character at a time, so it fits PostgreSQL's 63 bytes.
    /// </summary>
    public static string OldName(string typeName) => SwapName(typeName, "_pgproj_old");

    /// <summary>The type's name with <paramref name="suffix"/>, the name shortened, a character at a time, so the whole fits PostgreSQL's 63 bytes.</summary>
    public static string SwapName(string typeName, string suffix)
    {
        ArgumentNullException.ThrowIfNull(typeName);
        ArgumentNullException.ThrowIfNull(suffix);

        var name = typeName;
        while (name.Length > 0 && Encoding.UTF8.GetByteCount(name) + Encoding.UTF8.GetByteCount(suffix) > 63)
        {
            name = name[..^1];
        }

        return name + suffix;
    }
}

/// <summary>
/// The second half of an enum type's swap (G-191), right after the project's <c>CREATE TYPE</c>: each column that used the type is moved to the new one
/// through its text (<c>USING c::text::t</c>, <c>c::text[]::t[]</c> for an array), its default dropped first and set again after, and the old type is
/// dropped. A row that holds a label the new type does not have fails the move: the statement is in a <c>DO</c> block that says so in words, naming the
/// column and the type, and the transaction is rolled back.
/// </summary>
public sealed record class EnumSwapOperation : SqlOperation
{
    public required string? SchemaName { get; init; }

    public required string TypeName { get; init; }

    public required IReadOnlyList<EnumSwapColumn> Columns { get; init; }

    /// <summary>The labels the database's type has that the project's does not (after any rename): a row holding one cannot be moved.</summary>
    public required IReadOnlyList<string> RemovedLabels { get; init; }

    public override string GenerateSql()
    {
        var type = EnumSwapSql.Qualify(SchemaName, TypeName);
        var statements = new List<string>();
        foreach (var column in Columns)
        {
            var table = EnumSwapSql.Qualify(column.SchemaName, column.TableName);
            var name = EnumSwapSql.Quote(column.ColumnName);
            if (column.HasLiveDefault)
            {
                statements.Add($"ALTER TABLE {table} ALTER COLUMN {name} DROP DEFAULT;");
            }

            var (newType, viaText) = column.IsArray ? ($"{type}[]", "text[]") : (type, "text");
            var columnText = EnumSwapSql.Literal($"{(column.SchemaName is null ? string.Empty : column.SchemaName + ".")}{column.TableName}.{column.ColumnName}");
            var typeText = EnumSwapSql.Literal($"{(SchemaName is null ? string.Empty : SchemaName + ".")}{TypeName}");
            statements.Add(
                "DO $pgproj$" + Environment.NewLine
                + "BEGIN" + Environment.NewLine
                + $"    ALTER TABLE {table} ALTER COLUMN {name} TYPE {newType} USING {name}::{viaText}::{newType};" + Environment.NewLine
                + "EXCEPTION WHEN invalid_text_representation THEN" + Environment.NewLine
                + $"    RAISE EXCEPTION 'the column % holds a label the enum type % no longer has, so the type was not rebuilt: %', {columnText}, {typeText}, SQLERRM" + Environment.NewLine
                + "        USING HINT = 'Change or delete those rows first (a pre-deploy script can), or keep the label.';" + Environment.NewLine
                + "END" + Environment.NewLine
                + "$pgproj$;");
            if (column.DefaultExpression is { } expression)
            {
                statements.Add($"ALTER TABLE {table} ALTER COLUMN {name} SET DEFAULT {expression};");
            }
        }

        statements.Add($"DROP TYPE {EnumSwapSql.Qualify(SchemaName, EnumSwapRenameOperation.OldName(TypeName))};");
        return string.Join(Environment.NewLine, statements);
    }
}

internal static class EnumSwapSql
{
    public static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    public static string Qualify(string? schema, string name) => schema is null ? Quote(name) : $"{Quote(schema)}.{Quote(name)}";

    public static string Literal(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
}
