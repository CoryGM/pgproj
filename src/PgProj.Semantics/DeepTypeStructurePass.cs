using System.Text;

namespace PgProj.Semantics;

internal sealed class DeepTypeStructurePass
{
    /// <summary>
    /// Structure comes from <see cref="PgProj.Parser.SqlSyntaxTree.Types"/>: one <c>TypeNode</c> per genuine <c>CREATE TYPE</c> statement
    /// (pg_query's AST, G-78), carrying its enum labels, its composite fields, and its range options as the parser read them (G-90 part 4).
    /// A composite field's type and a range option's value are the text as written (<c>integer</c>, not pg_query's internal
    /// <c>pg_catalog.int4</c>), since that is what is deployed. Before G-90, this pass cut the body out of the statement's text with a
    /// regular expression and split it on commas character by character, so a comment inside the body broke fields and labels apart.
    /// </summary>
    public DatabaseModel Execute(DatabaseModel model, IEnumerable<PgProj.Parser.SqlSyntaxTree> syntaxTrees)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(syntaxTrees);

        var types = new Dictionary<string, TypeDefinition>(model.Types, PgProj.Parser.SqlIdentifiers.NameComparer);
        var diagnostics = new List<SemanticDiagnostic>(model.Diagnostics);

        foreach (var syntaxTree in syntaxTrees)
        {
            foreach (var typeNode in syntaxTree.Types)
            {
                var typeKey = DatabaseModel.CreateKey(typeNode.SchemaName, typeNode.Name ?? string.Empty);
                if (typeNode.Form is null || !types.TryGetValue(typeKey, out var typeDefinition))
                {
                    // A type with no form has no structure (a shell or base type is not a TypeNode since G-241), and every TypeNode was already declared by DeclarationPass.
                    continue;
                }

                var updatedType = typeNode.Form switch
                {
                    "composite" => MapCompositeType(typeDefinition, typeNode, diagnostics) with { IsComposite = true },
                    "range" => WithComparableSubtype(MapRangeType(typeDefinition, typeNode.RangeOptions ?? []), typeNode.RangeSubtype),
                    "enum" => typeDefinition with { CompositeFields = [], RangeOptions = null, EnumLabels = typeNode.EnumLabels, IsEnum = true },
                    _ => typeDefinition,
                };

                types[typeKey] = WithComparableFieldTypes(updatedType, typeNode.CompositeFieldTypes) with { HasTypedTables = typeNode.HasTypedTables };
            }

            foreach (var domainNode in syntaxTree.Domains)
            {
                var typeKey = DatabaseModel.CreateKey(domainNode.SchemaName, domainNode.Name ?? string.Empty);
                if (!types.TryGetValue(typeKey, out var typeDefinition))
                {
                    continue;
                }

                types[typeKey] = MapDomainStructure(model, typeDefinition, domainNode);
            }
        }

