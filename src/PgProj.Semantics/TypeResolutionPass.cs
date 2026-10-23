// Step 9 — TypeResolutionPass
// Implement the TypeResolutionPass for the semantic pipeline.
// This pass performs *type resolution* — converting raw PostgreSQL type names
// into resolved TypeDefinition references. It runs AFTER DeepDependencyPass
// and BEFORE SemanticBinder.
//
// === Goals ===
// 1. Resolve raw type names into TypeDefinition references:
//      - ColumnDefinition.RawType → ColumnDefinition.ResolvedType
//      - ConstraintDefinition type references (if any)
//      - FunctionDefinition return type + argument types
//      - ViewDefinition column types (if exposed)
// 2. Use DatabaseModel type lookup rules:
//      - schema-qualified type names
//      - unqualified type names resolved by schema or fallback search
//      - built-in PostgreSQL types (int, text, bool, etc.)
// 3. Emit diagnostics for unresolved types.
// 4. Do NOT:
//      - bind semantic references (SemanticBinder)
//      - normalize expressions (ExpressionNormalizationPass)
//      - validate expressions (ValidationPass)
//      - extract dependencies (DeepDependencyPass)
// 5. TypeResolutionPass must remain type‑resolution only.
//
// === Requirements ===
// - Create a TypeResolutionPass class with:
//       DatabaseModel Execute(DatabaseModel model)
// - For each semantic definition:
//       - Resolve column types
//       - Resolve function argument and return types
//       - Resolve constraint types (if applicable)
//       - Populate ResolvedType fields
//       - Emit diagnostics for unresolved types
// - Add helper methods:
//       - ResolveType(string? schemaName, string rawTypeName)
//       - ResolveBuiltInType(string rawTypeName)
//       - AddDiagnostic(List<SemanticDiagnostic> diagnostics, string message, string source)
//
// === Type Resolution Rules ===
// - Built-in PostgreSQL types:
//       * int, integer, bigint, smallint
//       * text, varchar, char
//       * bool, boolean
//       * numeric, decimal
//       * date, timestamp, timestamptz
//       * json, jsonb
// - Schema-qualified types:
//       * "schema.type" → direct lookup
// - Unqualified types:
//       * resolve by schema first
//       * fallback to any matching type in the model
// - Array types:
//       * "type[]" → resolve base type, mark as array
// - Domain types:
//       * treat as normal types (resolved by name)
//
// === Diagnostics ===
// - Emit errors for unresolved types:
//       * columns with unknown types
//       * functions with unknown argument/return types
//       * constraints referencing unknown types
//
// === TODO for deeper type handling (later passes) ===
// - Type coercion rules (ExpressionNormalizationPass)
// - Function argument type checking (SemanticBinder)
// - CHECK expression type validation (ExpressionNormalizationPass)
// - Domain constraint validation (ValidationPass)
//
// === Copilot Guidance ===
// Generate scaffolding and initial implementations.
// Focus on type resolution only.
// Emit diagnostics for unresolved types.
// Leave TODO comments for deeper type handling.
// Begin by generating TypeResolutionPass.cs with an Execute() method and stubs for
// ResolveType(), ResolveBuiltInType(), and AddDiagnostic().

namespace PgProj.Semantics;

internal sealed class TypeResolutionPass
{
    // Built-in type names as the parser gives them: an unquoted name is already folded to lower case, so they are matched exactly, as
    // PostgreSQL matches them. A quoted name with a capital ("Name", "Date") is never a built-in (G-275).
    private static readonly HashSet<string> SupplementalBuiltInTypeNames = new(StringComparer.Ordinal)
    {
        "bit varying",
        "daterange",
        "int4range",
        "int8range",
        "numrange",
        "time with time zone",
        "time without time zone",
        "timestamp with time zone",
        "timestamp without time zone",
        "tsmultirange",
        "tsquery",
        "tsrange",
        "tstzmultirange",
        "tstzrange",
    };

    private DatabaseModel _model = DatabaseModel.Empty;
    private SymbolTable _symbolTable = new(DatabaseModel.Empty);
    private List<SemanticDiagnostic> _diagnostics = [];
    private ISemanticNode? _currentOwner;

    // The types a table's columns name, while the table's own references are resolved: a missing one is reported for the column that names it, not
    // for the table too (G-326).
    private HashSet<string>? _columnTypes;

