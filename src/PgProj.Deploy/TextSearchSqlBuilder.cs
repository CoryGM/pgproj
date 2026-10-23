using PgProj.Semantics;

namespace PgProj.Deploy;

/// <summary>The SQL for text search dictionaries and configurations (G-149).</summary>
internal static class TextSearchSqlBuilder
{
    public static string FormatName(string? schemaName, string name)
        => string.IsNullOrWhiteSpace(schemaName) ? Quote(name) : $"{Quote(schemaName)}.{Quote(name)}";

    public static string CreateDictionary(TextSearchDictionaryDefinition dictionary)
    {
        var options = new List<string> { $"TEMPLATE = {QuoteDotted(dictionary.Template ?? string.Empty)}" };
        options.AddRange(dictionary.Options.OrderBy(static pair => pair.Key, StringComparer.Ordinal).Select(static pair => $"{pair.Key} = {Literal(pair.Value)}"));
        return CreateOperation.AppendComment(
            $"CREATE TEXT SEARCH DICTIONARY {FormatName(dictionary.SchemaName, dictionary.Name)} (\n    {string.Join(",\n    ", options)}\n);",
            CommentSqlHelper.GenerateObjectComment("TEXT SEARCH DICTIONARY", dictionary.SchemaName, dictionary.Name, dictionary.Comment));
    }

    public static string DropDictionary(TextSearchDictionaryDefinition dictionary)
        => $"DROP TEXT SEARCH DICTIONARY {FormatName(dictionary.SchemaName, dictionary.Name)};";

    /// <summary>
    /// <c>ALTER TEXT SEARCH DICTIONARY</c> for the options that changed: a new or changed option with its value, a removed one with none (PostgreSQL removes an option
    /// written without a value).
    /// </summary>
    public static string AlterDictionary(TextSearchDictionaryDefinition oldDictionary, TextSearchDictionaryDefinition newDictionary)
    {
        var changes = new List<string>();
        foreach (var (name, value) in newDictionary.Options.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (!oldDictionary.Options.TryGetValue(name, out var oldValue) || !string.Equals(oldValue, value, StringComparison.Ordinal))
            {
                changes.Add($"{name} = {Literal(value)}");
            }
        }

        changes.AddRange(oldDictionary.Options.Keys.Where(name => !newDictionary.Options.ContainsKey(name)).Order(StringComparer.Ordinal));
        var statements = new List<string>();
        if (changes.Count > 0)
        {
            statements.Add($"ALTER TEXT SEARCH DICTIONARY {FormatName(newDictionary.SchemaName, newDictionary.Name)} ({string.Join(", ", changes)});");
        }

        if (!string.Equals(oldDictionary.Comment, newDictionary.Comment, StringComparison.Ordinal))
        {
            statements.Add(CommentSqlHelper.GenerateObjectComment("TEXT SEARCH DICTIONARY", newDictionary.SchemaName, newDictionary.Name, newDictionary.Comment)
                ?? CommentSqlHelper.GenerateObjectCommentClear("TEXT SEARCH DICTIONARY", newDictionary.SchemaName, newDictionary.Name));
        }

        return statements.Count == 0
            ? $"-- Nothing to change in text search dictionary {FormatName(newDictionary.SchemaName, newDictionary.Name)}."
            : string.Join("\n", statements);
    }

    /// <summary>
    /// <c>CREATE TEXT SEARCH CONFIGURATION</c> with its parser or as a copy, then its mappings: <c>ADD MAPPING</c> into the empty configuration a parser makes, and
    /// <c>ALTER MAPPING</c> (or <c>DROP MAPPING</c> for a token the project leaves unmapped) over the mappings a copy starts with. Tokens with the same dictionaries
    /// share a statement.
    /// </summary>
    public static string CreateConfiguration(TextSearchConfigurationDefinition configuration)
    {
        var name = FormatName(configuration.SchemaName, configuration.Name);
        var statements = new List<string>
        {
            configuration.IsCopy
                ? $"CREATE TEXT SEARCH CONFIGURATION {name} (COPY = {QuoteDotted(configuration.CopyFrom!)});"
                : $"CREATE TEXT SEARCH CONFIGURATION {name} (PARSER = {QuoteDotted(configuration.Parser ?? "pg_catalog.default")});",
        };

        var verb = configuration.IsCopy ? "ALTER" : "ADD";
        foreach (var group in configuration.Mappings.Where(static pair => pair.Value.Count > 0).GroupBy(static pair => string.Join(",", pair.Value), StringComparer.Ordinal).OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            statements.Add(MappingStatement(name, verb, group.Select(static pair => pair.Key), group.First().Value));
        }

        var dropped = configuration.IsCopy ? configuration.Mappings.Where(static pair => pair.Value.Count == 0).Select(static pair => pair.Key).Order(StringComparer.Ordinal).ToArray() : [];
        if (dropped.Length > 0)
        {
            statements.Add($"ALTER TEXT SEARCH CONFIGURATION {name} DROP MAPPING IF EXISTS FOR {string.Join(", ", dropped.Select(Quote))};");
        }

        if (configuration.Comment is not null)
        {
            statements.Add(CommentSqlHelper.GenerateObjectComment("TEXT SEARCH CONFIGURATION", configuration.SchemaName, configuration.Name, configuration.Comment)!);
        }

        return string.Join("\n", statements);
    }

