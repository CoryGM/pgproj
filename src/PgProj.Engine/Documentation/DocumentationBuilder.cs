using PgProj.Semantics;

namespace PgProj.Engine;

/// <summary>
/// Builds the documentation of a <see cref="DatabaseModel"/> (G-159): the data dictionary (tables with their columns, keys, constraints, and indexes; views; functions and procedures; types; sequences)
/// with what each object's <c>COMMENT ON</c> text says (<see cref="DocTags"/>), and the entity relationship diagram (<see cref="ErdDiagram"/>). The model comes from the project's files or from a
/// live database, so the documentation is the same either way.
/// </summary>
internal static class DocumentationBuilder
{
    public static DatabaseDocumentation Build(DatabaseModel model, DocSource source, ErdFilter filter, DateTime generatedUtc)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(filter);

        var all = new List<DocItem>();
        T Track<T>(T item, string? comment)
            where T : DocItem
        {
            Annotate(item, DocTags.Parse(comment, KindOf(item)));
            all.Add(item);
            return item;
        }

        var constraintsByTable = model.Constraints.Values
            .Where(static constraint => !string.IsNullOrWhiteSpace(constraint.TableName))
            .GroupBy(static constraint => DatabaseModel.CreateKey(constraint.SchemaName, constraint.TableName!), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.OrderBy(static constraint => ConstraintRank(constraint.ConstraintType)).ThenBy(static constraint => constraint.Name, PgProj.Parser.SqlIdentifiers.NameComparer).ToArray(), StringComparer.OrdinalIgnoreCase);
        var indexesByTable = model.Indexes.Values
            .Where(static index => !string.IsNullOrWhiteSpace(index.TableName))
            .GroupBy(static index => DatabaseModel.CreateKey(index.SchemaName, index.TableName!), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.OrderBy(static index => index.Name, PgProj.Parser.SqlIdentifiers.NameComparer).ToArray(), StringComparer.OrdinalIgnoreCase);

        var triggersByTable = model.Triggers.Values
            .Where(static trigger => !string.IsNullOrWhiteSpace(trigger.TableName))
            .GroupBy(static trigger => DatabaseModel.CreateKey(trigger.SchemaName, trigger.TableName!), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.OrderBy(static trigger => trigger.Name, PgProj.Parser.SqlIdentifiers.NameComparer).ToArray(), StringComparer.OrdinalIgnoreCase);

        var policiesByTable = model.Policies.Values
            .Where(static policy => !string.IsNullOrWhiteSpace(policy.TableName))
            .GroupBy(static policy => DatabaseModel.CreateKey(policy.SchemaName, policy.TableName!), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.OrderBy(static policy => policy.Name, PgProj.Parser.SqlIdentifiers.NameComparer).ToArray(), StringComparer.OrdinalIgnoreCase);

