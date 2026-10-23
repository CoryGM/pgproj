using System.Text.Json;

namespace PgProj.Parser;

/// <summary>
/// Gives each table written with <c>LIKE source [INCLUDING ...]</c> what PostgreSQL copies into it (G-221): the source's columns, at the clause's place
/// among the table's own, and, as the options say, their defaults, generated expressions, identities and comments, the source's <c>CHECK</c> constraints,
/// its keys, exclusion constraints and indexes, and its extended statistics. Every copy is marked with the source's key
/// (<see cref="ColumnNode.CopiedByLike"/> and so on): the <c>LIKE</c> is deployed as written, so the table's <c>CREATE TABLE</c> makes them.
/// </summary>
/// <remarks>
/// <c>LIKE</c> copies the source as it is when the table is created, and the table is created after its source and after what it copies (the dependency
/// graph orders it so), so the source is read as the project finally declares it: with its <c>ALTER TABLE</c> columns and its constraints' names, which is
/// what <see cref="AlterTableColumnApplier"/> and <see cref="ConstraintNameAssigner"/> give it. A <c>CHECK</c> keeps its source's name; a key, exclusion
/// constraint, index, or statistics object is named as PostgreSQL names one created without a name. A table that copies another table that copies is
/// expanded after it. A source the project does not declare is listed in the result, so a caller with a database can read it from there and run this
/// again with it as one of the source trees; until then the table has only its own columns. Runs before <see cref="AlterTableColumnApplier"/>, so an
/// <c>ALTER TABLE</c> on the table finds the copied columns.
/// </remarks>
public static class TableLikeExpander
{
    /// <summary>The <see cref="SqlDiagnostic.Source"/> of the diagnostics this class adds to a syntax tree.</summary>
    public const string DiagnosticSource = "TableLike";

    private const string DefaultSchema = "public";
    private const int MaxIdentifierBytes = 63;

    /// <summary>
    /// Returns copies of <paramref name="trees"/> in which every table with a <c>LIKE</c> clause has what the clause copies, and the keys of the sources
    /// neither <paramref name="trees"/> nor <paramref name="sourceTrees"/> declare. <paramref name="sourceTrees"/> are read for sources only (tables read
    /// from a database, G-221), and are not returned.
    /// </summary>
    public static (IReadOnlyList<SqlSyntaxTree> Trees, IReadOnlyList<string> ExternalSources) Apply(IReadOnlyList<SqlSyntaxTree> trees, IReadOnlyList<SqlSyntaxTree>? sourceTrees = null)
    {
        ArgumentNullException.ThrowIfNull(trees);

        if (!trees.Any(static tree => tree.Nodes.OfType<TableNode>().Any(static table => table.LikeClauses.Count > 0)))
        {
            return (trees, []);
        }

        var nodesByTree = trees.Select(static tree => tree.Nodes.ToList()).ToList();
        var diagnosticsByTree = trees.Select(static tree => tree.Diagnostics.ToList()).ToList();
        var pending = new HashSet<string>(
            trees.SelectMany(static tree => tree.Nodes.OfType<TableNode>()).Where(static table => table.LikeClauses.Count > 0).Select(static table => TableKey(table)),
            SqlIdentifiers.NameComparer);
        var external = new List<string>();
        var chosenNames = new HashSet<string>(StringComparer.Ordinal);

        bool progressed;
        do
        {
            progressed = false;
            var expandedThisRound = new HashSet<string>(SqlIdentifiers.NameComparer);
            var catalog = new Catalog(ConstraintNameAssigner.Assign(AlterTableColumnApplier.Apply(
                [.. nodesByTree.Select((nodes, index) => trees[index] with { Nodes = nodes.ToArray() }), .. sourceTrees ?? []])));

            for (var treeIndex = 0; treeIndex < nodesByTree.Count; treeIndex++)
            {
                foreach (var table in nodesByTree[treeIndex].OfType<TableNode>().Where(table => table.LikeClauses.Count > 0 && pending.Contains(TableKey(table))).ToArray())
                {
                    var key = TableKey(table);

                    // A source that copies from another table is expanded first, and read again once it is; one that copies from this table in turn never is.
                    if (table.LikeClauses.Any(like => pending.Contains(LikeKey(like)) || expandedThisRound.Contains(LikeKey(like))))
                    {
                        continue;
                    }

                    var nodes = nodesByTree[treeIndex];
                    var position = nodes.FindIndex(node => ReferenceEquals(node, table));
                    var (expanded, added) = Expand(table, catalog, external, chosenNames, diagnosticsByTree[treeIndex]);
                    nodes[position] = expanded;
                    nodes.InsertRange(position + 1, added);
                    pending.Remove(key);
                    expandedThisRound.Add(key);
                    progressed = true;
                }
            }
        }
        while (progressed && pending.Count > 0);

        for (var treeIndex = 0; treeIndex < nodesByTree.Count; treeIndex++)
        {
            foreach (var table in nodesByTree[treeIndex].OfType<TableNode>().Where(table => pending.Contains(TableKey(table))))
            {
                diagnosticsByTree[treeIndex].Add(new SqlDiagnostic(
                    SqlDiagnosticSeverity.Error,
                    $"Table '{TableKey(table)}' copies (LIKE) from a table that copies from it in turn, or from itself; PostgreSQL cannot create it.",
                    DiagnosticSource));
            }
        }

        var result = trees.Select((tree, index) => tree with { Nodes = nodesByTree[index].ToArray(), Diagnostics = diagnosticsByTree[index] }).ToArray();
        return (result, external.Distinct(SqlIdentifiers.NameComparer).ToArray());
    }