    public DatabaseModel Execute(DatabaseModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        _model = model;
        _symbolTable = new SymbolTable(model);
        _diagnostics = [];

        var tables = model.Tables.Values
            .Select(ResolveTable)
            .ToDictionary(static definition => definition.Key, PgProj.Parser.SqlIdentifiers.NameComparer);
        var views = model.Views.Values
            .Select(ResolveView)
            .ToDictionary(static definition => definition.Key, PgProj.Parser.SqlIdentifiers.NameComparer);
        var functions = model.Functions.Values
            .Select(ResolveFunction)
            .ToDictionary(static definition => definition.Key, PgProj.Parser.SqlIdentifiers.NameComparer);
        var procedures = model.Procedures.Values
            .Select(ResolveProcedure)
            .ToDictionary(static definition => definition.Key, PgProj.Parser.SqlIdentifiers.NameComparer);
        var types = model.Types.Values
            .Select(ResolveTypeDefinition)
            .ToDictionary(static definition => definition.Key, PgProj.Parser.SqlIdentifiers.NameComparer);
        var indexes = model.Indexes.Values
            .Select(ResolveIndex)
            .ToDictionary(static definition => definition.Key, PgProj.Parser.SqlIdentifiers.NameComparer);
        var constraints = model.Constraints.Values
            .Select(ResolveConstraint)
            .ToDictionary(static definition => definition.Key, PgProj.Parser.SqlIdentifiers.NameComparer);
        var grants = model.Grants.Values
            .Select(ResolveGrant)
            .ToDictionary(static definition => definition.Key, PgProj.Parser.SqlIdentifiers.NameComparer);

        return model with
        {
            Tables = tables,
            Views = views,
            Functions = functions,
            Procedures = procedures,
            Types = types,
            Indexes = indexes,
            Constraints = constraints,
            Grants = grants,
            Diagnostics = model.Diagnostics.Concat(_diagnostics).ToList(),
        };
    }

    private TableDefinition ResolveTable(TableDefinition table)
    {
        var columns = table.Columns
            .Select(column => ResolveColumn(column, table.SchemaName))
            .ToArray();
        _columnTypes = columns.SelectMany(static column => column.ReferencedTypes).ToHashSet(PgProj.Parser.SqlIdentifiers.NameComparer);
        ResolvedTypeSet resolvedTypes;
        try
        {
            resolvedTypes = ResolveTypeReferences(table, table.ReferencedTypes, table.SchemaName);
        }
        finally
        {
            _columnTypes = null;
        }

        return table with
        {
            Columns = columns,
            ReferencedTypes = resolvedTypes.Keys,
            ResolvedTypes = resolvedTypes.Nodes,
        };
    }

    private ColumnDefinition ResolveColumn(ColumnDefinition column, string? ownerSchemaName)
    {
        var references = column.ReferencedTypes.ToList();
        if (!string.IsNullOrWhiteSpace(column.RawType)
            && !references.Any(reference => string.Equals(reference, column.RawType, StringComparison.OrdinalIgnoreCase)))
        {
            references.Add(column.RawType);
        }

        var resolvedTypes = ResolveTypeReferences(column, references, column.SchemaName ?? ownerSchemaName);

        // TODO: Resolve generated-expression and default-expression result types once deeper expression typing exists.
        return column with
        {
            ReferencedTypes = resolvedTypes.Keys,
            ResolvedTypes = resolvedTypes.Nodes,
        };
    }

    private ViewDefinition ResolveView(ViewDefinition view)
    {
        var resolvedTypes = ResolveTypeReferences(view, view.ReferencedTypes, view.SchemaName);

        // TODO: Resolve projected view column/result types when explicit view output metadata is modeled.
        return view with
        {
            ReferencedTypes = resolvedTypes.Keys,
            ResolvedTypes = resolvedTypes.Nodes,
        };
    }

    private FunctionDefinition ResolveFunction(FunctionDefinition function)
    {
        var resolvedTypes = ResolveTypeReferences(function, function.ReferencedTypes, function.SchemaName);

        // TODO: Resolve explicit function parameter and return signature types when FunctionDefinition exposes them directly.
        return function with
        {
            ReferencedTypes = resolvedTypes.Keys,
            ResolvedTypes = resolvedTypes.Nodes,
        };
    }

