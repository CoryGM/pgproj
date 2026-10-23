using PgProj.Parser;

namespace PgProj.Semantics;

internal sealed class ColumnPass
{
    public DatabaseModel Execute(DatabaseModel model, IEnumerable<SqlSyntaxTree> syntaxTrees)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(syntaxTrees);

        var tables = new Dictionary<string, TableDefinition>(model.Tables, PgProj.Parser.SqlIdentifiers.NameComparer);
        var sequences = new Dictionary<string, SequenceDefinition>(model.Sequences, PgProj.Parser.SqlIdentifiers.NameComparer);
        var syntaxTreeList = syntaxTrees as IReadOnlyList<SqlSyntaxTree> ?? syntaxTrees.ToArray();

        foreach (var syntaxTree in syntaxTreeList)
        {
            foreach (var tableNode in syntaxTree.Nodes.OfType<TableNode>())
            {
                var tableName = tableNode.Name ?? "unknown_table";
                var key = DatabaseModel.CreateKey(tableNode.SchemaName, tableName);
                if (!tables.TryGetValue(key, out var table))
                {
                    continue;
                }

                tables[key] = MapColumns(syntaxTree, tableNode, table);
                    AddImplicitOwnedSequences(tables[key], tableNode.SchemaName, sequences);
            }

            ApplyColumnComments(syntaxTree, tables);
        }

        ApplyPrimaryKeyImpliedNotNull(syntaxTreeList, tables);