    private static (TableNode Table, IReadOnlyList<SqlNode> Added) Expand(
        TableNode table,
        Catalog catalog,
        List<string> external,
        HashSet<string> chosenNames,
        List<SqlDiagnostic> diagnostics)
    {
        var tableName = table.Name ?? "unknown_table";
        var schema = Schema(table.SchemaName);
        var likes = new List<TableLikeNode>();
        var copiedColumns = new List<(int Before, IReadOnlyList<ColumnNode> Columns)>();
        var objects = new List<SqlNode>();
        var comments = new List<SqlNode>();
        var columnNames = new HashSet<string>(table.Columns.Where(static column => column.Name is not null).Select(static column => column.Name!), StringComparer.Ordinal);

        foreach (var like in table.LikeClauses)
        {
            var sourceKey = LikeKey(like);
            IReadOnlyList<ColumnNode> columns;
            if (catalog.Tables.TryGetValue(sourceKey, out var source))
            {
                likes.Add(like with { SourceKind = null });
                columns = CopyColumns(table, like, sourceKey, catalog.EffectiveColumns(source), catalog.PrimaryKeyColumns(sourceKey));
                if (like.Includes(TableLikeNode.Constraints))
                {
                    objects.AddRange(catalog.Constraints(sourceKey)
                        .Where(static constraint => string.Equals(constraint.ConstraintType, DefaultConstraintNamer.CheckConstraintType, StringComparison.OrdinalIgnoreCase) && constraint.Name is not null)
                        .Select(constraint => constraint with
                        {
                            TableName = tableName,
                            SchemaName = table.SchemaName,
                            IsSchemaDefaulted = false,
                            IsNameGenerated = false,
                            IsDeclaredInCreateTable = false,
                            CopiedByLike = sourceKey,
                        }));
                }

                var indexNames = new Dictionary<string, string>(StringComparer.Ordinal);
                if (like.Includes(TableLikeNode.Indexes))
                {
                    objects.AddRange(CopyIndexes(table, sourceKey, catalog, chosenNames, indexNames));
                }

                if (like.Includes(TableLikeNode.Statistics))
                {
                    objects.AddRange(CopyStatistics(table, sourceKey, catalog, chosenNames));
                }

                if (like.Includes(TableLikeNode.Comments))
                {
                    comments.AddRange(CopyComments(table, sourceKey, source, columns, objects, indexNames, catalog));
                }
            }
            else if (catalog.Composites.TryGetValue(sourceKey, out var type))
            {
                likes.Add(like with { SourceKind = TableLikeNode.TypeSource });
                columns = [.. type.CompositeFieldTypes.Select(field => new ColumnNode(field.Name, tableName, table.SchemaName, field.BaseType, field.BaseType, null, null, false, null, field.CollationName, null, null)
                {
                    WrittenType = field.RawType,
                    TypeModifiers = field.Modifiers,
                    ArrayDimensions = field.ArrayDimensions,
                    IntervalFields = field.IntervalFields,
                    CopiedByLike = sourceKey,
                })];
                if (like.Includes(TableLikeNode.Comments))
                {
                    comments.AddRange(CopyColumnComments(table, sourceKey, columns, catalog));
                }
            }
            else
            {
                likes.Add(like);
                columns = [];
                if (catalog.Views.Contains(sourceKey))
                {
                    diagnostics.Add(new SqlDiagnostic(
                        SqlDiagnosticSeverity.Warning,
                        $"Table '{TableKey(table)}' copies the columns of view '{sourceKey}' (LIKE), which pgproj cannot know without the database; the table is read as having only its own columns.",
                        DiagnosticSource));
                }
                else
                {
                    external.Add(sourceKey);
                }
            }

            var kept = new List<ColumnNode>();
            foreach (var column in columns)
            {
                if (column.Name is not null && !columnNames.Add(column.Name))
                {
                    diagnostics.Add(new SqlDiagnostic(
                        SqlDiagnosticSeverity.Error,
                        $"Table '{TableKey(table)}' has column '{column.Name}' more than once: LIKE '{sourceKey}' copies a column of that name.",
                        DiagnosticSource));
                    continue;
                }

                kept.Add(column);
            }

            copiedColumns.Add((like.OwnColumnsBefore, kept));
        }

        var merged = new List<ColumnNode>();
        for (var index = 0; index <= table.Columns.Count; index++)
        {
            foreach (var (_, columns) in copiedColumns.Where(copied => copied.Before == index || (index == table.Columns.Count && copied.Before > index)))
            {
                merged.AddRange(columns);
            }

            if (index < table.Columns.Count)
            {
                merged.Add(table.Columns[index]);
            }
        }

        var added = new List<SqlNode>();
        added.AddRange(copiedColumns.SelectMany(static copied => copied.Columns));
        added.AddRange(objects);
        added.AddRange(comments);
        return (table with { Columns = merged, LikeClauses = likes }, added);
    }