    /// <summary>
    /// Resolves a procedure's type references as a function's are (G-68). Without it a built-in type written with its short name
    /// (<c>int2</c>, <c>float8</c>, <c>bool</c>) stayed qualified with the procedure's own schema (<c>s.int2</c>), which is not a type
    /// and never matched the <c>pg_catalog</c> form the live database reports.
    /// </summary>
    private ProcedureDefinition ResolveProcedure(ProcedureDefinition procedure)
    {
        var resolvedTypes = ResolveTypeReferences(procedure, procedure.ReferencedTypes, procedure.SchemaName);

        return procedure with
        {
            ReferencedTypes = resolvedTypes.Keys,
            ResolvedTypes = resolvedTypes.Nodes,
        };
    }

    private TypeDefinition ResolveTypeDefinition(TypeDefinition type)
    {
        var resolvedTypes = ResolveTypeReferences(type, type.ReferencedTypes, type.SchemaName);
        return type with
        {
            ReferencedTypes = resolvedTypes.Keys,
            ResolvedTypes = resolvedTypes.Nodes,
        };
    }

    private IndexDefinition ResolveIndex(IndexDefinition index)
    {
        var resolvedTypes = ResolveTypeReferences(index, index.ReferencedTypes, index.SchemaName);

        // TODO: Resolve operator-class and cast-target types for index expressions when surfaced explicitly.
        return index with
        {
            ReferencedTypes = resolvedTypes.Keys,
            ResolvedTypes = resolvedTypes.Nodes,
        };
    }

    private ConstraintDefinition ResolveConstraint(ConstraintDefinition constraint)
    {
        var resolvedTypes = ResolveTypeReferences(constraint, constraint.ReferencedTypes, constraint.SchemaName);

        // TODO: Resolve CHECK-expression and domain-related type references when richer constraint typing is modeled.
        return constraint with
        {
            ReferencedTypes = resolvedTypes.Keys,
            ResolvedTypes = resolvedTypes.Nodes,
        };
    }

    private GrantDefinition ResolveGrant(GrantDefinition grant)
    {
        var resolvedTypes = ResolveTypeReferences(grant, grant.ReferencedTypes, grant.SchemaName);
        return grant with
        {
            ReferencedTypes = resolvedTypes.Keys,
            ResolvedTypes = resolvedTypes.Nodes,
        };
    }

    private ResolvedTypeSet ResolveTypeReferences(ISemanticNode owner, IEnumerable<string> rawTypeNames, string? schemaName)
    {
        var previousOwner = _currentOwner;
        _currentOwner = owner;

        try
        {
            var keys = new List<string>();
            var nodes = new List<TypeDefinition>();
            var seenKeys = new HashSet<string>(PgProj.Parser.SqlIdentifiers.NameComparer);

            foreach (var rawTypeName in rawTypeNames)
            {
                if (string.IsNullOrWhiteSpace(rawTypeName))
                {
                    continue;
                }

                var normalizedReference = NormalizeTypeReference(rawTypeName);
                if (string.IsNullOrWhiteSpace(normalizedReference))
                {
                    continue;
                }

                var builtInType = ResolveBuiltInType(normalizedReference);
                if (builtInType is not null)
                {
                    if (seenKeys.Add(builtInType))
                    {
                        keys.Add(builtInType);
                    }

                    continue;
                }

                var resolvedType = ResolveType(schemaName, normalizedReference);
                if (resolvedType is not null)
                {
                    if (seenKeys.Add(resolvedType.Key))
                    {
                        keys.Add(resolvedType.Key);
                        nodes.Add(resolvedType);
                    }

                    continue;
                }

                if (seenKeys.Add(normalizedReference))
                {
                    keys.Add(normalizedReference);
                }
            }

            return new ResolvedTypeSet(keys.ToArray(), nodes.ToArray());
        }
        finally
        {
            _currentOwner = previousOwner;
        }
    }

    private TypeDefinition? ResolveType(string? schemaName, string rawTypeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawTypeName);

        var normalizedReference = NormalizeTypeReference(rawTypeName);
        if (string.IsNullOrWhiteSpace(normalizedReference))
        {
            return null;
        }

        var (qualifiedSchemaName, simpleName) = SplitQualifiedName(normalizedReference);
        if (!string.IsNullOrWhiteSpace(qualifiedSchemaName)
            && _model.TryGetType(DatabaseModel.CreateKey(qualifiedSchemaName, simpleName), out var qualifiedMatch)
            && qualifiedMatch is not null)
        {
            return qualifiedMatch;
        }