        var tables = new List<DocTable>();
        foreach (var table in model.Tables.Values.Where(table => filter.Keeps(table.SchemaName)))
        {
            var schema = SchemaOf(table.SchemaName);
            var qualified = $"{schema}.{table.Name}";
            constraintsByTable.TryGetValue(table.Key, out var constraints);
            constraints ??= [];
            indexesByTable.TryGetValue(table.Key, out var indexes);
            indexes ??= [];
            triggersByTable.TryGetValue(table.Key, out var triggers);
            triggers ??= [];
            policiesByTable.TryGetValue(table.Key, out var policies);
            policies ??= [];

            var primaryKey = constraints.SelectMany(static constraint => constraint.PrimaryKeyColumns).ToHashSet(PgProj.Parser.SqlIdentifiers.NameComparer);
            var unique = constraints.Where(static constraint => string.Equals(constraint.ConstraintType, "UNIQUE", StringComparison.OrdinalIgnoreCase))
                .SelectMany(static constraint => constraint.UniqueColumns).ToHashSet(PgProj.Parser.SqlIdentifiers.NameComparer);
            var foreignKeys = constraints.Where(static constraint => string.Equals(constraint.ConstraintType, "FOREIGN KEY", StringComparison.OrdinalIgnoreCase)).ToArray();
            var foreign = foreignKeys.SelectMany(static constraint => constraint.ForeignKeyColumns).ToHashSet(PgProj.Parser.SqlIdentifiers.NameComparer);

            var columns = new List<DocColumn>();
            var position = 0;
            foreach (var column in table.Columns)
            {
                position++;
                var references = foreignKeys
                    .SelectMany(constraint => constraint.ForeignKeyColumns
                        .Select((name, ordinal) => (name, ordinal, constraint))
                        .Where(entry => string.Equals(entry.name, column.Name, StringComparison.Ordinal))
                        .Select(entry => $"{ReferencedTable(entry.constraint)}({(entry.ordinal < entry.constraint.ForeignKeyReferencedColumns.Count ? entry.constraint.ForeignKeyReferencedColumns[entry.ordinal] : "?")})"))
                    .ToArray();
                columns.Add(Track(new DocColumn
                {
                    Name = column.Name,
                    QualifiedName = $"{qualified}.{column.Name}",
                    Position = position,
                    Type = TypeText(column),
                    Nullable = column.AllowsNull && !primaryKey.Contains(column.Name),
                    Default = column.IsGenerated ? null : column.ServerRenderedDefault ?? column.RawDefaultExpression,
                    Generated = column.IsGenerated ? column.ServerRenderedGeneratedExpression ?? column.GeneratedExpression : null,
                    Identity = column.IdentityGeneration,
                    PrimaryKey = primaryKey.Contains(column.Name),
                    ForeignKey = foreign.Contains(column.Name),
                    Unique = unique.Contains(column.Name),
                    References = references.Length == 0 ? null : references,
                }, column.Comment));
            }

            var item = Track(new DocTable
            {
                Name = table.Name,
                QualifiedName = qualified,
                Schema = schema,
                Kind = table.IsTypedTable ? "typed table" : table.PartitionOfTableName is not null ? "partition" : table.PartitionStrategy is not null ? "partitioned table" : "table",
                PartitionedBy = table.PartitionStrategy is null ? null : $"{table.PartitionStrategy.ToUpperInvariant()} ({string.Join(", ", table.PartitionKeyElements.Select(static element => element.Name is null && element.RawExpression is null ? "?" : element.ToSql()))})",
                PartitionOf = table.PartitionOfTableName is null ? null : $"{SchemaOf(table.PartitionOfSchemaName)}.{table.PartitionOfTableName}",
                Columns = columns,
                Constraints = constraints.Select(constraint => Track(ConstraintDoc(constraint, qualified), constraint.Comment)).ToArray(),
                Triggers = triggers.Select(trigger => Track(TriggerDoc(trigger, qualified), trigger.Comment)).ToArray(),
                Policies = policies.Select(policy => Track(PolicyDoc(policy, qualified), policy.Comment)).ToArray(),
                RowSecurity = table.IsRowSecurityEnabled,
                Indexes = indexes.Select(index => Track(new DocIndex
                {
                    Name = index.Name,
                    QualifiedName = $"{qualified}.{index.Name}",
                    Method = index.AccessMethod,
                    Unique = index.IsUnique,
                    Keys = index.KeyElements.Count > 0
                        ? index.KeyElements.Select(static element => (element.Name ?? element.RawExpression ?? "?") + (element.IsDescending ? " DESC" : string.Empty)).ToArray()
                        : index.KeyColumns.ToArray(),
                    Include = index.IncludeColumns.Count == 0 ? null : index.IncludeColumns,
                    Predicate = index.ServerRenderedPredicate ?? index.RawPredicateExpression,
                }, index.Comment)).ToArray(),
            }, table.Comment);
            tables.Add(item);
        }

        var views = model.Views.Values.Where(view => filter.Keeps(view.SchemaName)).Select(view =>
        {
            var schema = SchemaOf(view.SchemaName);
            // A database's list is "name type" entries from the catalog; a project has no server, so its columns are worked out from the query (G-306).
            var outputColumns = view.ServerOutputColumns.Count > 0
                ? view.ServerOutputColumns.Select(ViewOutputColumns.SplitEntry).ToList()
                : view.OutputColumnNames.Count > 0
                    ? view.OutputColumnNames.Select((name, ordinal) => (Name: name, Type: ordinal < view.OutputColumnTypes.Count ? view.OutputColumnTypes[ordinal] : (string?)null)).ToList()
                    : [.. ViewOutputColumns.Derive(view, model)];
            return Track(new DocView
            {
                Name = view.Name,
                QualifiedName = $"{schema}.{view.Name}",
                Schema = schema,
                Materialized = view.IsMaterialized,
                Columns = outputColumns.Select(column => new DocViewColumn(column.Name, column.Type, view.ColumnComments.GetValueOrDefault(column.Name))).ToArray(),
                Definition = (view.ServerRenderedBody ?? view.RawBodyExpression)?.Trim(),
            }, view.Comment);
        }).ToList();

