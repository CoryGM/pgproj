using System.Text.Json;

namespace PgProj.Parser;

/// <summary>
/// Reads <c>CompositeTypeStmt</c>, <c>CreateEnumStmt</c>, <c>CreateRangeStmt</c>, <c>DefineStmt</c> (shell/base
/// types), <c>CreateDomainStmt</c>, and <c>CreateExtensionStmt</c> from pg_query's JSON AST (G-78). These were
/// previously found with regular expressions over the raw SQL text - the same risk class G-65 already found and
/// fixed for grants (a regex can match across a statement boundary, inside a comment, or inside a string literal).
/// Confirmed AST shapes (pg_query 17): a name is either a <c>RangeVar</c> (<c>schemaname</c>/<c>relname</c>, used
/// only by <c>CompositeTypeStmt</c>'s <c>typevar</c>) or a list of <c>{"String":{"sval":"..."}}</c> parts (every
/// other statement here), last part is the name, the one before it (if any) is the schema.
/// </summary>
internal static class TypeStatementReader
{
    public static TypeNode? ReadCompositeType(JsonElement element, string statementSql)
    {
        if (!element.TryGetProperty("typevar", out var typevar))
        {
            return null;
        }

        var name = ReadDirectString(typevar, "relname");
        return string.IsNullOrWhiteSpace(name)
            ? null
            : new TypeNode(name, ReadDirectString(typevar, "schemaname"), statementSql) { Form = "composite", CompositeFieldTypes = ReadCompositeFieldTypes(element, statementSql) };
    }

    /// <summary>
    /// Each field of a <c>CompositeTypeStmt</c>'s <c>coldeflist</c> (<c>ColumnDef</c> nodes, the same shape a table column has), read
    /// with the column type readers so a field compares the way a column does (G-99).
    /// </summary>
    private static IReadOnlyList<CompositeFieldTypeNode> ReadCompositeFieldTypes(JsonElement element, string statementSql)
    {
        // Each field's type as written, for deployment (G-90 part 4); paired by position with the tree's fields, and left out when the counts differ.
        var rawTypes = TypeBodyTextReader.ReadCompositeFieldTypes(statementSql);

        if (!element.TryGetProperty("coldeflist", out var columns) || columns.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var fields = new List<CompositeFieldTypeNode>();
        foreach (var item in columns.EnumerateArray())
        {
            if (!item.TryGetProperty("ColumnDef", out var column))
            {
                continue;
            }

            var fieldName = ReadDirectString(column, "colname");
            if (string.IsNullOrWhiteSpace(fieldName))
            {
                continue;
            }

            string? baseType = null;
            if (column.TryGetProperty("typeName", out var typeNameElement))
            {
                var typeParts = ReadNameParts(typeNameElement, "names");
                baseType = typeParts.Count == 0 ? null : string.Join('.', typeParts);
            }

            var details = ColumnTypeDetailsReader.Read(column);
            fields.Add(new CompositeFieldTypeNode(fieldName, baseType, details.Modifiers, details.ArrayDimensions, details.IntervalFields)
            {
                CollationName = ReadCollationName(column),
            });
        }

        if (rawTypes is not null && rawTypes.Count == fields.Count)
        {
            for (var index = 0; index < fields.Count; index++)
            {
                fields[index] = fields[index] with { RawType = rawTypes[index] };
            }
        }

        return fields;
    }

    public static TypeNode? ReadEnumType(JsonElement element, string statementSql)
        => ReadTypeNodeFromNameParts(element, "typeName", statementSql) is { } type
            ? type with { Form = "enum", EnumLabels = ReadEnumLabels(element) }
            : null;

    /// <summary>An enum's labels (<c>vals</c>, each a <c>String</c> node), in order (G-90 part 4).</summary>
    private static IReadOnlyList<string> ReadEnumLabels(JsonElement element)
    {
        var labels = new List<string>();
        if (element.TryGetProperty("vals", out var values) && values.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in values.EnumerateArray())
            {
                if (ReadNestedString(value) is { } label)
                {
                    labels.Add(label);
                }
            }
        }

        return labels;
    }

    public static TypeNode? ReadRangeType(JsonElement element, string statementSql)
        => ReadTypeNodeFromNameParts(element, "typeName", statementSql) is { } type
            ? type with { Form = "range", RangeOptions = TypeBodyTextReader.ReadRangeOptions(statementSql) ?? [], RangeSubtype = ReadRangeSubtype(element) }
            : null;

