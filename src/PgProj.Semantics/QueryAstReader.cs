using System.Text.Json;

namespace PgProj.Semantics;

/// <summary>
/// Reads what a query refers to from pg_query's tree for it: a view's query, or the statements of a SQL-language routine body (G-90).
/// This replaced regular expressions over the SQL text, which read a schema-qualified call as a column (G-92), found no function call
/// in a <c>LANGUAGE sql</c> body (G-94), and matched keywords, literals, and comments as names. In the tree a relation is a
/// <c>RangeVar</c>, a column a <c>ColumnRef</c>, a call a <c>FuncCall</c>, and a cast a <c>TypeCast</c>, and nothing else is.
/// <para>
/// Names are resolved the way PostgreSQL scopes them: each statement (the query, a subquery, a CTE's body, an <c>INSERT</c>/<c>UPDATE</c>/
/// <c>DELETE</c>) opens a scope holding its <c>WITH</c> names and its <c>FROM</c> items, keyed by alias or, without one, by relation name.
/// An unqualified relation named like a CTE in scope is that CTE, not a relation. A qualified column (<c>o.id</c>, <c>s.t.id</c>) is
/// resolved through the scopes from the innermost out, so a correlated subquery sees its outer query's names. A column qualified by a
/// subquery, a function, a join, or a CTE is not a relation's column, and one whose qualifier is in no scope (a routine's parameter
/// written <c>f.p</c>) is not read as one.
/// </para>
/// </summary>
internal static class QueryAstReader
{
    /// <summary>A relation the query reads or writes, as written: <see cref="SchemaName"/> is null when unqualified.</summary>
    internal sealed record Relation(string? SchemaName, string Name);

    /// <summary>A column of <see cref="Relation"/>, written with <see cref="Qualifier"/> (an alias, a relation name, or <c>schema.relation</c>).</summary>
    internal sealed record Column(Relation Relation, string Qualifier, string Name);

    internal sealed record References(
        IReadOnlyList<Relation> Relations,
        IReadOnlyList<Column> Columns,
        IReadOnlyList<string> Functions,
        IReadOnlyList<string> Types)
    {
        /// <summary>Operator names as written (<c>===</c> or <c>schema.===</c>), from each operator expression (G-138).</summary>
        public IReadOnlyList<string> Operators { get; init; } = [];
    }

    public static References Collect(string? astJson)
    {
        var collector = new Collector();
        if (string.IsNullOrWhiteSpace(astJson))
        {
            return collector.ToReferences();
        }

        try
        {
            using var document = PgProj.Parser.PgQueryJson.Parse(astJson);
            collector.Walk(document.RootElement);
        }
        catch (JsonException)
        {
            // Not a tree: nothing is read from it.
        }

        return collector.ToReferences();
    }

    private sealed class Scope
    {
        public HashSet<string> CteNames { get; } = new(StringComparer.Ordinal);

        /// <summary>Each FROM item's name in this scope: its relation, or null for a subquery, function, join alias, or CTE.</summary>
        public Dictionary<string, Relation?> Items { get; } = new(StringComparer.Ordinal);
    }

    private sealed class Collector
    {
        private readonly List<Scope> _scopes = [];
        private readonly List<Relation> _relations = [];
        private readonly HashSet<Relation> _seenRelations = [];
        private readonly List<Column> _columns = [];
        private readonly HashSet<Column> _seenColumns = [];
        private readonly List<string> _functions = [];
        private readonly List<string> _operators = [];
        private readonly HashSet<string> _seenOperators = new(StringComparer.Ordinal);
        private readonly HashSet<string> _seenFunctions = new(StringComparer.Ordinal);
        private readonly List<string> _types = [];
        private readonly HashSet<string> _seenTypes = new(StringComparer.Ordinal);

        public References ToReferences() => new(_relations, _columns, _functions, _types) { Operators = _operators };

        public void Walk(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                    {
                        Walk(item);
                    }

                    break;
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        WalkProperty(property.Name, property.Value);
                    }