        var routines = new List<DocRoutine>();
        foreach (var function in model.Functions.Values.Where(function => filter.Keeps(function.SchemaName)))
        {
            routines.Add(Track(RoutineDoc(function.Name, function.SchemaName, "function", function.Signature, function.Language), function.Comment));
        }

        foreach (var procedure in model.Procedures.Values.Where(procedure => filter.Keeps(procedure.SchemaName)))
        {
            routines.Add(Track(RoutineDoc(procedure.Name, procedure.SchemaName, "procedure", procedure.Signature, procedure.Language), procedure.Comment));
        }

        var types = model.Types.Values
            .Where(type => filter.Keeps(type.SchemaName) && !string.Equals(type.TypeKind, "extension", StringComparison.OrdinalIgnoreCase))
            .Select(type =>
            {
                var schema = SchemaOf(type.SchemaName);
                return Track(new DocType
                {
                    Name = type.Name,
                    QualifiedName = $"{schema}.{type.Name}",
                    Schema = schema,
                    Kind = type.EnumLabels.Count > 0 ? "enum" : type.CompositeFields.Count > 0 ? "composite" : type.DomainBaseType is not null ? "domain" : type.RangeOptions is not null ? "range" : type.TypeKind,
                    Labels = type.EnumLabels.Count == 0 ? null : type.EnumLabels,
                    Fields = type.CompositeFields.Count == 0 ? null : type.CompositeFields.Select(field => new DocViewColumn(field.Name, field.RawType ?? field.DataType, type.ColumnComments.GetValueOrDefault(field.Name))).ToArray(),
                    BaseType = type.DomainBaseType,
                }, type.Comment);
            }).ToList();

        var sequences = model.Sequences.Values
            .Where(sequence => filter.Keeps(sequence.SchemaName) && !sequence.IsIdentityOwned)
            .Select(sequence =>
            {
                var schema = SchemaOf(sequence.SchemaName);
                return Track(new DocSequence
                {
                    Name = sequence.Name,
                    QualifiedName = $"{schema}.{sequence.Name}",
                    Schema = schema,
                    DataType = sequence.DataType,
                    Increment = sequence.IncrementBy,
                    Minimum = sequence.MinValue,
                    Maximum = sequence.MaxValue,
                    Start = sequence.StartValue,
                    Cycle = sequence.Cycle,
                    OwnedBy = sequence.OwnedByTable is null ? null : $"{sequence.OwnedByTable}{(sequence.OwnedByColumn is null ? string.Empty : "." + sequence.OwnedByColumn)}",
                }, sequence.Comment);
            }).ToList();

