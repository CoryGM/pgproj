using PgProj.Semantics;

namespace PgProj.Deploy;

/// <summary>
/// The second half of a composite type's swap (G-119, the user's decisions of 2026-11-09), right after the project's <c>CREATE TYPE</c>, when a field is
/// retyped while table columns use the type (PostgreSQL refuses <c>ALTER ATTRIBUTE ... TYPE</c> then). Each column is moved to the new type with its value
/// built field by field, matched by name, and read back through text, so each field's value is parsed by its new type and one that does not fit fails
/// loudly instead of being cut: <c>USING CASE WHEN c::text IS NULL THEN NULL ELSE ROW((c).a, (c).b, NULL)::text::t END</c> (a field the new type adds is
/// null; a null column stays null, not a row of nulls). An array column whose fields line up by position moves with <c>c::text::t[]</c>; one whose
/// fields do not, through a function made for the move (<see cref="MoveFunctionName"/>), which keeps the order and nulls of a one-dimensional array and
/// refuses a multi-dimensional one by name. The column's default is dropped first and set again after; each move is in a <c>DO</c> block that names the
/// column and the type when a value does not fit, and the transaction is rolled back. The old type is dropped last.
/// </summary>
public sealed record class CompositeSwapOperation : SqlOperation
{
    public required string? SchemaName { get; init; }

    public required string TypeName { get; init; }

    public required IReadOnlyList<EnumSwapColumn> Columns { get; init; }

    /// <summary>The database's fields, in its order.</summary>
    public required IReadOnlyList<string> OldFieldNames { get; init; }

    /// <summary>The project's fields, in its order: the new type's.</summary>
    public required IReadOnlyList<string> NewFieldNames { get; init; }

    /// <summary>Why the move can lose a value (a field dropped, a field narrowed), for <c>--allow-destructive</c>; empty when it keeps every value.</summary>
    public IReadOnlyList<string> DestructiveReasons { get; init; } = [];

    /// <summary>
    /// The function an array column moves through when its fields do not line up by position: <c>&lt;type&gt;_pgproj_move</c> in the type's schema, the
    /// type's name shortened to fit PostgreSQL's 63 bytes. It is dropped before the old type.
    /// </summary>
    public static string MoveFunctionName(string typeName) => EnumSwapRenameOperation.SwapName(typeName, "_pgproj_move");

    /// <summary>True when the fields the old and new types share are all the fields of both, in the same order: a value's text reads the same in both.</summary>
    public bool FieldsLineUp => OldFieldNames.SequenceEqual(NewFieldNames, StringComparer.Ordinal);