    public static string DropConfiguration(TextSearchConfigurationDefinition configuration)
        => $"DROP TEXT SEARCH CONFIGURATION {FormatName(configuration.SchemaName, configuration.Name)};";

    /// <summary>
    /// The mapping statements that bring <paramref name="oldConfiguration"/> (as the database has it, mapping every token it uses) to <paramref name="newConfiguration"/>.
    /// For a copy only the tokens the project states are touched; otherwise a token the project does not map is dropped.
    /// </summary>
    public static string AlterConfiguration(TextSearchConfigurationDefinition oldConfiguration, TextSearchConfigurationDefinition newConfiguration)
    {
        var name = FormatName(newConfiguration.SchemaName, newConfiguration.Name);
        var added = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var altered = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var dropped = new List<string>();

        foreach (var (token, dictionaries) in newConfiguration.Mappings.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            var normalized = newConfiguration.NormalizedMappings.TryGetValue(token, out var canonical) ? canonical : dictionaries;
            var hasOld = oldConfiguration.NormalizedMappings.TryGetValue(token, out var oldDictionaries) && oldDictionaries.Count > 0;
            if (dictionaries.Count == 0)
            {
                if (hasOld)
                {
                    dropped.Add(token);
                }
            }
            else if (!hasOld)
            {
                Group(added, token, dictionaries);
            }
            else if (!normalized.SequenceEqual(oldDictionaries!, PgProj.Parser.SqlIdentifiers.NameComparer))
            {
                Group(altered, token, dictionaries);
            }
        }

        if (!newConfiguration.IsCopy)
        {
            dropped.AddRange(oldConfiguration.NormalizedMappings.Where(pair => pair.Value.Count > 0 && !newConfiguration.Mappings.ContainsKey(pair.Key)).Select(static pair => pair.Key).Order(StringComparer.Ordinal));
        }

        var statements = new List<string>();
        statements.AddRange(added.OrderBy(static pair => pair.Key, StringComparer.Ordinal).Select(pair => MappingStatement(name, "ADD", TokensOf(pair.Value), DictionariesOf(pair.Key))));
        statements.AddRange(altered.OrderBy(static pair => pair.Key, StringComparer.Ordinal).Select(pair => MappingStatement(name, "ALTER", TokensOf(pair.Value), DictionariesOf(pair.Key))));
        if (dropped.Count > 0)
        {
            statements.Add($"ALTER TEXT SEARCH CONFIGURATION {name} DROP MAPPING FOR {string.Join(", ", dropped.Select(Quote))};");
        }

        if (!string.Equals(oldConfiguration.Comment, newConfiguration.Comment, StringComparison.Ordinal))
        {
            statements.Add(CommentSqlHelper.GenerateObjectComment("TEXT SEARCH CONFIGURATION", newConfiguration.SchemaName, newConfiguration.Name, newConfiguration.Comment)
                ?? CommentSqlHelper.GenerateObjectCommentClear("TEXT SEARCH CONFIGURATION", newConfiguration.SchemaName, newConfiguration.Name));
        }

        return statements.Count == 0
            ? $"-- Nothing to change in text search configuration {FormatName(newConfiguration.SchemaName, newConfiguration.Name)}."
            : string.Join("\n", statements);

        static void Group(Dictionary<string, List<string>> groups, string token, IReadOnlyList<string> dictionaries)
        {
            var key = string.Join("\u0001", dictionaries);
            if (!groups.TryGetValue(key, out var tokens))
            {
                groups[key] = tokens = [];
            }

            tokens.Add(token);
        }

        static IEnumerable<string> TokensOf(List<string> tokens) => tokens;
        static IReadOnlyList<string> DictionariesOf(string key) => key.Split('\u0001');
    }

    private static string MappingStatement(string configurationName, string verb, IEnumerable<string> tokens, IReadOnlyList<string> dictionaries)
        => $"ALTER TEXT SEARCH CONFIGURATION {configurationName} {verb} MAPPING FOR {string.Join(", ", tokens.Order(StringComparer.Ordinal).Select(Quote))} WITH {string.Join(", ", dictionaries.Select(QuoteDotted))};";

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static string QuoteDotted(string name) => string.Join('.', name.Split('.').Select(Quote));

    private static string Literal(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
}