        var schemas = tables.Select(static item => item.Schema)
            .Concat(views.Select(static item => item.Schema))
            .Concat(routines.Select(static item => item.Schema))
            .Concat(types.Select(static item => item.Schema))
            .Concat(sequences.Select(static item => item.Schema))
            .Concat(model.SchemaDefinitions.Values.Where(definition => filter.Keeps(definition.Name)).Select(static definition => definition.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(name => Track(
                new DocSchema
                {
                    Name = name,
                    QualifiedName = name,
                    Tables = Sorted(tables.Where(item => Same(item.Schema, name))),
                    Views = Sorted(views.Where(item => Same(item.Schema, name))),
                    Routines = routines.Where(item => Same(item.Schema, name)).OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase).ThenBy(static item => item.Signature, StringComparer.OrdinalIgnoreCase).ToArray(),
                    Types = Sorted(types.Where(item => Same(item.Schema, name))),
                    Sequences = Sorted(sequences.Where(item => Same(item.Schema, name))),
                },
                model.SchemaDefinitions.TryGetValue(SchemaKeys.Create(name), out var schemaDefinition) ? schemaDefinition.Comment : null))
            .ToArray();

        ResolveSeeAlso(all);

        var problems = all
            .SelectMany(static item => (item.Issues ?? []).Select(issue => new DocProblem(item.QualifiedName, issue.Severity, issue.Tag, issue.Message)))
            .ToArray();
        var counts = new DocCounts(
            schemas.Length,
            tables.Count,
            tables.Sum(static table => table.Columns.Count),
            views.Count,
            routines.Count,
            types.Count,
            sequences.Count,
            tables.Sum(static table => table.Indexes.Count),
            tables.Sum(static table => table.Constraints.Count),
            tables.Sum(static table => table.Triggers.Count),
            tables.Sum(static table => table.Policies.Count),
            problems.Count(static problem => problem.Severity == DocSeverity.Error),
            problems.Count(static problem => problem.Severity == DocSeverity.Warning));

        return new DatabaseDocumentation
        {
            GeneratedUtc = generatedUtc,
            Source = source,
            SchemasOnly = filter.Exclude ? [] : filter.Schemas,
            SchemasExcluded = filter.Exclude ? filter.Schemas : [],
            Counts = counts,
            Schemas = schemas,
            Erd = ErdDiagram.Build(model, filter),
            Problems = problems,
        };
    }

    /// <summary>The schemas that have an object the documentation covers, in order: what <c>--schemas</c> and <c>--exclude-schemas</c> can name.</summary>
    public static IReadOnlyList<string> SchemaNames(DatabaseModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        return model.Tables.Values.Select(static item => item.SchemaName)
            .Concat(model.Views.Values.Select(static item => item.SchemaName))
            .Concat(model.Functions.Values.Select(static item => item.SchemaName))
            .Concat(model.Procedures.Values.Select(static item => item.SchemaName))
            .Concat(model.Types.Values.Where(static item => !string.Equals(item.TypeKind, "extension", StringComparison.OrdinalIgnoreCase)).Select(static item => item.SchemaName))
            .Concat(model.Sequences.Values.Select(static item => item.SchemaName))
            .Concat(model.SchemaDefinitions.Values.Select(static item => item.Name))
            .Select(SchemaOf)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>The order a table's constraints are listed in: the primary key, then unique constraints, foreign keys, checks, and the rest.</summary>
    private static int ConstraintRank(string? type)
        => type?.ToUpperInvariant() switch
        {
            "PRIMARY KEY" => 0,
            "UNIQUE" => 1,
            "FOREIGN KEY" => 2,
            "CHECK" => 3,
            _ => 4,
        };

    /// <summary>The kind of object an item documents, as <see cref="DocTags.AppliesTo"/> names it.</summary>
    private static string KindOf(DocItem item)
        => item switch
        {
            DocSchema => "schema",
            DocTable => "table",
            DocView => "view",
            DocColumn => "column",
            DocConstraint => "constraint",
            DocTrigger => "trigger",
            DocPolicy => "policy",
            DocIndex => "index",
            DocRoutine => "routine",
            DocType => "type",
            _ => "sequence",
        };

    private static T[] Sorted<T>(IEnumerable<T> items)
        where T : DocItem
        => items.OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string SchemaOf(string? schemaName) => string.IsNullOrWhiteSpace(schemaName) ? "public" : schemaName;

    private static string TypeText(ColumnDefinition column)
    {
        var signature = column.GetComparableTypeSignature();
        return Friendly(string.IsNullOrWhiteSpace(signature) ? column.RawType ?? column.DataType ?? "unknown" : signature);
    }

    /// <summary>PostgreSQL's internal type names written as people write them (<c>int8</c> is <c>bigint</c>), and a type in <c>pg_catalog</c> or <c>public</c> without its schema.</summary>
    internal static string Friendly(string type)
    {
        var name = type.Trim();
        foreach (var prefix in new[] { "pg_catalog.", "public." })
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[prefix.Length..];
                break;
            }
        }

