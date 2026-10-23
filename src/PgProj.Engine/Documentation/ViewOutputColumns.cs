using System.Text.Json;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Engine;

/// <summary>
/// The output columns of a project's view, worked out from its query's parse tree (G-306), so <c>document</c> of a project lists them as <c>document</c> of a database does.
/// A database's come from the catalog; a project has no server to ask. The names follow PostgreSQL's rules for a select-list item (<c>AS</c> alias, a column's own name, a
/// function's name, <c>?column?</c> for anything else, the first leg of a <c>UNION</c>, a view's column list), <c>*</c> and <c>t.*</c> are expanded from the project's own tables and
/// views, and a type is given only for a plain column of one of them. When a name cannot be known (a <c>*</c> over something the project does not define, a subquery or function in
/// <c>FROM</c>), the result is empty rather than wrong.
/// </summary>
internal static class ViewOutputColumns
{
    public static IReadOnlyList<(string Name, string? Type)> Derive(ViewDefinition view, DatabaseModel model)
        => Derive(view, model, []) ?? [];

    private static List<(string Name, string? Type)>? Derive(ViewDefinition view, DatabaseModel model, HashSet<string> inProgress)
    {
        if (string.IsNullOrWhiteSpace(view.QueryAst) || !inProgress.Add(view.Key))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(view.QueryAst, PgQueryJson.DocumentOptions);
            var select = document.RootElement;
            if (select.TryGetProperty("SelectStmt", out var wrapped))
            {
                select = wrapped;
            }

            var columns = Columns(select, view, model, inProgress);
            if (columns is null)
            {
                return null;
            }

            // CREATE VIEW v (a, b) AS ...: the list names the leading columns.
            for (var index = 0; index < view.ColumnAliases.Count && index < columns.Count; index++)
            {
                columns[index] = (view.ColumnAliases[index], columns[index].Type);
            }

            return columns;
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            inProgress.Remove(view.Key);
        }
    }

    private static List<(string Name, string? Type)>? Columns(JsonElement select, ViewDefinition view, DatabaseModel model, HashSet<string> inProgress)
    {
        // UNION, INTERSECT, EXCEPT: the names are the first leg's.
        if (select.TryGetProperty("op", out var op) && op.GetString() is { } setOperation && setOperation != "SETOP_NONE" && select.TryGetProperty("larg", out var left))
        {
            return Columns(left, view, model, inProgress);
        }

        if (select.TryGetProperty("valuesLists", out var values))
        {
            var width = values.GetArrayLength() > 0 && values[0].TryGetProperty("List", out var row) && row.TryGetProperty("items", out var cells) ? cells.GetArrayLength() : 0;
            return [.. Enumerable.Range(1, width).Select(static ordinal => ("column" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture), (string?)null))];
        }

        List<Relation>? relations = [];
        if (select.TryGetProperty("fromClause", out var from) && !CollectRelations(from, relations))
        {
            relations = null;
        }

        var columns = new List<(string Name, string? Type)>();
        if (!select.TryGetProperty("targetList", out var targets))
        {
            return columns;
        }

        foreach (var target in targets.EnumerateArray())
        {
            var item = target.GetProperty("ResTarget");
            var value = item.TryGetProperty("val", out var val) ? val : default;
            if (item.TryGetProperty("name", out var alias) && alias.GetString() is { } aliasName)
            {
                columns.Add((aliasName, TypeOfColumn(value, relations, view, model, inProgress)));
                continue;
            }

            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("ColumnRef", out var reference) && ExpandStar(reference, relations, view, model, inProgress) is { } expanded)
            {
                if (expanded.Count == 0)
                {
                    return null;
                }

                columns.AddRange(expanded);
                continue;
            }

            columns.Add((NameOf(value) ?? "?column?", TypeOfColumn(value, relations, view, model, inProgress)));
        }

        return columns;
    }

    /// <summary>The columns of <c>*</c> or <c>t.*</c>; null when the item is not a star; empty when it is one that cannot be expanded.</summary>
    private static List<(string Name, string? Type)>? ExpandStar(JsonElement reference, List<Relation>? relations, ViewDefinition view, DatabaseModel model, HashSet<string> inProgress)
    {
        var fields = reference.GetProperty("fields");
        var last = fields[fields.GetArrayLength() - 1];
        if (!last.TryGetProperty("A_Star", out _))
        {
            return null;
        }

        if (relations is null)
        {
            return [];
        }

        string? qualifier = fields.GetArrayLength() >= 2 && fields[fields.GetArrayLength() - 2].TryGetProperty("String", out var name) ? name.GetProperty("sval").GetString() : null;
        var result = new List<(string Name, string? Type)>();
        foreach (var relation in relations.Where(relation => qualifier is null || string.Equals(relation.Alias, qualifier, StringComparison.Ordinal)))
        {
            var columns = ColumnsOf(relation, view, model, inProgress);
            if (columns is null)
            {
                return [];
            }

            result.AddRange(columns);
        }

        return result;
    }

    private static string? TypeOfColumn(JsonElement value, List<Relation>? relations, ViewDefinition view, DatabaseModel model, HashSet<string> inProgress)
    {
        if (relations is null || value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("ColumnRef", out var reference))
        {
            return null;
        }

        var fields = reference.GetProperty("fields");
        var names = fields.EnumerateArray().Select(static field => field.TryGetProperty("String", out var text) ? text.GetProperty("sval").GetString() : null).ToArray();
        if (names.Length == 0 || names[^1] is not { } column)
        {
            return null;
        }

        var qualifier = names.Length >= 2 ? names[^2] : null;
        foreach (var relation in relations.Where(relation => qualifier is null || string.Equals(relation.Alias, qualifier, StringComparison.Ordinal)))
        {
            if (ColumnsOf(relation, view, model, inProgress) is { } columns)
            {
                foreach (var candidate in columns.Where(candidate => candidate.Name == column))
                {
                    return candidate.Type;
                }
            }
        }

        return null;
    }

    private static List<(string Name, string? Type)>? ColumnsOf(Relation relation, ViewDefinition view, DatabaseModel model, HashSet<string> inProgress)
    {
        foreach (var schema in relation.Schema is { } named ? new[] { named } : model.SearchPathFor(view.SchemaName).ToArray())
        {
            var key = $"{schema}.{relation.Name}";
            if (model.TryGetTable(key, out var table) && table is not null)
            {
                return [.. table.Columns.Select(static column => (column.Name, column.RawType ?? column.DataType))];
            }

            if (model.TryGetView(key, out var other) && other is not null)
            {
                return Derive(other, model, inProgress);
            }
        }

        return null;
    }

    /// <summary>The relations of a FROM clause, by the name a select item uses for them; false when one is a subquery or a function, whose columns are not known here.</summary>
    private static bool CollectRelations(JsonElement from, List<Relation> relations)
    {
        foreach (var item in from.EnumerateArray())
        {
            if (item.TryGetProperty("RangeVar", out var range))
            {
                var name = range.GetProperty("relname").GetString()!;
                var alias = range.TryGetProperty("alias", out var aliasNode) ? aliasNode.GetProperty("aliasname").GetString()! : name;
                relations.Add(new Relation(range.TryGetProperty("schemaname", out var schema) ? schema.GetString() : null, name, alias));
            }
            else if (item.TryGetProperty("JoinExpr", out var join))
            {
                var both = new JsonElement[] { join.GetProperty("larg"), join.GetProperty("rarg") };
                foreach (var side in both)
                {
                    using var wrapper = JsonDocument.Parse("[" + side.GetRawText() + "]");
                    if (!CollectRelations(wrapper.RootElement, relations))
                    {
                        return false;
                    }
                }
            }
            else
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The name PostgreSQL gives a select-list item with no alias (<c>FigureColname</c>); null when it is <c>?column?</c>.</summary>
    private static string? NameOf(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (value.TryGetProperty("ColumnRef", out var reference))
        {
            var fields = reference.GetProperty("fields");
            return fields[fields.GetArrayLength() - 1].TryGetProperty("String", out var text) ? text.GetProperty("sval").GetString() : null;
        }

        if (value.TryGetProperty("FuncCall", out var call))
        {
            var names = call.GetProperty("funcname");
            return names[names.GetArrayLength() - 1].GetProperty("String").GetProperty("sval").GetString();
        }

        if (value.TryGetProperty("TypeCast", out var cast))
        {
            if (cast.TryGetProperty("arg", out var argument) && NameOf(argument) is { } inner)
            {
                return inner;
            }

            var typeNames = cast.GetProperty("typeName").GetProperty("names");
            return typeNames[typeNames.GetArrayLength() - 1].GetProperty("String").GetProperty("sval").GetString();
        }

        return value.TryGetProperty("CoalesceExpr", out _) ? "coalesce"
            : value.TryGetProperty("CaseExpr", out _) ? "case"
            : value.TryGetProperty("A_ArrayExpr", out _) ? "array"
            : value.TryGetProperty("RowExpr", out _) ? "row"
            : value.TryGetProperty("MinMaxExpr", out var minMax) ? (minMax.GetProperty("op").GetString() == "IS_GREATEST" ? "greatest" : "least")
            : value.TryGetProperty("SubLink", out var link) && link.TryGetProperty("subLinkType", out var type) && type.GetString() == "EXISTS_SUBLINK" ? "exists"
            : null;
    }

    /// <summary>A catalog entry <c>name type</c> (the name quoted when it needs it, as <c>quote_ident</c> writes it) as its unquoted name and its type.</summary>
    internal static (string Name, string? Type) SplitEntry(string entry)
    {
        if (entry.StartsWith('"'))
        {
            for (var index = 1; index < entry.Length; index++)
            {
                if (entry[index] != '"')
                {
                    continue;
                }

                if (index + 1 < entry.Length && entry[index + 1] == '"')
                {
                    index++;
                    continue;
                }

                return (entry[1..index].Replace("\"\"", "\"", StringComparison.Ordinal), index + 2 < entry.Length ? entry[(index + 2)..] : null);
            }
        }

        var space = entry.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? (entry, null) : (entry[..space], entry[(space + 1)..]);
    }

    private sealed record Relation(string? Schema, string Name, string Alias);
}