                    break;
            }
        }

        private void WalkProperty(string name, JsonElement value)
        {
            switch (name)
            {
                case "SelectStmt":
                case "InsertStmt":
                case "UpdateStmt":
                case "DeleteStmt":
                case "MergeStmt":
                    WalkStatement(name, value);
                    break;
                case "RangeVar":
                    // A FROM item was registered with its scope when its statement was entered; one anywhere else (TRUNCATE t) is still read.
                    AddRelation(value);
                    break;
                case "ColumnRef":
                    AddColumn(value);
                    break;
                case "FuncCall":
                    if (ReadNames(value, "funcname") is { Count: > 0 } functionName)
                    {
                        Add(_functions, _seenFunctions, string.Join('.', functionName));
                    }

                    Walk(value);
                    break;
                case "CallStmt":
                    // CALL p(...): the procedure is named by the statement's funccall, a bare FuncCall with no wrapper key (G-175).
                    if (value.TryGetProperty("funccall", out var callTarget)
                        && ReadNames(callTarget, "funcname") is { Count: > 0 } procedureName)
                    {
                        Add(_functions, _seenFunctions, string.Join('.', procedureName));
                    }

                    Walk(value);
                    break;
                case "A_Expr":
                    if (ReadNames(value, "name") is { Count: > 0 } operatorName)
                    {
                        Add(_operators, _seenOperators, string.Join('.', operatorName));
                    }

                    Walk(value);
                    break;
                case "TypeCast":
                case "JsonTableColumn":
                case "JsonOutput":
                    // A cast, and the types SQL/JSON names: a JSON_TABLE column's and a RETURNING clause's (G-231).
                    if (value.TryGetProperty("typeName", out var typeName)
                        && ReadNames(typeName, "names") is { Count: > 0 } typeParts)
                    {
                        var parts = typeParts.Where(static part => !string.Equals(part, "pg_catalog", StringComparison.Ordinal)).ToArray();
                        if (parts.Length > 0)
                        {
                            Add(_types, _seenTypes, string.Join('.', parts));
                        }
                    }

                    Walk(value);
                    break;
                default:
                    Walk(value);
                    break;
            }
        }

        private void WalkStatement(string kind, JsonElement statement)
        {
            var scope = new Scope();
            _scopes.Add(scope);
            try
            {
                if (statement.TryGetProperty("withClause", out var withClause)
                    && withClause.TryGetProperty("ctes", out var ctes)
                    && ctes.ValueKind == JsonValueKind.Array)
                {
                    foreach (var cte in ctes.EnumerateArray())
                    {
                        if (cte.TryGetProperty("CommonTableExpr", out var expression)
                            && expression.TryGetProperty("ctename", out var cteName)
                            && cteName.GetString() is { } text)
                        {
                            scope.CteNames.Add(text);
                        }
                    }
                }

                foreach (var (field, isTarget) in FromFields(kind))
                {
                    if (!statement.TryGetProperty(field, out var items))
                    {
                        continue;
                    }

                    if (isTarget)
                    {
                        RegisterRangeVar(scope, items);
                    }
                    else
                    {
                        RegisterFromItems(scope, items);
                    }
                }

                // Everything else, including each FROM item's own parts (join conditions, subqueries, function calls), with this scope open.
                // A set operation's branches are typed SelectStmt fields (not wrapped in a "SelectStmt" node), each its own query.
                foreach (var property in statement.EnumerateObject())
                {
                    if (kind == "SelectStmt" && property.Name is "larg" or "rarg" && property.Value.ValueKind == JsonValueKind.Object)
                    {
                        WalkStatement("SelectStmt", property.Value);
                    }
                    else if (kind == "SelectStmt" && property.Name == "lockingClause")
                    {
                        // FOR UPDATE|SHARE OF x names FROM items of this query, by alias or table name, as RangeVars: they are not further relations (G-307).
                    }
                    else
                    {
                        WalkProperty(property.Name, property.Value);
                    }
                }
            }
            finally
            {
                _scopes.RemoveAt(_scopes.Count - 1);
            }
        }

        /// <summary>A statement's FROM items: a typed target relation (a bare <c>RangeVar</c>), or a list of FROM-clause nodes.</summary>
        private static IEnumerable<(string Field, bool IsTarget)> FromFields(string kind)
            => kind switch
            {
                "SelectStmt" => [("fromClause", false)],
                "InsertStmt" => [("relation", true)],
                "UpdateStmt" => [("relation", true), ("fromClause", false)],
                "DeleteStmt" => [("relation", true), ("usingClause", false)],
                "MergeStmt" => [("relation", true), ("sourceRelation", false)],
                _ => [],
            };

        private void RegisterFromItems(Scope scope, JsonElement items)
        {
            switch (items.ValueKind)
            {
                case JsonValueKind.Array:
                    foreach (var item in items.EnumerateArray())
                    {
                        RegisterFromItems(scope, item);
                    }

                    break;
                case JsonValueKind.Object:
                    foreach (var property in items.EnumerateObject())
                    {
                        switch (property.Name)
                        {
                            case "RangeVar":
                                RegisterRangeVar(scope, property.Value);
                                break;
                            case "JoinExpr":
                                if (property.Value.TryGetProperty("larg", out var left))
                                {
                                    RegisterFromItems(scope, left);
                                }

                                if (property.Value.TryGetProperty("rarg", out var right))
                                {
                                    RegisterFromItems(scope, right);
                                }

                                RegisterOpaque(scope, property.Value);
                                break;
                            case "RangeSubselect":
                            case "RangeFunction":
                            case "RangeTableFunc":
                            case "RangeTableSample":
                                if (property.Name == "RangeTableSample" && property.Value.TryGetProperty("relation", out var sampled))
                                {
                                    RegisterFromItems(scope, sampled);
                                }

                                RegisterOpaque(scope, property.Value);
                                break;
                        }
                    }

                    break;
            }
        }

        private void RegisterRangeVar(Scope scope, JsonElement rangeVar)
        {
            if (!rangeVar.TryGetProperty("relname", out var relationName) || relationName.GetString() is not { Length: > 0 } name)
            {
                return;
            }

            // A CTE in scope registers as an opaque item (AddRelation returns null for it).
            scope.Items[ReadAlias(rangeVar) ?? name] = AddRelation(rangeVar);
        }

        /// <summary>Records a relation read or written, or returns null for an unqualified name that is a CTE in scope.</summary>
        private Relation? AddRelation(JsonElement rangeVar)
        {
            if (!rangeVar.TryGetProperty("relname", out var relationName) || relationName.GetString() is not { Length: > 0 } name)
            {
                return null;
            }

            var schemaName = rangeVar.TryGetProperty("schemaname", out var schema) ? schema.GetString() : null;
            if (string.IsNullOrEmpty(schemaName) && IsCteInScope(name))
            {
                return null;
            }

            var relation = new Relation(string.IsNullOrEmpty(schemaName) ? null : schemaName, name);
            if (_seenRelations.Add(relation))
            {
                _relations.Add(relation);
            }

            return relation;
        }

        private static void RegisterOpaque(Scope scope, JsonElement item)
        {
            if (ReadAlias(item) is { } alias)
            {
                scope.Items[alias] = null;
            }
        }

        private bool IsCteInScope(string name) => _scopes.Any(scope => scope.CteNames.Contains(name));

        private void AddColumn(JsonElement columnRef)
        {
            if (ReadNames(columnRef, "fields") is not { Count: >= 2 and <= 3 } parts)
            {
                // Unqualified (or t.*): which relation it belongs to is not written.
                return;
            }

            var columnName = parts[^1];
            Relation? relation;
            string qualifier;
            if (parts.Count == 3)
            {
                qualifier = $"{parts[0]}.{parts[1]}";
                relation = FindItem(parts[1], out var item) && item is not null && string.Equals(item.SchemaName, parts[0], StringComparison.Ordinal)
                    ? item
                    : new Relation(parts[0], parts[1]);
            }
            else
            {
                qualifier = parts[0];
                if (!FindItem(parts[0], out relation) || relation is null)
                {
                    return;
                }
            }

            var column = new Column(relation, qualifier, columnName);
            if (_seenColumns.Add(column))
            {
                _columns.Add(column);
            }
        }

        private bool FindItem(string name, out Relation? relation)
        {
            for (var index = _scopes.Count - 1; index >= 0; index--)
            {
                if (_scopes[index].Items.TryGetValue(name, out relation))
                {
                    return true;
                }
            }

            relation = null;
            return false;
        }

        private static string? ReadAlias(JsonElement item)
            => item.TryGetProperty("alias", out var alias) && alias.TryGetProperty("aliasname", out var aliasName) ? aliasName.GetString() : null;

        private static void Add(List<string> into, HashSet<string> seen, string value)
        {
            if (seen.Add(value))
            {
                into.Add(value);
            }
        }
    }

    private static List<string>? ReadNames(JsonElement node, string field)
    {
        if (!node.TryGetProperty(field, out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var names = new List<string>();
        foreach (var item in list.EnumerateArray())
        {
            if (item.TryGetProperty("String", out var text) && text.TryGetProperty("sval", out var value) && value.GetString() is { } part)
            {
                names.Add(part);
            }
            else
            {
                // A_Star (t.*): not one column.
                return null;
            }
        }

        return names;
    }
}