        if (_model.TryGetType(normalizedReference, out var directMatch) && directMatch is not null)
        {
            return directMatch;
        }

        if (!string.IsNullOrWhiteSpace(schemaName)
            && _model.TryGetType(DatabaseModel.CreateKey(schemaName, normalizedReference), out var scopedMatch)
            && scopedMatch is not null)
        {
            return scopedMatch;
        }

        var fallbackMatch = _model.Types.Values.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, simpleName, StringComparison.Ordinal)
            || string.Equals(candidate.Key, normalizedReference, StringComparison.Ordinal));
        if (fallbackMatch is not null)
        {
            return fallbackMatch;
        }

        // A table or view name is also a type (its row type), so it is not a missing type (G-50); nor is a project range's multirange (G-204).
        if (IsRowType(schemaName, normalizedReference) || MultirangeTypes.FindRange(_model, schemaName, normalizedReference) is not null)
        {
            return null;
        }

        // A column reports the type it names; its table does not report it again (G-326).
        if (_currentOwner is TableDefinition && _columnTypes?.Contains(normalizedReference) == true)
        {
            return null;
        }

        AddDiagnostic(
            _diagnostics,
            $"{_currentOwner?.Kind ?? "Node"} '{_currentOwner?.Key ?? normalizedReference}' references missing type '{normalizedReference}'.",
            nameof(TypeResolutionPass),
            SchemaScopeSeverity.ForReference(_model, string.IsNullOrWhiteSpace(qualifiedSchemaName) ? null : qualifiedSchemaName), location: SemanticPlaces.OfType(_currentOwner, normalizedReference));
        return null;
    }

    private bool IsRowType(string? schemaName, string reference)
    {
        if ((_model.TryGetTable(reference, out var table) && table is not null)
            || (_model.TryGetView(reference, out var view) && view is not null))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(schemaName))
        {
            return false;
        }

        var scopedKey = DatabaseModel.CreateKey(schemaName, reference);
        return (_model.TryGetTable(scopedKey, out table) && table is not null)
            || (_model.TryGetView(scopedKey, out view) && view is not null);
    }

    private string? ResolveBuiltInType(string rawTypeName)
    {
        if (string.IsNullOrWhiteSpace(rawTypeName))
        {
            return null;
        }

        var normalizedReference = NormalizeTypeReference(rawTypeName);
        var (_, simpleName) = SplitQualifiedName(normalizedReference);
        return _symbolTable.IsKnownBuiltInType(simpleName) || SupplementalBuiltInTypeNames.Contains(simpleName)
            ? simpleName
            : null;
    }

    private static string NormalizeTypeReference(string typeReference)
    {
        var normalized = typeReference.Trim();
        while (normalized.EndsWith("[]", StringComparison.Ordinal))
        {
            normalized = normalized[..^2].TrimEnd();
        }

        var typeModifierIndex = normalized.IndexOf('(');
        if (typeModifierIndex >= 0)
        {
            normalized = normalized[..typeModifierIndex].TrimEnd();
        }

        normalized = string.Join(' ', normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        const string pgCatalogPrefix = "pg_catalog.";
        if (normalized.StartsWith(pgCatalogPrefix, StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[pgCatalogPrefix.Length..];
        }

        return normalized;
    }

    private static (string? SchemaName, string Name) SplitQualifiedName(string typeReference)
    {
        var separatorIndex = typeReference.IndexOf('.');
        if (separatorIndex <= 0 || separatorIndex >= typeReference.Length - 1)
        {
            return (null, typeReference);
        }

        return (
            typeReference[..separatorIndex].Trim(),
            typeReference[(separatorIndex + 1)..].Trim());
    }

    private static void AddDiagnostic(List<SemanticDiagnostic> diagnostics, string message, string source, SemanticDiagnosticSeverity severity = SemanticDiagnosticSeverity.Error, SemanticLocation? location = null)
        => diagnostics.Add(severity == SemanticDiagnosticSeverity.Warning
            ? SemanticDiagnostics.Warning(message, location, source)
            : SemanticDiagnostics.Error(message, location, source));

    private sealed record ResolvedTypeSet(IReadOnlyList<string> Keys, IReadOnlyList<TypeDefinition> Nodes);
}
