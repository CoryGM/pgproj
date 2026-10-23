using PgProj.Parser;

namespace PgProj.Semantics;

/// <summary>
/// Applies the project's <c>ALTER TEXT SEARCH CONFIGURATION ... MAPPING</c> and <c>ALTER TEXT SEARCH DICTIONARY</c> statements (G-149) to the configurations and
/// dictionaries it declares. Run after every file is declared, so an <c>ALTER</c> may be in a different file from the object it changes, and in file order. One
/// that names an object the project does not declare (a built-in configuration, say) is a warning and is ignored: there is nothing in the project to change.
/// </summary>
internal static class TextSearchDeclarations
{
    public static void Apply(IReadOnlyList<SqlSyntaxTree> trees, DatabaseModel model)
    {
        ArgumentNullException.ThrowIfNull(trees);
        ArgumentNullException.ThrowIfNull(model);

        if (model.TextSearchConfigurations is not IDictionary<string, TextSearchConfigurationDefinition> configurations
            || model.TextSearchDictionaries is not IDictionary<string, TextSearchDictionaryDefinition> dictionaries
            || model.Diagnostics is not List<SemanticDiagnostic> diagnostics)
        {
            throw new InvalidOperationException("The text search collections of the model must be mutable during declaration.");
        }

        // A configuration copied from another of the project's, written without a schema, is the one in its own schema.
        foreach (var configuration in configurations.Values.Where(static configuration => configuration.CopyFrom is not null).ToArray())
        {
            configurations[configuration.Key] = configuration with { CopyFrom = Qualify(model, configuration.SchemaName, configuration.CopyFrom, dictionary: false) };
        }

        foreach (var tree in trees)
        {
            var fileLocation = new SemanticLocation(tree.FilePath, null);
            foreach (var node in tree.Nodes)
            {
                // The ALTER statement's place (G-309e).
                var location = SemanticLocation.Of(tree.FilePath, node.Range) ?? fileLocation;
                switch (node)
                {
                    case TextSearchMappingNode mapping:
                        ApplyMapping(mapping, model, configurations, diagnostics, location);
                        break;
                    case TextSearchDictionaryAlterNode alter:
                        ApplyDictionaryAlter(alter, dictionaries, diagnostics, location);
                        break;
                }
            }
        }
    }

    private static void ApplyMapping(
        TextSearchMappingNode node,
        DatabaseModel model,
        IDictionary<string, TextSearchConfigurationDefinition> configurations,
        List<SemanticDiagnostic> diagnostics,
        SemanticLocation location)
    {
        var key = DatabaseModel.CreateKey(node.SchemaName, node.Name ?? string.Empty);
        // Keyed with its kind (G-228); the warning names it as written.
        if (!configurations.TryGetValue("tsconfiguration:" + key, out var configuration))
        {
            diagnostics.Add(SemanticDiagnostics.Warning(
                $"ALTER TEXT SEARCH CONFIGURATION {key}: the project does not declare it, so the mapping is ignored.",
                location,
                nameof(TextSearchDeclarations)));
            return;
        }

        var mappings = configuration.Mappings.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        IReadOnlyList<string> dictionaryNames = [.. node.Dictionaries.Select(name => Qualify(model, configuration.SchemaName, name, dictionary: true))];
        foreach (var token in node.Tokens)
        {
            switch (node.Action)
            {
                case TextSearchMappingAction.Add:
                case TextSearchMappingAction.Alter:
                    mappings[token] = dictionaryNames;
                    break;
                case TextSearchMappingAction.Drop when configuration.IsCopy:
                    // The copy's mapping for this token is not known, so the drop is recorded as a mapping with no dictionaries.
                    mappings[token] = [];
                    break;
                case TextSearchMappingAction.Drop:
                    mappings.Remove(token);
                    break;
            }
        }

        configurations[configuration.Key] = configuration with { Mappings = mappings };
    }

    /// <summary>
    /// A name written without a schema that is one of the project's own objects of that kind in <paramref name="contextSchema"/> becomes <c>schema.name</c>, so the SQL
    /// that is deployed finds it whatever the deploying session's search path is; any other name (a built-in, or one written with its schema) is left as written.
    /// </summary>
    private static string Qualify(DatabaseModel model, string? contextSchema, string? name, bool dictionary)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('.', StringComparison.Ordinal))
        {
            return name ?? string.Empty;
        }

        var key = DatabaseModel.CreateKey(contextSchema, name);
        if (dictionary)
        {
            return model.TextSearchDictionaries.TryGetValue("tsdictionary:" + key, out var found) ? DatabaseModel.CreateKey(found.SchemaName, found.Name) : name;
        }

        return model.TextSearchConfigurations.TryGetValue("tsconfiguration:" + key, out var configuration) ? DatabaseModel.CreateKey(configuration.SchemaName, configuration.Name) : name;
    }

    private static void ApplyDictionaryAlter(
        TextSearchDictionaryAlterNode node,
        IDictionary<string, TextSearchDictionaryDefinition> dictionaries,
        List<SemanticDiagnostic> diagnostics,
        SemanticLocation location)
    {
        var key = DatabaseModel.CreateKey(node.SchemaName, node.Name ?? string.Empty);
        if (!dictionaries.TryGetValue("tsdictionary:" + key, out var dictionary))
        {
            diagnostics.Add(SemanticDiagnostics.Warning(
                $"ALTER TEXT SEARCH DICTIONARY {key}: the project does not declare it, so the change is ignored.",
                location,
                nameof(TextSearchDeclarations)));
            return;
        }

        var options = dictionary.Options.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        foreach (var (name, value) in node.Options)
        {
            if (value is null)
            {
                options.Remove(name);
            }
            else
            {
                options[name] = value;
            }
        }

        dictionaries[dictionary.Key] = dictionary with { Options = options };
    }
}