    /// <summary>The <c>subtype</c> option's <c>TypeName</c> (a <c>DefElem</c> in <c>params</c>), read as a column's type is (G-115).</summary>
    private static CompositeFieldTypeNode? ReadRangeSubtype(JsonElement element)
    {
        if (!element.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var parameter in parameters.EnumerateArray())
        {
            if (parameter.TryGetProperty("DefElem", out var definition)
                && string.Equals(ReadDirectString(definition, "defname"), "subtype", StringComparison.OrdinalIgnoreCase)
                && definition.TryGetProperty("arg", out var argument)
                && argument.TryGetProperty("TypeName", out var typeName))
            {
                var parts = ReadNameParts(typeName, "names");
                var details = ColumnTypeDetailsReader.ReadTypeName(typeName);
                return parts.Count == 0 ? null : new CompositeFieldTypeNode("subtype", string.Join('.', parts), details.Modifiers, details.ArrayDimensions, details.IntervalFields);
            }
        }

        return null;
    }

    /// <summary>The definition options of a base type that name a function (<c>CREATE TYPE</c>'s parameters, lower case as pg_query writes them).</summary>
    private static readonly HashSet<string> SupportFunctionOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "input", "output", "receive", "send", "typmod_in", "typmod_out", "analyze", "subscript",
    };

    /// <summary>
    /// <c>DefineStmt</c> is shared by several unrelated <c>CREATE</c> forms (operator, aggregate, collation, text
    /// search objects, ...), distinguished only by <c>kind</c>; only <c>OBJECT_TYPE</c> is a type: a shell type,
    /// <c>CREATE TYPE name;</c> (no <c>definition</c>), or a base type with <c>INPUT</c>/<c>OUTPUT</c> functions. Neither is
    /// modeled (G-241): the marker carries the functions the definition names, so they can be left out with the type.
    /// </summary>
    public static BaseTypeNode? ReadBaseType(JsonElement element)
    {
        if (ReadDirectString(element, "kind") != "OBJECT_TYPE")
        {
            return null;
        }

        var parts = ReadNameParts(element, "defnames");
        if (parts.Count == 0)
        {
            return null;
        }

        var functions = new List<BaseTypeSupportFunction>();
        var hasDefinition = element.TryGetProperty("definition", out var definition) && definition.ValueKind == JsonValueKind.Array && definition.GetArrayLength() > 0;
        if (hasDefinition)
        {
            foreach (var item in definition.EnumerateArray())
            {
                if (item.TryGetProperty("DefElem", out var option)
                    && ReadDirectString(option, "defname") is { } optionName
                    && SupportFunctionOptions.Contains(optionName)
                    && option.TryGetProperty("arg", out var argument)
                    && ReadFunctionNameParts(argument) is { Count: > 0 } functionParts)
                {
                    functions.Add(new BaseTypeSupportFunction(functionParts.Count > 1 ? functionParts[^2] : null, functionParts[^1]));
                }
            }
        }

        return new BaseTypeNode(parts[^1], parts.Count > 1 ? parts[^2] : null, !hasDefinition) { SupportFunctions = functions };
    }

    /// <summary>A function named in a type's definition: pg_query writes it as a <c>TypeName</c> (<c>names</c>), a <c>List</c> of name parts, or a bare <c>String</c>.</summary>
    private static List<string> ReadFunctionNameParts(JsonElement argument)
    {
        if (argument.TryGetProperty("TypeName", out var typeName))
        {
            return ReadNameParts(typeName, "names");
        }

        if (argument.TryGetProperty("List", out var list))
        {
            return ReadNameParts(list, "items");
        }

        return ReadNestedString(argument) is { Length: > 0 } single ? [single] : [];
    }

    /// <summary>
    /// Reads a <c>CreateDomainStmt</c>: its base type (with modifiers/array bounds/interval fields, via
    /// <see cref="ColumnTypeDetailsReader"/> - it reads a <c>typeName</c> node the same way a column definition
    /// does) and its <c>DEFAULT</c>/<c>NOT NULL</c>/<c>CHECK</c> constraints, a flat <c>constraints</c> array mixing
    /// all three kinds in declaration order (confirmed via scratch AST dumps, G-77). A <c>DEFAULT</c>/<c>CHECK</c>
    /// expression is sliced from the statement as written, matching a table's own CHECK (G-73); the tree is kept
    /// alongside a CHECK's text for the canonical comparison.
    /// </summary>
    public static DomainNode? ReadDomain(JsonElement element, string statementSql, int statementOffset)
    {
        var parts = ReadNameParts(element, "domainname");
        if (parts.Count == 0)
        {
            return null;
        }

        string? baseType = null;
        if (element.TryGetProperty("typeName", out var typeNameElement))
        {
            var typeParts = ReadNameParts(typeNameElement, "names");
            baseType = typeParts.Count == 0 ? null : string.Join('.', typeParts);
        }

        var typeDetails = ColumnTypeDetailsReader.Read(element);
        string? rawDefaultExpression = null;
        var isNotNull = false;
        var checkConstraints = new List<DomainCheckConstraintNode>();

        if (element.TryGetProperty("constraints", out var constraintsElement) && constraintsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var wrapper in constraintsElement.EnumerateArray())
            {
                if (!wrapper.TryGetProperty("Constraint", out var constraint))
                {
                    continue;
                }

                var location = constraint.TryGetProperty("location", out var locationElement) && locationElement.ValueKind == JsonValueKind.Number
                    ? locationElement.GetInt32() - statementOffset
                    : -1;

                switch (ReadDirectString(constraint, "contype"))
                {
                    case "CONSTR_DEFAULT":
                        rawDefaultExpression = (location >= 0 ? ConstraintIndexTextReader.TryReadDefaultExpression(statementSql, location) : null)
                            ?? (constraint.TryGetProperty("raw_expr", out var defaultExprElement) ? PgQueryParser.ReadExpressionText(statementSql, statementOffset, defaultExprElement) : null);
                        break;
                    case "CONSTR_NOTNULL":
                        isNotNull = true;
                        break;
                    case "CONSTR_CHECK":
                        var checkName = ReadDirectString(constraint, "conname");
                        var checkAst = constraint.TryGetProperty("raw_expr", out var checkExprElement) ? checkExprElement.GetRawText() : null;
                        var rawCheckExpression = (location >= 0 ? ConstraintIndexTextReader.TryReadCheckExpression(statementSql, location) : null)
                            ?? (checkExprElement.ValueKind != JsonValueKind.Undefined ? PgQueryParser.ReadExpressionText(statementSql, statementOffset, checkExprElement) : null);
                        if (rawCheckExpression is not null)
                        {
                            checkConstraints.Add(new DomainCheckConstraintNode(checkName, rawCheckExpression, checkAst));
                        }

                        break;
                }
            }
        }

        return new DomainNode(parts[^1], parts.Count > 1 ? parts[^2] : null, baseType)
        {
            WrittenBaseType = typeNameElement.ValueKind == JsonValueKind.Object ? PgQueryParser.ReadTypeText(statementSql, statementOffset, typeNameElement) : null,
            BaseTypeModifiers = typeDetails.Modifiers,
            BaseTypeArrayDimensions = typeDetails.ArrayDimensions,
            BaseTypeIntervalFields = typeDetails.IntervalFields,
            RawDefaultExpression = rawDefaultExpression,
            IsNotNull = isNotNull,
            CheckConstraints = checkConstraints,
            CollationName = ReadCollationName(element),
        };
    }

    /// <summary>
    /// <c>ALTER DOMAIN name ADD CONSTRAINT c CHECK (...) [NOT VALID]</c> (G-233), an <c>AlterDomainStmt</c> of subtype <c>C</c> whose <c>def</c> is a
    /// <c>CONSTR_CHECK</c> constraint; any other action of <c>ALTER DOMAIN</c> is null here and reported as not modeled.
    /// </summary>
    public static AlterDomainCheckNode? ReadAlterDomainCheck(JsonElement element, string statementSql, int statementOffset)
    {
        if (!IsAlterDomainCheck(element, out var constraint))
        {
            return null;
        }

        var parts = ReadNameParts(element, "typeName");
        if (parts.Count == 0)
        {
            return null;
        }

        var location = constraint.TryGetProperty("location", out var locationElement) && locationElement.ValueKind == JsonValueKind.Number
            ? locationElement.GetInt32() - statementOffset
            : -1;
        var checkExpression = constraint.TryGetProperty("raw_expr", out var checkExprElement) ? checkExprElement : default;
        var rawCheckExpression = (location >= 0 ? ConstraintIndexTextReader.TryReadCheckExpression(statementSql, location) : null)
            ?? (checkExpression.ValueKind != JsonValueKind.Undefined ? PgQueryParser.ReadExpressionText(statementSql, statementOffset, checkExpression) : null);
        if (rawCheckExpression is null)
        {
            return null;
        }

        var isNotValid = constraint.TryGetProperty("skip_validation", out var skip) && skip.ValueKind == JsonValueKind.True;
        var check = new DomainCheckConstraintNode(ReadDirectString(constraint, "conname"), rawCheckExpression, checkExpression.ValueKind != JsonValueKind.Undefined ? checkExpression.GetRawText() : null)
        {
            IsNotValid = isNotValid,
        };
        return new AlterDomainCheckNode(parts[^1], parts.Count > 1 ? parts[^2] : null, check);
    }

    /// <summary>True when an <c>AlterDomainStmt</c> is <c>ADD CONSTRAINT ... CHECK</c>, the one action modeled (G-233).</summary>
    public static bool IsAlterDomainCheck(JsonElement element, out JsonElement constraint)
    {
        constraint = default;
        return ReadDirectString(element, "subtype") == "C"
            && element.TryGetProperty("def", out var definition)
            && definition.TryGetProperty("Constraint", out constraint)
            && ReadDirectString(constraint, "contype") == "CONSTR_CHECK";
    }

    public static ExtensionNode? ReadExtension(JsonElement element, string? statementSql = null)
    {
        var name = ReadDirectString(element, "extname");
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string? schemaName = null;
        string? version = null;
        var cascade = false;
        if (element.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array)
        {
            foreach (var option in options.EnumerateArray())
            {
                if (!option.TryGetProperty("DefElem", out var definition))
                {
                    continue;
                }

                switch (ReadDirectString(definition, "defname"))
                {
                    case "schema" when definition.TryGetProperty("arg", out var argument):
                        schemaName = ReadNestedString(argument);
                        break;
                    case "new_version" when definition.TryGetProperty("arg", out var argument):
                        version = ReadNestedString(argument);
                        break;
                    case "cascade":
                        // pg_query writes CASCADE as a Boolean whose true value it may leave out; the option is only there when it was written.
                        cascade = !definition.TryGetProperty("arg", out var flag)
                            || !flag.TryGetProperty("Boolean", out var boolean)
                            || !boolean.TryGetProperty("boolval", out var value)
                            || value.ValueKind != JsonValueKind.False;
                        break;
                }
            }
        }

        // What is deployed is what the project wrote (G-207, G-213): IF NOT EXISTS, SCHEMA, VERSION, and CASCADE as written.
        var written = statementSql?.Trim().TrimEnd(';').TrimEnd();
        return new ExtensionNode(name, schemaName)
        {
            WrittenText = string.IsNullOrEmpty(written) ? null : written,
            Version = version,
            IsCascade = cascade,
        };
    }

    private static TypeNode? ReadTypeNodeFromNameParts(JsonElement element, string propertyName, string statementSql)
    {
        var parts = ReadNameParts(element, propertyName);
        return parts.Count == 0 ? null : new TypeNode(parts[^1], parts.Count > 1 ? parts[^2] : null, statementSql);
    }

    /// <summary>
    /// The collation a <c>COLLATE</c> clause names, its parts as written and joined with dots (<c>C</c>, <c>pg_catalog.C</c>, <c>app.ci</c>), the way a table column's collation
    /// is read; null when there is none (G-192). The clause is <c>collClause</c>, with or without a <c>CollateClause</c> wrapper.
    /// </summary>
    internal static string? ReadCollationName(JsonElement element)
    {
        if (!element.TryGetProperty("collClause", out var clause) || clause.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (clause.TryGetProperty("CollateClause", out var wrapped))
        {
            clause = wrapped;
        }

        var parts = ReadNameParts(clause, "collname");
        return parts.Count == 0 ? null : string.Join('.', parts);
    }

    private static List<string> ReadNameParts(JsonElement element, string propertyName)
    {
        var parts = new List<string>();
        if (!element.TryGetProperty(propertyName, out var nameElement) || nameElement.ValueKind != JsonValueKind.Array)
        {
            return parts;
        }

        foreach (var part in nameElement.EnumerateArray())
        {
            var value = ReadNestedString(part);
            if (!string.IsNullOrWhiteSpace(value))
            {
                parts.Add(value);
            }
        }

        return parts;
    }

    private static string? ReadDirectString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>A name part is <c>{"String":{"sval":"..."}}</c>.</summary>
    private static string? ReadNestedString(JsonElement element)
        => element.TryGetProperty("String", out var stringNode) && stringNode.TryGetProperty("sval", out var svalElement)
            ? svalElement.GetString()
            : null;
}