    private static IReadOnlyList<ColumnNode> CopyColumns(TableNode table, TableLikeNode like, string sourceKey, IReadOnlyList<ColumnNode> sourceColumns, IReadOnlySet<string> primaryKey)
    {
        var defaults = like.Includes(TableLikeNode.Defaults);
        var generated = like.Includes(TableLikeNode.Generated);
        var identity = like.Includes(TableLikeNode.Identity);
        var comments = like.Includes(TableLikeNode.Comments);
        return [.. sourceColumns.Select(column =>
        {
            var copiesGenerated = generated && column.IsGenerated;
            var copiesDefault = defaults && !column.IsGenerated;
            var copiesIdentity = identity && column.IdentityGeneration is not null;
            return column with
            {
                TableName = table.Name,
                SchemaName = table.SchemaName,
                IsSchemaDefaulted = false,
                // NOT NULL is always copied, a key's, an identity's and a serial's included.
                IsNullable = column.IsNullable == false || column.IdentityGeneration is not null || column.IsSerial || (column.Name is not null && primaryKey.Contains(column.Name))
                    ? false
                    : column.IsNullable,
                RawDefaultExpression = copiesDefault ? column.RawDefaultExpression : null,
                DefaultExpressionAst = copiesDefault ? column.DefaultExpressionAst : null,
                IsGenerated = copiesGenerated,
                GeneratedExpression = copiesGenerated ? column.GeneratedExpression : null,
                GeneratedExpressionAst = copiesGenerated ? column.GeneratedExpressionAst : null,
                IsVirtual = copiesGenerated && column.IsVirtual,
                IdentityGeneration = copiesIdentity ? column.IdentityGeneration : null,
                IdentityOptions = copiesIdentity ? column.IdentityOptions : null,
                IdentityOptionsText = copiesIdentity ? column.IdentityOptionsText : null,
                // A serial's default names the source's sequence, which the copy shares when it copies defaults; it gets no sequence of its own.
                IsSerial = false,
                Comment = comments ? column.Comment : null,
                // INCLUDING STORAGE and INCLUDING COMPRESSION (G-222).
                Storage = like.Includes(TableLikeNode.Storage) ? column.Storage : null,
                Compression = like.Includes(TableLikeNode.Compression) ? column.Compression : null,
                StorageParameters = null,
                RenamedFrom = null,
                NotNullIsNotValid = false,
                NotNullConstraintName = null,
                NotNullName = null,
                CopiedByLike = sourceKey,
            };
        })];
    }