        return model with
        {
            Tables = tables,
            Sequences = sequences,
        };
    }

    /// <summary>
    /// PostgreSQL forces <c>NOT NULL</c> on every primary-key column, whether the key is declared
    /// inline (<c>id bigint PRIMARY KEY</c>), at table level (<c>PRIMARY KEY (a, b)</c>), or later via
    /// <c>ALTER TABLE ... ADD PRIMARY KEY</c>, and the live catalog reports it as not nullable (G-39).
    /// A project column whose nullability is unspecified is therefore marked <c>NOT NULL</c> so it
    /// compares equal to its deployed form; otherwise it re-diffs forever and <c>publish</c> would emit
    /// <c>DROP NOT NULL</c> on a primary-key column, which PostgreSQL rejects. Constraints are collected
    /// across all syntax trees because the key may be declared in a different statement or file than the
    /// table. An explicit nullability is left untouched.
    /// </summary>
    private static void ApplyPrimaryKeyImpliedNotNull(
        IReadOnlyList<SqlSyntaxTree> syntaxTrees,
        IDictionary<string, TableDefinition> tables)
    {
        foreach (var constraintNode in syntaxTrees.SelectMany(static tree => tree.Nodes.OfType<ConstraintNode>()))
        {
            if (!IsPrimaryKey(constraintNode.ConstraintType)
                || constraintNode.KeyColumns.Count == 0
                || string.IsNullOrWhiteSpace(constraintNode.TableName))
            {
                continue;
            }

            var table = ResolveTable(constraintNode, tables);
            if (table is null)
            {
                continue;
            }

            var keyColumns = new HashSet<string>(constraintNode.KeyColumns, PgProj.Parser.SqlIdentifiers.NameComparer);
            if (!table.Columns.Any(column => column.IsNullable is null && keyColumns.Contains(column.Name)))
            {
                continue;
            }

            tables[table.Key] = table with
            {
                Columns = table.Columns
                    .Select(column => column.IsNullable is null && keyColumns.Contains(column.Name)
                        ? column with { IsNullable = false }
                        : column)
                    .ToArray(),
            };
        }
    }

    private static bool IsPrimaryKey(string? constraintType)
        => constraintType?.Trim().Replace('_', ' ').ToUpperInvariant() is "P" or "PK" or "CONSTR PRIMARY" or "PRIMARY KEY";

    private static TableDefinition? ResolveTable(ConstraintNode node, IDictionary<string, TableDefinition> tables)
    {
        if (tables.TryGetValue(DatabaseModel.CreateKey(node.SchemaName, node.TableName!), out var exactTable))
        {
            return exactTable;
        }

        return string.IsNullOrWhiteSpace(node.SchemaName)
            ? tables.Values.FirstOrDefault(candidate => string.Equals(candidate.Name, node.TableName, StringComparison.Ordinal))
            : null;
    }

    /// <summary>An identity option as <c>CREATE SEQUENCE</c> names it, and the <see cref="SequenceDefinition"/> property it sets (G-214).</summary>
    public static readonly IReadOnlyList<(string Option, string Property)> IdentityOptionProperties =
    [
        ("increment", nameof(SequenceDefinition.IncrementBy)),
        ("minvalue", nameof(SequenceDefinition.MinValue)),
        ("maxvalue", nameof(SequenceDefinition.MaxValue)),
        ("start", nameof(SequenceDefinition.StartValue)),
        ("cache", nameof(SequenceDefinition.CacheSize)),
        ("cycle", nameof(SequenceDefinition.Cycle)),
        ("unlogged", nameof(SequenceDefinition.IsUnlogged)),
        ("logged", nameof(SequenceDefinition.IsUnlogged)),
    ];

    private static void AddImplicitOwnedSequences(
        TableDefinition table,
        string? schemaName,
        IDictionary<string, SequenceDefinition> sequences)
    {
        foreach (var column in table.Columns)
        {
            if (!column.IsSerial && string.IsNullOrWhiteSpace(column.IdentityGeneration))
            {
                continue;
            }

            var sequenceName = $"{table.Name}_{column.Name}_seq";
            var key = DatabaseModel.CreateKey(schemaName, sequenceName);
            if (sequences.ContainsKey(key))
            {
                continue;
            }

            var sequence = new SequenceDefinition(
                key,
                sequenceName,
                schemaName,
                ReferencedTables: [],
                ReferencedColumns: [],
                ReferencedTypes: [],
                ReferencedFunctions: [],
                ResolvedTables: [],
                ResolvedColumns: [],
                ResolvedTypes: [],
                ResolvedFunctions: [])
            {
                OwnedByTable = table.Key,
                OwnedByColumn = column.Name,
                DataType = column.RawType,
                IsIdentityOwned = !column.IsSerial && !string.IsNullOrWhiteSpace(column.IdentityGeneration),
                // PostgreSQL makes a serial's or an identity's sequence unlogged with an unlogged table (G-224).
                IsUnlogged = table.IsUnlogged,
                IncrementBy = "1",
                MinValue = "1",
                MaxValue = GetSequenceMaxValue(column.RawType),
                StartValue = "1",
                CacheSize = "1",
            };

            if (sequence.IsIdentityOwned)
            {
                // An identity's sequence has the options its column states and PostgreSQL's defaults for the rest, which are not compared (G-214).
                var options = column.DeclarationNode?.IdentityOptions;
                string? Option(string name) => options is not null && options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
                sequence = sequence with
                {
                    IncrementBy = Option("increment"),
                    MinValue = Option("minvalue"),
                    MaxValue = Option("maxvalue"),
                    StartValue = Option("start"),
                    CacheSize = Option("cache"),
                    Cycle = Option("cycle") is { } cycle && bool.TryParse(cycle, out var cycles) ? cycles : null,
                    // UNLOGGED or LOGGED among the identity's options says whether its sequence is logged whatever the table is (G-294).
                    IsUnlogged = Option("unlogged") is not null ? true : Option("logged") is not null ? false : sequence.IsUnlogged,
                    IdentityStatedOptions = new HashSet<string>(
                        IdentityOptionProperties.Where(pair => Option(pair.Option) is not null).Select(static pair => pair.Property),
                        StringComparer.Ordinal),
                };

                // The rest are PostgreSQL's defaults for the type and direction, which the sequence has; they are kept, not compared.
                var effective = sequence.GetEffectiveOptions();
                sequence = sequence with
                {
                    IncrementBy = sequence.IncrementBy ?? effective.IncrementBy,
                    MinValue = sequence.MinValue ?? effective.MinValue,
                    MaxValue = sequence.MaxValue ?? effective.MaxValue,
                    StartValue = sequence.StartValue ?? effective.StartValue,
                    CacheSize = sequence.CacheSize ?? effective.CacheSize,
                };
            }

            sequences[key] = sequence;
        }
    }

    // A declared integer column reaches here as pg_catalog.int4 and a serial one as integer; both must give the int4 maximum
    // (matching only the plain spellings gave every identity integer sequence the bigint maximum) (G-57).
    private static string GetSequenceMaxValue(string? dataType)
        => ColumnTypeNormalizer.NormalizeBaseType(dataType) switch
        {
            "int2" => "32767",
            "int4" => "2147483647",
            _ => "9223372036854775807",
        };

    private static void ApplyColumnComments(SqlSyntaxTree syntaxTree, IDictionary<string, TableDefinition> tables)
    {
        foreach (var commentNode in syntaxTree.ObjectComments.Where(static node => node.ObjectKind == "Column"))
        {
            var tableKey = DatabaseModel.CreateKey(commentNode.SchemaName, commentNode.TableName ?? string.Empty);
            var table = tables.TryGetValue(tableKey, out var exactTable)
                ? exactTable
                : tables.Values.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, commentNode.TableName, StringComparison.Ordinal)
                    && (commentNode.SchemaName is null || string.Equals(candidate.SchemaName, commentNode.SchemaName, StringComparison.Ordinal)));

            if (table is null)
            {
                continue;
            }

            var columnIndex = -1;
            for (var i = 0; i < table.Columns.Count; i++)
            {
                if (string.Equals(table.Columns[i].Name, commentNode.ColumnName, StringComparison.OrdinalIgnoreCase))
                {
                    columnIndex = i;
                    break;
                }
            }

            if (columnIndex < 0)
            {
                continue;
            }

            var updatedColumns = table.Columns.ToArray();
            updatedColumns[columnIndex] = updatedColumns[columnIndex] with { Comment = commentNode.CommentText };
            tables[table.Key] = table with { Columns = updatedColumns };
        }
    }

    private static TableDefinition MapColumns(SqlSyntaxTree tree, TableNode tableNode, TableDefinition table)
    {
        // A column is placed at its name, or, when it has no place of its own (copied by LIKE), at its table's statement (G-309e).
        var columns = tableNode.Columns
            .Select(column => MapColumn(column) with
            {
                Location = SemanticLocation.Of(tree.FilePath, column.Range ?? tableNode.Range),
                TypeLocation = SemanticLocation.Of(tree.FilePath, column.TypeRange),
            })
            .ToArray();

        return table with
        {
            Columns = columns,
        };
    }

    private static ColumnDefinition MapColumn(ColumnNode columnNode)
    {
        var columnName = columnNode.Name ?? "unknown_column";
        var tableName = columnNode.TableName;
        var qualifiedColumnName = tableName is null ? columnName : $"{tableName}.{columnName}";
        var schemaName = columnNode.SchemaName;
        var rawType = columnNode.RawType ?? columnNode.DataType;
        var storageParameters = columnNode.StorageParameters is null
            ? null
            : new Dictionary<string, string>(columnNode.StorageParameters, StringComparer.OrdinalIgnoreCase);
        var key = DatabaseModel.CreateKey(schemaName, qualifiedColumnName);

        // TODO: Capture richer PostgreSQL collation semantics once collation definitions participate in semantic binding.
        // TODO: Capture structured storage-parameter semantics beyond shallow key/value copying when downstream consumers are defined.
        // TODO: Capture richer generated-column semantics (PostgreSQL 12+) once expression trees are modeled structurally.
        return new ColumnDefinition(
            key,
            columnName,
            schemaName,
            tableName,
            rawType,
            rawType,
            columnNode.IsNullable,
            columnNode.RawDefaultExpression,
            columnNode.IsGenerated,
            columnNode.GeneratedExpression,
            columnNode.CollationName,
            storageParameters,
            ReferencedTables: [],
            ReferencedColumns: [],
            ReferencedTypes: [],
            ReferencedFunctions: [],
            ResolvedTables: [],
            ResolvedColumns: [],
            ResolvedTypes: [],
            ResolvedFunctions: [],
            columnNode.IdentityGeneration,
            columnNode.IsSerial,
            columnNode.RenamedFrom)
        {
            DeclarationNode = columnNode,
            WrittenType = columnNode.WrittenType,
            IdentityOptionsText = columnNode.IdentityOptionsText,
            Storage = columnNode.Storage,
            IsStorageSetByAlter = columnNode.IsStorageSetByAlter,
            Compression = columnNode.Compression,
            TypeModifiers = columnNode.TypeModifiers,
            ArrayDimensions = columnNode.ArrayDimensions,
            IntervalFields = columnNode.IntervalFields,
            IsVirtual = columnNode.IsVirtual,
            NotNullIsNotValid = columnNode.NotNullIsNotValid,
            CopiedByLike = columnNode.CopiedByLike,
            NotNullConstraintName = columnNode.NotNullConstraintName,
            NotNullName = columnNode.NotNullName,
            NotNullNoInherit = columnNode.NotNullNoInherit,
        };
    }
}
