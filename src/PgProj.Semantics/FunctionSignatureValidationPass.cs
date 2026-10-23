namespace PgProj.Semantics;

internal sealed class FunctionSignatureValidationPass
{
    // Any language is accepted (G-211): PostgreSQL rejects one it does not have when the routine is created. pgproj reads the body of a routine in
    // one of these for what it uses; internal and c name compiled code, which uses no object of the project's.
    private static readonly HashSet<string> LanguagesReadForDependencies = new(StringComparer.OrdinalIgnoreCase)
    {
        "sql",
        "plpgsql",
        "internal",
        "c",
    };

    private static readonly HashSet<string> AllowedVolatility = new(StringComparer.OrdinalIgnoreCase)
    {
        "immutable",
        "stable",
        "volatile",
    };

    private static readonly HashSet<string> AllowedParallelSafety = new(StringComparer.OrdinalIgnoreCase)
    {
        "safe",
        "restricted",
        "unsafe",
    };

    private SymbolTable _symbolTable = new(DatabaseModel.Empty);
    private IList<SemanticDiagnostic>? _diagnostics;

    public void Execute(DatabaseModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (model.Diagnostics is not IList<SemanticDiagnostic> diagnostics)
        {
            return;
        }

        _symbolTable = new SymbolTable(model);
        _diagnostics = diagnostics;

        foreach (var function in model.Functions.Values)
        {
            ValidateParameterTypes(function);
            ValidateReturnType(function);
            ValidateAttributes(function);
            WarnOfUnreadBody("Function", function.Key, function.Language, function.Location);
        }

        foreach (var procedure in model.Procedures.Values)
        {
            WarnOfUnreadBody("Procedure", procedure.Key, procedure.Language, procedure.Location);
        }
    }

    /// <summary>
    /// A routine in a language pgproj does not read (PL/Perl, PL/Python, PL/Tcl, PL/v8, ...) is deployed and compared as written, but what its body uses is
    /// not known, so those objects are not ordered before it (G-211). Said once per routine.
    /// </summary>
    private void WarnOfUnreadBody(string kind, string key, string? language, SemanticLocation? location)
    {
        if (string.IsNullOrWhiteSpace(language) || LanguagesReadForDependencies.Contains(language.Trim()))
        {
            return;
        }

        _diagnostics?.Add(SemanticDiagnostics.Warning(
            $"{kind} '{key}' is written in LANGUAGE {language.Trim()}, whose body pgproj does not read: it is deployed and compared as written, but the objects it uses are not ordered before it.",
            location,
            nameof(FunctionSignatureValidationPass)));
    }

    private void ValidateParameterTypes(FunctionDefinition def)
    {
        ArgumentNullException.ThrowIfNull(def);

        foreach (var parameterType in GetParameterTypeReferences(def))
        {
            ValidateResolvedType(def, parameterType, "parameter");
        }

        // TODO: Validate named parameter binding once FunctionDefinition surfaces explicit parameter metadata.
        // TODO: Validate variadic parameters once FunctionDefinition captures VARIADIC declarations.
        // TODO: Validate default parameter values once the parser exposes argument defaults.
    }

    private void ValidateReturnType(FunctionDefinition def)
    {
        ArgumentNullException.ThrowIfNull(def);

        var returnType = GetReturnTypeReference(def);
        if (!string.IsNullOrWhiteSpace(returnType))
        {
            ValidateResolvedType(def, returnType, "return");
        }

        // TODO: Validate composite return types once FunctionDefinition models RETURNS TABLE and row-type metadata.
    }

    private void ValidateAttributes(FunctionDefinition def)
    {
        ArgumentNullException.ThrowIfNull(def);

        if (string.IsNullOrWhiteSpace(def.Language))
        {
            AddError(def, "does not declare a LANGUAGE attribute.");
        }

        var volatility = GetVolatility(def);
        if (!string.IsNullOrWhiteSpace(volatility) && !AllowedVolatility.Contains(volatility))
        {
            AddError(def, $"declares unsupported VOLATILITY '{volatility}'.");
        }

        var parallelSafety = GetParallelSafety(def);
        if (!string.IsNullOrWhiteSpace(parallelSafety) && !AllowedParallelSafety.Contains(parallelSafety))
        {
            AddError(def, $"declares unsupported PARALLEL safety '{parallelSafety}'.");
        }
    }

    private void ValidateResolvedType(FunctionDefinition def, string typeReference, string signaturePart)
    {
        var normalizedReference = NormalizeTypeReference(typeReference);
        if (string.IsNullOrWhiteSpace(normalizedReference))
        {
            AddError(def, $"contains a blank {signaturePart} type reference.");
            return;
        }

        if (_symbolTable.IsKnownBuiltInType(normalizedReference))
        {
            return;
        }

        if (def.ResolvedTypes.Any(type =>
                string.Equals(type.Key, normalizedReference, StringComparison.Ordinal)
                || string.Equals(type.Name, normalizedReference, StringComparison.Ordinal)
                || string.Equals(DatabaseModel.CreateKey(type.SchemaName, type.Name), normalizedReference, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        if (_symbolTable.TryGetType(def, normalizedReference, out _))
        {
            return;
        }

        AddError(def, $"references unknown {signaturePart} type '{normalizedReference}'.");
    }

    private void AddError(FunctionDefinition def, string message)
    {
        _diagnostics?.Add(SemanticDiagnostics.Error(
            $"Function '{def.Key}' {message}",
            def.Location,
            nameof(FunctionSignatureValidationPass)));
    }

    private static IEnumerable<string> GetParameterTypeReferences(FunctionDefinition def)
    {
        ArgumentNullException.ThrowIfNull(def);

        // TODO: Extract parameter type references from explicit FunctionDefinition signature metadata when available.
        return [];
    }

    private static string? GetReturnTypeReference(FunctionDefinition def)
    {
        ArgumentNullException.ThrowIfNull(def);

        // TODO: Extract the RETURNS type from explicit FunctionDefinition signature metadata when available.
        return null;
    }

    private static string? GetVolatility(FunctionDefinition def)
    {
        ArgumentNullException.ThrowIfNull(def);

        // TODO: Surface IMMUTABLE/STABLE/VOLATILE on FunctionDefinition and validate it here.
        return null;
    }

    private static string? GetParallelSafety(FunctionDefinition def)
    {
        ArgumentNullException.ThrowIfNull(def);

        // TODO: Surface PARALLEL SAFE/RESTRICTED/UNSAFE on FunctionDefinition and validate it here.
        return null;
    }

    private static string NormalizeTypeReference(string typeReference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeReference);

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
}