    /// <summary>
    /// The source's keys, exclusion constraints, and indexes, each on the table under the name PostgreSQL gives an index created without one: the table's
    /// name, the names of the index's columns (its expressions' as PostgreSQL figures them), and <c>pkey</c>, <c>key</c>, <c>excl</c>, or <c>idx</c>, with a
    /// number when the name is taken. They are named in the order the source declares them, which is the order PostgreSQL copies them in.
    /// </summary>
    private static IEnumerable<SqlNode> CopyIndexes(TableNode table, string sourceKey, Catalog catalog, HashSet<string> chosenNames, Dictionary<string, string> indexNames)
    {
        var tableName = table.Name ?? "unknown_table";
        var schema = Schema(table.SchemaName);
        foreach (var item in catalog.IndexesAndKeys(sourceKey))
        {
            switch (item)
            {
                case ConstraintNode constraint:
                {
                    var (label, columns) = constraint.ConstraintType?.ToUpperInvariant() switch
                    {
                        DefaultConstraintNamer.PrimaryKeyConstraintType => ("pkey", (IReadOnlyList<string?>)[]),
                        DefaultConstraintNamer.UniqueConstraintType => ("key", [.. constraint.KeyColumns, .. constraint.IncludeColumns]),
                        _ => ("excl", [.. constraint.ExclusionElements.Select(static element => element.Name ?? FigureIndexColumnName(element.ExpressionAst)), .. constraint.IncludeColumns]),
                    };
                    var name = ChooseRelationName(tableName, label == "pkey" ? null : DefaultConstraintNamer.BuildAddition(ChooseIndexColumnNames(columns)), label, schema, catalog, chosenNames);
                    yield return constraint with
                    {
                        Name = name,
                        TableName = tableName,
                        SchemaName = table.SchemaName,
                        IsSchemaDefaulted = false,
                        IsNameGenerated = false,
                        IsDeclaredInCreateTable = false,
                        CopiedByLike = sourceKey,
                    };
                    break;
                }

                case IndexNode index:
                {
                    var columns = index.KeyElements.Select(static element => element.Name ?? FigureIndexColumnName(element.ExpressionAst)).Concat(index.IncludeColumns).ToArray();
                    var name = ChooseRelationName(tableName, DefaultConstraintNamer.BuildAddition(ChooseIndexColumnNames(columns)), "idx", schema, catalog, chosenNames);
                    if (index.Name is not null)
                    {
                        indexNames[index.Name] = name;
                    }

                    yield return index with { Name = name, TableName = tableName, SchemaName = table.SchemaName, IsSchemaDefaulted = false, CopiedByLike = sourceKey };
                    break;
                }
            }
        }
    }