        foreach (var (internalName, friendly) in new[] { ("int8", "bigint"), ("int4", "integer"), ("int2", "smallint"), ("bool", "boolean"), ("float8", "double precision"), ("float4", "real") })
        {
            if (name.StartsWith(internalName, StringComparison.OrdinalIgnoreCase) && (name.Length == internalName.Length || !char.IsLetterOrDigit(name[internalName.Length])))
            {
                return friendly + name[internalName.Length..];
            }
        }

        return name;
    }

    private static string ReferencedTable(ConstraintDefinition constraint)
    {
        var referenced = (constraint.ForeignKeyReferencedTable ?? string.Empty).Replace("\"", string.Empty, StringComparison.Ordinal);
        return referenced.Contains('.', StringComparison.Ordinal)
            ? referenced
            : $"{SchemaOf(constraint.ForeignKeyReferencedSchemaName ?? constraint.SchemaName)}.{referenced}";
    }

    /// <summary>A constraint's name qualified by its table, as <c>app.orders.constraint:orders_pkey</c>: the prefix tells it from an index of the same name (a primary key has both).</summary>
    /// <summary>A trigger's name qualified by its table, as <c>app.orders.trigger:audit_orders</c>.</summary>
    /// <summary>A policy's name qualified by its table, as <c>app.orders.policy:own_rows</c>.</summary>
    private static DocPolicy PolicyDoc(PolicyDefinition policy, string tableQualifiedName)
        => new()
        {
            Name = policy.Name,
            QualifiedName = $"{tableQualifiedName}.policy:{policy.Name}",
            Command = policy.Command.ToUpperInvariant(),
            Permissive = policy.IsPermissive,
            Roles = policy.Roles.ToArray(),
            Using = string.IsNullOrWhiteSpace(policy.RawUsingExpression) ? null : policy.RawUsingExpression.Trim(),
            WithCheck = string.IsNullOrWhiteSpace(policy.RawWithCheckExpression) ? null : policy.RawWithCheckExpression.Trim(),
        };

    private static DocTrigger TriggerDoc(TriggerDefinition trigger, string tableQualifiedName)
        => new()
        {
            Name = trigger.Name,
            QualifiedName = $"{tableQualifiedName}.trigger:{trigger.Name}",
            Timing = trigger.Timing?.ToUpperInvariant(),
            Events = trigger.Events.Select(static name => name.ToUpperInvariant()).ToArray(),
            Level = trigger.IsRowLevel ? "row" : "statement",
            Function = trigger.FunctionName is null ? null : $"{trigger.FunctionName}({string.Join(", ", trigger.FunctionArguments)})",
            When = string.IsNullOrWhiteSpace(trigger.RawWhenCondition) ? null : trigger.RawWhenCondition.Trim(),
            ConstraintTrigger = trigger.IsConstraintTrigger,
        };

    private static DocConstraint ConstraintDoc(ConstraintDefinition constraint, string tableQualifiedName)
    {
        var qualifiedName = $"{tableQualifiedName}.constraint:{constraint.Name}";
        var type = (constraint.ConstraintType ?? "CONSTRAINT").ToUpperInvariant();
        return type switch
        {
            "PRIMARY KEY" => new DocConstraint { Name = constraint.Name, QualifiedName = qualifiedName, Type = type, Columns = constraint.PrimaryKeyColumns },
            "UNIQUE" => new DocConstraint { Name = constraint.Name, QualifiedName = qualifiedName, Type = type, Columns = constraint.UniqueColumns },
            "FOREIGN KEY" => new DocConstraint
            {
                Name = constraint.Name,
                QualifiedName = qualifiedName,
                Type = type,
                Columns = constraint.ForeignKeyColumns,
                Definition = $"{ReferencedTable(constraint)}({string.Join(", ", constraint.ForeignKeyReferencedColumns)})",
                OnUpdate = Action(constraint.ForeignKeyUpdateAction),
                OnDelete = Action(constraint.ForeignKeyDeleteAction) is { } onDelete && constraint.ForeignKeyDeleteSetColumns.Count > 0
                    ? $"{onDelete} ({string.Join(", ", constraint.ForeignKeyDeleteSetColumns)})"
                    : Action(constraint.ForeignKeyDeleteAction),
                Deferrable = constraint.IsDeferrable,
            },
            "CHECK" => new DocConstraint { Name = constraint.Name, QualifiedName = qualifiedName, Type = type, Definition = constraint.RawCheckExpression ?? constraint.NormalizedCheckExpression },
            _ => new DocConstraint
            {
                Name = constraint.Name,
                QualifiedName = qualifiedName,
                Type = type,
                Definition = string.Join(", ", constraint.ExclusionElements.Select(static element => $"{element.Name ?? element.RawExpression} WITH {element.OperatorName}")),
            },
        };
    }

    /// <summary>A foreign key's referential action, left out when it is the default (<c>NO ACTION</c>).</summary>
    private static string? Action(string? action)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            return null;
        }

        // The model keeps pg_constraint's one-letter codes for a constraint read from a database, and the words for one written in a file.
        var text = action.Trim().ToUpperInvariant() switch
        {
            "A" => "NO ACTION",
            "R" => "RESTRICT",
            "C" => "CASCADE",
            "N" => "SET NULL",
            "D" => "SET DEFAULT",
            var words => words,
        };
        return text == "NO ACTION" ? null : text;
    }

    private static DocRoutine RoutineDoc(string name, string? schemaName, string kind, PgProj.Parser.RoutineSignature? signature, string? language)
    {
        var schema = SchemaOf(schemaName);
        var arguments = signature is null
            ? string.Empty
            : string.Join(", ", signature.Parameters.Where(static parameter => parameter.IsArgument).Select(static parameter => Friendly(parameter.DataType ?? "text")));
        return new DocRoutine
        {
            Name = name,
            QualifiedName = $"{schema}.{name}({arguments})",
            Schema = schema,
            Kind = kind,
            Signature = $"{name}({string.Join(", ", (signature?.Parameters ?? []).Select(static parameter => string.Join(' ', new[] { string.IsNullOrWhiteSpace(parameter.Mode) || string.Equals(parameter.Mode, "IN", StringComparison.OrdinalIgnoreCase) ? null : parameter.Mode!.ToUpperInvariant(), parameter.Name, Friendly(parameter.DataType ?? "text") }.Where(static part => !string.IsNullOrWhiteSpace(part)))))})",
            Returns = kind == "function" ? (signature?.ReturnType is { } returns ? (signature.ReturnsSet ? "SETOF " : string.Empty) + Friendly(returns) : null) : null,
            Language = signature?.Language ?? language,
            Volatility = signature?.Volatility,
            SecurityDefiner = signature?.IsSecurityDefiner ?? false,
        };
    }

    private static void Annotate(DocItem item, DocAnnotations annotations)
    {
        item.Description = annotations.Description;
        item.BusinessName = annotations.BusinessName;
        item.Owner = annotations.Owner;
        item.Classification = annotations.Classification;
        item.Pii = annotations.Pii;
        item.Deprecated = annotations.Deprecated;
        item.SinceVersion = annotations.SinceVersion;
        item.SinceDate = annotations.SinceDate;
        item.RetentionMonths = annotations.RetentionMonths;
        item.RetentionDate = annotations.RetentionDate;
        item.Examples = annotations.Examples.Count == 0 ? null : annotations.Examples;
        item.SeeAlso = annotations.SeeAlso.Count == 0 ? null : annotations.SeeAlso.Select(static value => new DocLink(value, null)).ToArray();
        item.Custom = annotations.Custom.Count == 0 ? null : annotations.Custom;
        item.InvalidTags = annotations.Invalid.Count == 0 ? null : annotations.Invalid;
        item.Issues = annotations.Issues.Count == 0 ? null : annotations.Issues;
    }

    /// <summary>A <c>@see-also</c> value that names an object in the document becomes a link to it; anything else (a URL, an object that is not documented) stays as text.</summary>
    private static void ResolveSeeAlso(List<DocItem> all)
    {
        var known = all.Select(static item => item.QualifiedName).ToHashSet(PgProj.Parser.SqlIdentifiers.NameComparer);
        foreach (var item in all.Where(static item => item.SeeAlso is not null))
        {
            item.SeeAlso = item.SeeAlso!.Select(link =>
            {
                if (known.Contains(link.Value))
                {
                    return link with { Target = link.Value };
                }

                return known.Contains($"public.{link.Value}") ? link with { Target = $"public.{link.Value}" } : link;
            }).ToArray();
        }
    }
}
