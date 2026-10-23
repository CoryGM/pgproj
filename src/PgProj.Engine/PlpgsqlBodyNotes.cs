using PgProj.Diff;
using PgProj.Semantics;

namespace PgProj.Engine;

/// <summary>One thing a change removes or alters that a PL/pgSQL routine's body names.</summary>
public sealed record PlpgsqlBodyNote(string RoutineKind, string RoutineKey, string What);

/// <summary>
/// When a change drops, renames, or retypes something a PL/pgSQL routine's body names (G-303b), say so. PostgreSQL records no dependency for a PL/pgSQL body and looks its names
/// up only when the routine runs, so nothing stops the change and the routine fails later, at run time. pgproj does not rebuild the routine: a PL/pgSQL function survives a table
/// being dropped and created again, and recreating it cannot make a body valid whose table is gone. The note is information only; it changes no script and no order.
/// <para>
/// What it covers, from what the body names (<see cref="PlpgsqlBodyReferences"/>): a table or view that is removed, a function or procedure that is removed with no routine of
/// that name added in its place, and a column of a table (named through its qualifier) that is removed, renamed, or given another type. A column a body does not
/// qualify (<c>SELECT total FROM orders</c>) is not read.
/// </para>
/// </summary>
internal static class PlpgsqlBodyNotes
{
    private static readonly string[] s_typeProperties =
    [
        nameof(ColumnDefinition.DataType),
        nameof(ColumnDefinition.RawType),
        nameof(ColumnDefinition.NormalizedTypeModifiers),
        nameof(ColumnDefinition.IntervalFields),
        "IsArray",
    ];

    public static IReadOnlyList<PlpgsqlBodyNote> Find(ChangeSet changes, DatabaseModel source)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(source);

        var removedRelations = changes.Removed.Where(static change => change.Node is TableDefinition or ViewDefinition).Select(static change => change.Node).ToList();
        var removedRelationKeys = new HashSet<string>(removedRelations.Select(static node => node.Key), PgProj.Parser.SqlIdentifiers.NameComparer);
        var removedRoutines = changes.Removed
            .Where(change => change.Node is FunctionDefinition or ProcedureDefinition
                && !changes.Added.Any(added => added.Node.GetType() == change.Node.GetType() && Same(added.Node, change.Node)))
            .Select(static change => change.Node)
            .ToList();

        // Column changes, by the column's table and name as the database has it now.
        var columnChanges = new List<(string? Schema, string Table, string Column, string What)>();
        foreach (var removed in changes.Removed.Where(static change => change.Node is ColumnDefinition))
        {
            var column = (ColumnDefinition)removed.Node;
            if (column.TableName is { } table && !removedRelationKeys.Contains(DatabaseModel.CreateKey(column.SchemaName, table)))
            {
                columnChanges.Add((column.SchemaName, table, column.Name, "dropped"));
            }
        }

        foreach (var modified in changes.Modified.Where(static change => change.OldNode is ColumnDefinition))
        {
            var column = (ColumnDefinition)modified.OldNode;
            if (column.TableName is not { } table)
            {
                continue;
            }

            if (modified.Details.FirstOrDefault(static detail => detail.PropertyName == nameof(ColumnDefinition.Name)) is { OldValue: { } oldName })
            {
                columnChanges.Add((column.SchemaName, table, oldName, "renamed"));
            }
            else if (modified.Details.Any(static detail => s_typeProperties.Contains(detail.PropertyName, StringComparer.Ordinal)))
            {
                columnChanges.Add((column.SchemaName, table, column.Name, "given another type"));
            }
        }

        var notes = new List<PlpgsqlBodyNote>();
        foreach (var (kind, key, schema, references) in Routines(source))
        {
            foreach (var node in removedRelations)
            {
                if (references.Relations.Any(relation => Matches(source, schema, relation.Schema, relation.Name, node.SchemaName, node.Name)))
                {
                    notes.Add(new PlpgsqlBodyNote(kind, key, $"{(node is TableDefinition ? "table" : "view")} '{Qualified(node.SchemaName, node.Name)}' is dropped"));
                }
            }

            foreach (var node in removedRoutines)
            {
                if (references.Functions.Any(function => Matches(source, schema, function.Schema, function.Name, node.SchemaName, node.Name)))
                {
                    notes.Add(new PlpgsqlBodyNote(kind, key, $"{(node is FunctionDefinition ? "function" : "procedure")} '{Qualified(node.SchemaName, node.Name)}' is dropped"));
                }
            }

            foreach (var (columnSchema, table, column, what) in columnChanges)
            {
                if (references.Columns.Any(reference => reference.Column == column && Matches(source, schema, reference.Schema, reference.Table, columnSchema, table)))
                {
                    notes.Add(new PlpgsqlBodyNote(kind, key, $"column '{Qualified(columnSchema, table)}.{column}' is {what}"));
                }
            }
        }

        return [.. notes.Distinct()];
    }

    public static string Describe(IReadOnlyList<PlpgsqlBodyNote> notes, string verb)
    {
        ArgumentNullException.ThrowIfNull(notes);
        var lines = notes.Select(static note => $"  - {note.RoutineKind} '{note.RoutineKey}' names {note.What}.");
        return $"{verb} what the body of a PL/pgSQL routine names. PostgreSQL records no dependency for such a body and looks its names up only when the routine runs, "
            + "so nothing stops this and the routine fails then:" + Environment.NewLine + string.Join(Environment.NewLine, lines);
    }

    private static IEnumerable<(string Kind, string Key, string? Schema, PlpgsqlBodyReferences References)> Routines(DatabaseModel source)
    {
        foreach (var function in source.Functions.Values.Where(static function => function.PlpgsqlReferences is not null))
        {
            yield return ("function", function.Key, function.SchemaName, function.PlpgsqlReferences!);
        }

        foreach (var procedure in source.Procedures.Values.Where(static procedure => procedure.PlpgsqlReferences is not null))
        {
            yield return ("procedure", procedure.Key, procedure.SchemaName, procedure.PlpgsqlReferences!);
        }
    }

    /// <summary>
    /// Whether a name written in a routine's body is the object <paramref name="schema"/>.<paramref name="name"/>: a qualified one by its schema, an unqualified one when
    /// the schema is on the routine's search path.
    /// </summary>
    private static bool Matches(DatabaseModel model, string? routineSchema, string? writtenSchema, string writtenName, string? schema, string name)
        => string.Equals(writtenName, name, StringComparison.Ordinal)
            && (writtenSchema is null
                ? string.Equals(schema, routineSchema, StringComparison.Ordinal) || model.SearchPathFor(routineSchema).Contains(schema ?? string.Empty, StringComparer.Ordinal)
                : string.Equals(writtenSchema, schema, StringComparison.Ordinal));

    /// <summary>Two routines of one name in one schema (an overload, or the same routine with another signature).</summary>
    private static bool Same(ISemanticNode first, ISemanticNode second)
        => string.Equals(first.SchemaName, second.SchemaName, StringComparison.Ordinal) && string.Equals(first.Name, second.Name, StringComparison.Ordinal);

    private static string Qualified(string? schema, string name) => schema is null ? name : $"{schema}.{name}";
}