    /// <summary>
    /// The source's extended statistics, on the table under the name PostgreSQL gives them: the table's name, the columns' names and <c>expr</c> for each
    /// expression, and <c>stat</c>. PostgreSQL lists a copy's columns before its expressions.
    /// </summary>
    private static IEnumerable<SqlNode> CopyStatistics(TableNode table, string sourceKey, Catalog catalog, HashSet<string> chosenNames)
    {
        var tableName = table.Name ?? "unknown_table";
        var schema = Schema(table.SchemaName);
        foreach (var statistics in catalog.Statistics(sourceKey))
        {
            var elements = statistics.Elements.Where(static element => element.Name is not null).Concat(statistics.Elements.Where(static element => element.Name is null)).ToArray();
            var addition = DefaultConstraintNamer.BuildAddition([.. elements.Select(static element => element.Name ?? "expr")]);
            var name = DefaultConstraintNamer.MakeObjectName(tableName, addition, "stat");
            for (var pass = 1; catalog.StatisticsNames(schema).Contains(name) || chosenNames.Contains($"stat:{schema}.{name}"); pass++)
            {
                name = DefaultConstraintNamer.MakeObjectName(tableName, addition, "stat" + pass.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            chosenNames.Add($"stat:{schema}.{name}");
            yield return statistics with
            {
                Name = name,
                SchemaName = table.SchemaName,
                IsSchemaDefaulted = false,
                TableSchemaName = table.SchemaName,
                TableName = tableName,
                Elements = elements,
                CopiedByLike = sourceKey,
            };
        }
    }

    /// <summary>The comments of the copied columns, <c>CHECK</c> constraints, and indexes, written on the copies, in the order the project states them.</summary>
    private static IEnumerable<SqlNode> CopyComments(
        TableNode table,
        string sourceKey,
        TableNode source,
        IReadOnlyList<ColumnNode> columns,
        IReadOnlyList<SqlNode> objects,
        IReadOnlyDictionary<string, string> indexNames,
        Catalog catalog)
    {
        var tableName = table.Name ?? "unknown_table";
        var checks = objects.OfType<ConstraintNode>()
            .Where(static constraint => string.Equals(constraint.ConstraintType, DefaultConstraintNamer.CheckConstraintType, StringComparison.OrdinalIgnoreCase) && constraint.Name is not null)
            .Select(static constraint => constraint.Name!)
            .ToHashSet(StringComparer.Ordinal);
        var sourceSchema = Schema(source.SchemaName);

        foreach (var comment in CopyColumnComments(table, sourceKey, columns, catalog))
        {
            yield return comment;
        }

        foreach (var comment in catalog.Comments)
        {
            if (comment.ObjectKind == "Constraint" && comment.ObjectName is { } constraintName && checks.Contains(constraintName)
                && string.Equals(Key(comment.SchemaName, comment.TableName ?? string.Empty), sourceKey, StringComparison.Ordinal))
            {
                yield return new ObjectCommentNode("Constraint", table.SchemaName, tableName, null, comment.CommentText) { ObjectName = constraintName };
            }
            else if (comment.ObjectKind == "Index" && comment.TableName is { } indexName && indexNames.TryGetValue(indexName, out var copyName)
                && string.Equals(Schema(comment.SchemaName), sourceSchema, StringComparison.Ordinal))
            {
                yield return new ObjectCommentNode("Index", table.SchemaName, copyName, null, comment.CommentText);
            }
        }
    }

    private static IEnumerable<SqlNode> CopyColumnComments(TableNode table, string sourceKey, IReadOnlyList<ColumnNode> columns, Catalog catalog)
    {
        var copied = columns.Where(static column => column.Name is not null).Select(static column => column.Name!).ToHashSet(StringComparer.Ordinal);
        foreach (var comment in catalog.Comments)
        {
            if (comment.ObjectKind == "Column" && comment.ColumnName is { } columnName && copied.Contains(columnName)
                && string.Equals(Key(comment.SchemaName, comment.TableName ?? string.Empty), sourceKey, StringComparison.Ordinal))
            {
                yield return new ObjectCommentNode("Column", table.SchemaName, table.Name, columnName, comment.CommentText);
            }
        }
    }

    /// <summary><c>ChooseRelationName</c>: the name, with a number after the label while a relation in the schema already has it.</summary>
    private static string ChooseRelationName(string tableName, string? addition, string label, string schema, Catalog catalog, HashSet<string> chosenNames)
    {
        var name = DefaultConstraintNamer.MakeObjectName(tableName, addition, label);
        for (var pass = 1; catalog.RelationNames(schema).Contains(name) || chosenNames.Contains($"rel:{schema}.{name}"); pass++)
        {
            name = DefaultConstraintNamer.MakeObjectName(tableName, addition, label + pass.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        chosenNames.Add($"rel:{schema}.{name}");
        return name;
    }

    /// <summary><c>ChooseIndexColumnNames</c>: <c>expr</c> for an expression PostgreSQL names nothing, and a number after a name an earlier column has.</summary>
    internal static IReadOnlyList<string> ChooseIndexColumnNames(IEnumerable<string?> names)
    {
        var result = new List<string>();
        foreach (var original in names.Select(static name => name ?? "expr"))
        {
            var current = original;
            for (var suffix = 1; result.Contains(current, StringComparer.Ordinal); suffix++)
            {
                var number = suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
                current = ClipToBytes(original, MaxIdentifierBytes - number.Length) + number;
            }

            result.Add(current);
        }

        return result;
    }

    /// <summary>
    /// <c>FigureIndexColname</c>: the name PostgreSQL gives an index column that is an expression, from its tree: a function's name, a column's, a cast's
    /// operand's (or its type's), and a few keywords; null when PostgreSQL names it nothing, which makes it <c>expr</c>.
    /// </summary>
    internal static string? FigureIndexColumnName(string? expressionAst)
    {
        if (string.IsNullOrWhiteSpace(expressionAst))
        {
            return null;
        }

        try
        {
            using var document = PgQueryJson.Parse(expressionAst);
            return Figure(document.RootElement).Name;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (string? Name, int Strength) Figure(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object)
        {
            return (null, 0);
        }

        foreach (var property in node.EnumerateObject())
        {
            var value = property.Value;
            switch (property.Name)
            {
                case "ColumnRef":
                    return LastString(value, "fields") is { } column ? (column, 2) : (null, 0);
                case "A_Indirection":
                    return LastString(value, "indirection") is { } field ? (field, 2) : Figure(Child(value, "arg"));
                case "FuncCall":
                    return LastString(value, "funcname") is { } function ? (function, 2) : (null, 0);
                case "A_Expr":
                    return String(value, "kind") == "AEXPR_NULLIF" ? ("nullif", 2) : (null, 0);
                case "TypeCast":
                {
                    var inner = Figure(Child(value, "arg"));
                    return inner.Strength <= 1 && value.TryGetProperty("typeName", out var typeName) && LastString(typeName, "names") is { } type ? (type, 1) : inner;
                }

                case "CollateClause":
                    return Figure(Child(value, "arg"));
                case "GroupingFunc":
                    return ("grouping", 2);
                case "SubLink":
                    return String(value, "subLinkType") switch
                    {
                        "EXISTS_SUBLINK" => ("exists", 2),
                        "ARRAY_SUBLINK" => ("array", 2),
                        _ => (null, 0),
                    };
                case "CaseExpr":
                {
                    var result = Figure(Child(value, "defresult"));
                    return result.Strength <= 1 ? ("case", 1) : result;
                }

                case "A_ArrayExpr":
                    return ("array", 2);
                case "RowExpr":
                    return ("row", 2);
                case "CoalesceExpr":
                    return ("coalesce", 2);
                case "MinMaxExpr":
                    return (String(value, "op") == "IS_GREATEST" ? "greatest" : "least", 2);
                case "SQLValueFunction" when String(value, "op") is { } op && op.StartsWith("SVFOP_", StringComparison.Ordinal):
                    return (op["SVFOP_".Length..].Replace("_N", string.Empty, StringComparison.Ordinal).ToLowerInvariant(), 2);
                default:
                    return (null, 0);
            }
        }

        return (null, 0);
    }

    private static JsonElement Child(JsonElement node, string name) => node.TryGetProperty(name, out var child) ? child : default;

    private static string? String(JsonElement node, string name)
        => node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? LastString(JsonElement node, string listName)
    {
        if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(listName, out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
        {
            return null;
        }

        var last = list[list.GetArrayLength() - 1];
        return last.ValueKind == JsonValueKind.Object && last.TryGetProperty("String", out var text) && text.TryGetProperty("sval", out var sval) ? sval.GetString() : null;
    }

    private static string ClipToBytes(string value, int maxBytes)
    {
        var builder = new System.Text.StringBuilder();
        var used = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (used + rune.Utf8SequenceLength > maxBytes)
            {
                break;
            }

            builder.Append(rune.ToString());
            used += rune.Utf8SequenceLength;
        }

        return builder.ToString();
    }

    private static string Schema(string? schemaName) => string.IsNullOrWhiteSpace(schemaName) ? DefaultSchema : schemaName.Trim();

    private static string Key(string? schemaName, string name) => $"{Schema(schemaName)}.{name.Trim()}";

    private static string TableKey(TableNode table) => Key(table.SchemaName, table.Name ?? "unknown_table");

    private static string LikeKey(TableLikeNode like) => Key(like.SchemaName, like.Name);

    /// <summary>What the project (and any source trees) declares, as the expansion reads it: each table, its constraints, indexes, and statistics in declaration order, and the names taken in each schema.</summary>
    private sealed class Catalog
    {
        private readonly Dictionary<string, List<(int Order, SqlNode Node)>> _indexesAndKeys = new(SqlIdentifiers.NameComparer);
        private readonly Dictionary<string, List<ConstraintNode>> _constraints = new(SqlIdentifiers.NameComparer);
        private readonly Dictionary<string, List<StatisticsNode>> _statistics = new(SqlIdentifiers.NameComparer);
        private readonly Dictionary<string, HashSet<string>> _relationNames = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _statisticsNames = new(StringComparer.Ordinal);

        public Catalog(IReadOnlyList<SqlSyntaxTree> trees)
        {
            var order = 0;
            foreach (var node in trees.SelectMany(static tree => tree.Nodes))
            {
                order++;
                switch (node)
                {
                    case TableNode { Name: { } name } table:
                        Tables.TryAdd(Key(table.SchemaName, name), table);
                        Relation(table.SchemaName, name);
                        foreach (var column in table.Columns.Where(static column => column.Name is not null && (column.IsSerial || column.IdentityGeneration is not null)))
                        {
                            Relation(table.SchemaName, DefaultConstraintNamer.MakeObjectName(name, column.Name, "seq"));
                        }

                        break;
                    case TypeNode { Name: { } name } type:
                        if (string.Equals(type.Form, "composite", StringComparison.OrdinalIgnoreCase))
                        {
                            Composites.TryAdd(Key(type.SchemaName, name), type);
                        }

                        Relation(type.SchemaName, name);
                        break;
                    case ViewNode { Name: { } name } view:
                        Views.Add(Key(view.SchemaName, name));
                        Relation(view.SchemaName, name);
                        break;
                    case SequenceNode { Name: { } name } sequence:
                        Relation(sequence.SchemaName, name);
                        break;
                    case IndexNode { TableName: { } tableName } index:
                        Add(_indexesAndKeys, Key(index.SchemaName, tableName), (order, index));
                        if (index.Name is not null)
                        {
                            Relation(index.SchemaName, index.Name);
                        }

                        break;
                    case ConstraintNode { TableName: { } tableName } constraint:
                        var tableKey = Key(constraint.SchemaName, tableName);
                        Add(_constraints, tableKey, constraint);
                        if (DefaultConstraintNamer.IsIndexBacked(constraint.ConstraintType))
                        {
                            Add(_indexesAndKeys, tableKey, (order, constraint));
                            if (constraint.Name is not null)
                            {
                                Relation(constraint.SchemaName, constraint.Name);
                            }
                        }

                        break;
                    case StatisticsNode { TableName: { } tableName } statistics:
                        Add(_statistics, Key(statistics.TableSchemaName, tableName), statistics);
                        if (statistics.Name is not null)
                        {
                            Add(_statisticsNames, Schema(statistics.SchemaName), statistics.Name);
                        }

                        break;
                    case ObjectCommentNode comment:
                        Comments.Add(comment);
                        break;
                }
            }
        }

        public Dictionary<string, TableNode> Tables { get; } = new(SqlIdentifiers.NameComparer);

        public Dictionary<string, TypeNode> Composites { get; } = new(SqlIdentifiers.NameComparer);

        public HashSet<string> Views { get; } = new(SqlIdentifiers.NameComparer);

        public List<ObjectCommentNode> Comments { get; } = [];

        public IReadOnlyList<ConstraintNode> Constraints(string tableKey) => _constraints.TryGetValue(tableKey, out var list) ? list : [];

        public IEnumerable<SqlNode> IndexesAndKeys(string tableKey)
            => _indexesAndKeys.TryGetValue(tableKey, out var list) ? list.OrderBy(static item => item.Order).Select(static item => item.Node) : [];

        public IReadOnlyList<StatisticsNode> Statistics(string tableKey) => _statistics.TryGetValue(tableKey, out var list) ? list : [];

        public IReadOnlySet<string> RelationNames(string schema) => _relationNames.TryGetValue(schema, out var names) ? names : new HashSet<string>();

        public IReadOnlySet<string> StatisticsNames(string schema) => _statisticsNames.TryGetValue(schema, out var names) ? names : new HashSet<string>();

        public IReadOnlySet<string> PrimaryKeyColumns(string tableKey)
            => Constraints(tableKey)
                .Where(static constraint => string.Equals(constraint.ConstraintType, DefaultConstraintNamer.PrimaryKeyConstraintType, StringComparison.OrdinalIgnoreCase))
                .SelectMany(static constraint => constraint.KeyColumns)
                .ToHashSet(StringComparer.Ordinal);

        /// <summary>
        /// A table's columns as PostgreSQL has them: a partition's are its parent's, and a child of <c>INHERITS</c> has its parents' first, then its own
        /// (one of a parent's names that the child declares again is the child's).
        /// </summary>
        public IReadOnlyList<ColumnNode> EffectiveColumns(TableNode table, int depth = 0)
        {
            if (depth > 32)
            {
                return table.Columns;
            }

            if (table.PartitionOfTableName is { } parentName)
            {
                return Tables.TryGetValue(Key(table.PartitionOfSchemaName, parentName), out var parent) ? EffectiveColumns(parent, depth + 1) : table.Columns;
            }

            if (table.InheritsFrom.Count == 0)
            {
                return table.Columns;
            }

            var own = table.Columns.Where(static column => column.Name is not null).GroupBy(static column => column.Name!, StringComparer.Ordinal).ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
            var columns = new List<ColumnNode>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var parent in table.InheritsFrom.Select(parent => Tables.TryGetValue(Key(parent.SchemaName, parent.Name), out var found) ? found : null).OfType<TableNode>())
            {
                foreach (var column in EffectiveColumns(parent, depth + 1).Where(column => column.Name is not null && seen.Add(column.Name)))
                {
                    columns.Add(own.TryGetValue(column.Name!, out var declared) ? declared : column);
                }
            }

            columns.AddRange(table.Columns.Where(column => column.Name is null || !seen.Contains(column.Name)));
            return columns;
        }

        private void Relation(string? schemaName, string name) => Add(_relationNames, Schema(schemaName), name);

        private static void Add<T>(Dictionary<string, List<T>> lists, string key, T value)
        {
            if (!lists.TryGetValue(key, out var list))
            {
                lists[key] = list = [];
            }

            list.Add(value);
        }

        private static void Add(Dictionary<string, HashSet<string>> sets, string key, string value)
        {
            if (!sets.TryGetValue(key, out var set))
            {
                sets[key] = set = new HashSet<string>(StringComparer.Ordinal);
            }

            set.Add(value);
        }
    }
}