    public override string GenerateSql()
    {
        var type = EnumSwapSql.Qualify(SchemaName, TypeName);
        var oldType = EnumSwapSql.Qualify(SchemaName, EnumSwapRenameOperation.OldName(TypeName));
        var function = EnumSwapSql.Qualify(SchemaName, MoveFunctionName(TypeName));
        var typeText = EnumSwapSql.Literal($"{(SchemaName is null ? string.Empty : SchemaName + ".")}{TypeName}");
        var needsFunction = !FieldsLineUp && Columns.Any(static column => column.IsArray);
        var statements = new List<string>();
        if (needsFunction)
        {
            statements.Add(
                $"CREATE FUNCTION {function}(v {oldType}[], c text) RETURNS {type}[] LANGUAGE plpgsql AS $pgproj_move$" + Environment.NewLine
                + "BEGIN" + Environment.NewLine
                + "    IF v IS NULL THEN RETURN NULL; END IF;" + Environment.NewLine
                + "    IF array_ndims(v) IS NULL THEN RETURN '{}'; END IF;" + Environment.NewLine
                + "    IF array_ndims(v) > 1 THEN" + Environment.NewLine
                + $"        RAISE EXCEPTION 'the column % holds a multi-dimensional array, which pgproj does not move to the changed composite type %', c, {typeText}" + Environment.NewLine
                + "            USING ERRCODE = 'feature_not_supported', HINT = 'Change the column by hand (ALTER TABLE ... ALTER COLUMN ... TYPE ... USING ...), or keep the type.';" + Environment.NewLine
                + "    END IF;" + Environment.NewLine
                + $"    RETURN ARRAY(SELECT {Rebuilt("v[i]", type)} FROM generate_subscripts(v, 1) AS i ORDER BY i);" + Environment.NewLine
                + "END" + Environment.NewLine
                + "$pgproj_move$;");
        }

        foreach (var column in Columns)
        {
            var table = EnumSwapSql.Qualify(column.SchemaName, column.TableName);
            var name = EnumSwapSql.Quote(column.ColumnName);
            var columnName = $"{(column.SchemaName is null ? string.Empty : column.SchemaName + ".")}{column.TableName}.{column.ColumnName}";
            if (column.HasLiveDefault)
            {
                statements.Add($"ALTER TABLE {table} ALTER COLUMN {name} DROP DEFAULT;");
            }

            var (newType, expression) = (column.IsArray, FieldsLineUp) switch
            {
                (false, _) => (type, Rebuilt(name, type)),
                (true, true) => ($"{type}[]", $"{name}::text::{type}[]"),
                (true, false) => ($"{type}[]", $"{function}({name}, {EnumSwapSql.Literal(columnName)})"),
            };
            statements.Add(
                "DO $pgproj$" + Environment.NewLine
                + "BEGIN" + Environment.NewLine
                + $"    ALTER TABLE {table} ALTER COLUMN {name} TYPE {newType} USING {expression};" + Environment.NewLine
                + "EXCEPTION WHEN data_exception OR integrity_constraint_violation THEN" + Environment.NewLine
                + $"    RAISE EXCEPTION 'the column % holds a value the composite type % cannot take after the change, so the type was not rebuilt: %', {EnumSwapSql.Literal(columnName)}, {typeText}, SQLERRM" + Environment.NewLine
                + "        USING ERRCODE = SQLSTATE, HINT = 'Change those rows first (a pre-deploy script can), or keep the field''s type.';" + Environment.NewLine
                + "END" + Environment.NewLine
                + "$pgproj$;");
            if (column.DefaultExpression is { } defaultExpression)
            {
                statements.Add($"ALTER TABLE {table} ALTER COLUMN {name} SET DEFAULT {defaultExpression};");
            }
        }

        if (needsFunction)
        {
            statements.Add($"DROP FUNCTION {function}({oldType}[], text);");
        }

        statements.Add($"DROP TYPE {oldType};");
        return string.Join(Environment.NewLine, statements);
    }

    // The value of the old type in `value`, as the new type: each of the new type's fields from the old field of its name, or null, read through text.
    private string Rebuilt(string value, string type)
    {
        var fields = NewFieldNames.Select(field => OldFieldNames.Contains(field, StringComparer.Ordinal) ? $"({value}).{EnumSwapSql.Quote(field)}" : "NULL");
        return $"CASE WHEN {value}::text IS NULL THEN NULL ELSE ROW({string.Join(", ", fields)})::text::{type} END";
    }

    /// <summary>
    /// Why moving the columns from <paramref name="oldType"/> to <paramref name="newType"/> can lose a value (decision 2, the rule of G-35 and G-281): a field
    /// dropped, and a field retyped to a type not known to keep every value (<see cref="ColumnTypeWidening"/>). A widening is not destructive.
    /// </summary>
    public static IReadOnlyList<string> GetDestructiveReasons(TypeDefinition oldType, TypeDefinition newType)
    {
        ArgumentNullException.ThrowIfNull(oldType);
        ArgumentNullException.ThrowIfNull(newType);

        var name = $"{(newType.SchemaName is null ? string.Empty : newType.SchemaName + ".")}{newType.Name}";
        var newFields = newType.CompositeFields.ToDictionary(static field => field.Name, StringComparer.Ordinal);
        var reasons = AlterOperation.GetDestructiveCompositeFieldReasons(oldType, newType).ToList();
        foreach (var oldField in oldType.CompositeFields)
        {
            if (newFields.TryGetValue(oldField.Name, out var newField)
                && !string.Equals(Comparable(oldField), Comparable(newField), StringComparison.Ordinal)
                && !ColumnTypeWidening.KeepsEveryValue(Comparable(oldField), Comparable(newField)))
            {
                reasons.Add($"field \"{oldField.Name}\" of composite type {name} changes from {oldField.RawType ?? oldField.DataType} to {newField.RawType ?? newField.DataType}, "
                    + "which may not hold every value: a stored value that does not fit stops the publish, and a number or time may be rounded");
            }
        }

        return reasons;
    }

    private static string Comparable(CompositeTypeFieldDefinition field) => field.ComparableType ?? field.DataType ?? field.RawType ?? string.Empty;
}