        return model with
        {
            Types = types,
            Diagnostics = diagnostics,
        };
    }

    /// <summary>
    /// A domain's structure comes straight from <see cref="PgProj.Parser.DomainNode"/>'s own AST-derived fields
    /// (G-77) - unlike composite/range/enum types above, there is no text re-parsing to do: pg_query's tree
    /// already gives the base type, DEFAULT, NOT NULL, and each CHECK constraint directly.
    /// </summary>
    private static TypeDefinition MapDomainStructure(DatabaseModel model, TypeDefinition typeDefinition, PgProj.Parser.DomainNode domainNode)
    {
        var referencedTypes = new HashSet<string>(typeDefinition.ReferencedTypes, PgProj.Parser.SqlIdentifiers.NameComparer);
        if (!string.IsNullOrWhiteSpace(domainNode.BaseType))
        {
            // The parser's name, already folded as PostgreSQL folds it: a quoted name keeps its case (G-286). NormalizeTypeReference reads
            // written text, with its quotes, and would lower-case a quoted name that reaches it without them.
            referencedTypes.Add(domainNode.BaseType.Trim());
        }

        // A domain's DEFAULT (unlike its CHECK constraints, needed below for the canonical comparison anyway) keeps
        // only raw text, not a tree, so a function call there does not become a dependency; DEFAULT is almost
        // always a literal, and getting this wrong for the rare function-valued default is a missed deploy-order
        // edge, not a wrong one.
        var referencedFunctions = new HashSet<string>(typeDefinition.ReferencedFunctions, PgProj.Parser.SqlIdentifiers.NameComparer);

        var referencedOperators = new List<string>(typeDefinition.ReferencedOperators);
        var checkConstraints = new List<DomainCheckConstraintDefinition>();
        foreach (var check in domainNode.CheckConstraints)
        {
            var checkReferences = ExpressionAstReader.Collect(check.ExpressionAst);
            referencedOperators.AddRange(OperatorReferences.Resolve(model, checkReferences.Operators, typeDefinition.SchemaName));
            foreach (var function in checkReferences.Functions)
            {
                referencedFunctions.Add(function);
            }

            checkConstraints.Add(new DomainCheckConstraintDefinition(check.Name, check.RawExpression) { ExpressionAst = check.ExpressionAst, IsNotValid = check.IsNotValid });
        }

        return typeDefinition with
        {
            DomainBaseType = domainNode.BaseType,
            DomainWrittenBaseType = domainNode.WrittenBaseType,
            DomainBaseTypeModifiers = domainNode.BaseTypeModifiers,
            DomainBaseTypeArrayDimensions = domainNode.BaseTypeArrayDimensions,
            DomainIntervalFields = domainNode.BaseTypeIntervalFields,
            DomainRawDefaultExpression = domainNode.RawDefaultExpression,
            DomainIsNotNull = domainNode.IsNotNull,
            DomainCollationName = domainNode.CollationName,
            DomainCheckConstraints = AssignCheckConstraintNames(domainNode.Name ?? typeDefinition.Name, checkConstraints),
            ReferencedTypes = referencedTypes.ToArray(),
            ReferencedFunctions = referencedFunctions.ToArray(),
            ReferencedOperators = OperatorReferences.Merge([], referencedOperators),
        };
    }

    /// <summary>
    /// Gives every CHECK constraint declared without a name the default name PostgreSQL assigns (confirmed against
    /// a real server, G-77): <c>&lt;domain&gt;_check</c>, then <c>&lt;domain&gt;_check1</c>, <c>_check2</c>, ...,
    /// skipping any name an explicit constraint on the same domain already uses. Unlike table constraints
    /// (<see cref="PgProj.Parser.ConstraintNameAssigner"/>), a domain's own CHECK constraints are only ever
    /// declared together in its one <c>CREATE DOMAIN</c> statement, so PostgreSQL's numbering depends only on
    /// order within this list - there is no cross-file ambiguity to resolve.
    /// </summary>
    private static IReadOnlyList<DomainCheckConstraintDefinition> AssignCheckConstraintNames(string domainName, IReadOnlyList<DomainCheckConstraintDefinition> checks)
    {
        if (checks.Count == 0)
        {
            return checks;
        }

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var check in checks)
        {
            if (!string.IsNullOrWhiteSpace(check.Name))
            {
                used.Add(check.Name);
            }
        }

        var result = new List<DomainCheckConstraintDefinition>(checks.Count);
        var suffix = 0;
        foreach (var check in checks)
        {
            if (!string.IsNullOrWhiteSpace(check.Name))
            {
                result.Add(check);
                continue;
            }

            string candidate;
            do
            {
                candidate = suffix == 0 ? $"{domainName}_check" : $"{domainName}_check{suffix.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
                suffix++;
            }
            while (!used.Add(candidate));

            result.Add(check with { Name = candidate, IsNameGenerated = true });
        }

        return result;
    }

    /// <summary>
    /// Gives each composite field the canonical type pg_query's parse of it yields (G-99). The field text stays as written for
    /// deployment; this is only what comparison uses.
    /// </summary>
    private static TypeDefinition WithComparableFieldTypes(TypeDefinition type, IReadOnlyList<PgProj.Parser.CompositeFieldTypeNode> fieldTypes)
    {
        if (fieldTypes.Count == 0 || type.CompositeFields.Count == 0)
        {
            return type;
        }

        var byName = new Dictionary<string, PgProj.Parser.CompositeFieldTypeNode>(PgProj.Parser.SqlIdentifiers.NameComparer);
        foreach (var fieldType in fieldTypes)
        {
            byName.TryAdd(fieldType.Name, fieldType);
        }

        return type with
        {
            CompositeFields = type.CompositeFields
                .Select(field => byName.TryGetValue(field.Name, out var fieldType) && fieldType.BaseType is not null
                    ? field with
                    {
                        ComparableType = ColumnTypeNormalizer.BuildComparableTypeSignature(fieldType.BaseType, fieldType.Modifiers, fieldType.ArrayDimensions, fieldType.IntervalFields),
                    }
                    : field)
                .ToArray(),
        };
    }

    private static TypeDefinition MapCompositeType(
        TypeDefinition typeDefinition,
        PgProj.Parser.TypeNode typeNode,
        ICollection<SemanticDiagnostic> diagnostics)
    {
        var fields = new List<CompositeTypeFieldDefinition>();
        var referencedTypes = new HashSet<string>(typeDefinition.ReferencedTypes, PgProj.Parser.SqlIdentifiers.NameComparer);

        foreach (var field in typeNode.CompositeFieldTypes)
        {
            if (string.IsNullOrWhiteSpace(field.RawType))
            {
                diagnostics.Add(SemanticDiagnostics.Warning(
                    $"Type '{typeDefinition.Key}' contains a composite field '{field.Name}' whose type could not be read.",
                    typeDefinition.Location,
                    nameof(DeepTypeStructurePass)));
                continue;
            }

            var normalizedType = NormalizeTypeReference(field.RawType);
            fields.Add(new CompositeTypeFieldDefinition(field.Name, normalizedType, field.RawType.Trim()) { CollationName = field.CollationName });

            if (!string.IsNullOrWhiteSpace(normalizedType))
            {
                referencedTypes.Add(normalizedType);
            }
        }

        return typeDefinition with
        {
            CompositeFields = fields,
            RangeOptions = null,
            EnumLabels = [],
            ReferencedTypes = referencedTypes.ToArray(),
        };
    }

    /// <summary>
    /// Gives a range's subtype the canonical type pg_query's parse of it yields (G-115), as a composite field gets (G-99). Without its type
    /// modifiers: <c>pg_range</c> keeps no modifier, so <c>subtype = numeric(10,2)</c> is created as a range over <c>numeric</c>.
    /// </summary>
    private static TypeDefinition WithComparableSubtype(TypeDefinition type, PgProj.Parser.CompositeFieldTypeNode? subtype)
        => type.RangeOptions is { } options && subtype?.BaseType is not null
            ? type with
            {
                RangeOptions = options with
                {
                    ComparableSubtype = ColumnTypeNormalizer.BuildComparableTypeSignature(subtype.BaseType, [], subtype.ArrayDimensions, intervalFields: null),
                },
            }
            : type;

    private static TypeDefinition MapRangeType(TypeDefinition typeDefinition, IReadOnlyList<KeyValuePair<string, string>> options)
    {
        string? subtype = null;
        string? subtypeOperatorClass = null;
        string? multirangeTypeName = null;
        string? collationName = null;
        string? canonicalFunction = null;
        string? subtypeDiffFunction = null;

        var referencedTypes = new HashSet<string>(typeDefinition.ReferencedTypes, PgProj.Parser.SqlIdentifiers.NameComparer);
        var referencedFunctions = new HashSet<string>(typeDefinition.ReferencedFunctions, PgProj.Parser.SqlIdentifiers.NameComparer);

        foreach (var (key, value) in options)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            switch (key.ToUpperInvariant())
            {
                case "SUBTYPE":
                    subtype = NormalizeTypeReference(value);
                    if (!string.IsNullOrWhiteSpace(subtype))
                    {
                        referencedTypes.Add(subtype);
                    }

                    break;
                // An operator class, collation, or function is kept as written, quotes and all, since it is deployed as written: without its
                // quotes "C" was deployed as C, which PostgreSQL folds to c (G-115). The diff compares each as PostgreSQL reads it.
                case "SUBTYPE_OPCLASS":
                    subtypeOperatorClass = value.Trim();
                    break;
                case "COLLATION":
                    collationName = value.Trim();
                    break;
                case "CANONICAL":
                    canonicalFunction = value.Trim();
                    if (!string.IsNullOrWhiteSpace(canonicalFunction))
                    {
                        referencedFunctions.Add(NormalizeIdentifier(canonicalFunction));
                    }

                    break;
                case "MULTIRANGE_TYPE_NAME":
                    multirangeTypeName = value.Trim();
                    break;
                case "SUBTYPE_DIFF":
                    subtypeDiffFunction = value.Trim();
                    if (!string.IsNullOrWhiteSpace(subtypeDiffFunction))
                    {
                        referencedFunctions.Add(NormalizeIdentifier(subtypeDiffFunction));
                    }

                    break;
            }
        }

        return typeDefinition with
        {
            CompositeFields = [],
            RangeOptions = new RangeTypeOptionsDefinition(subtype, subtypeOperatorClass, collationName, canonicalFunction, subtypeDiffFunction) { MultirangeTypeName = multirangeTypeName },
            ReferencedTypes = referencedTypes.ToArray(),
            ReferencedFunctions = referencedFunctions.ToArray(),
        };
    }

    private static string NormalizeIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return string.Empty;
        }

        var parts = identifier
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static part => NormalizeIdentifierPart(part))
            .Where(static part => !string.IsNullOrWhiteSpace(part));

        return string.Join('.', parts);
    }

    private static string NormalizeIdentifierPart(string part)
    {
        var trimmed = part.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
        {
            return trimmed[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal);
        }

        return trimmed;
    }

    private static string NormalizeTypeReference(string rawType)
    {
        if (string.IsNullOrWhiteSpace(rawType))
        {
            return string.Empty;
        }

        var text = rawType.Trim();
        var builder = new StringBuilder(text.Length);
        var inDoubleQuote = false;
        for (var index = 0; index < text.Length; index++)
        {
            var ch = text[index];
            if (ch == '"')
            {
                if (inDoubleQuote && index + 1 < text.Length && text[index + 1] == '"')
                {
                    builder.Append('"');
                    index++;
                    continue;
                }

                inDoubleQuote = !inDoubleQuote;
                continue;
            }

            builder.Append(inDoubleQuote ? ch : char.ToLowerInvariant(ch));
        }

        return string.Join(' ', builder.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
