using PgProj.Parser;

namespace PgProj.Semantics;

/// <summary>
/// Turns the owners a project states (<see cref="OwnerNode"/>) into <see cref="OwnerDefinition"/>s (G-146). Run after every file is declared, so an
/// <c>ALTER ... OWNER TO</c> may sit in a different file from the object it names. An owner for an object the project does not declare is a warning and
/// is ignored: there is nothing to set it on, and a typo should be seen.
/// </summary>
internal static class OwnerDeclarations
{
    /// <summary>What an owner statement names, once found in the model.</summary>
    private sealed record Target(string Kind, string Key, string? SchemaName, string Name, ISemanticNode? Node);

    public static void Declare(IReadOnlyList<SqlSyntaxTree> trees, DatabaseModel model)
    {
        ArgumentNullException.ThrowIfNull(trees);
        ArgumentNullException.ThrowIfNull(model);

        if (model.Owners is not IDictionary<string, OwnerDefinition> owners || model.Diagnostics is not List<SemanticDiagnostic> diagnostics)
        {
            throw new InvalidOperationException($"{nameof(DatabaseModel)}.{nameof(DatabaseModel.Owners)} must be backed by a mutable dictionary during declaration.");
        }

        foreach (var tree in trees)
        {
            foreach (var node in tree.Owners)
            {
                if (string.IsNullOrWhiteSpace(node.OwnerRole))
                {
                    continue;
                }

                // The OWNER TO statement's place (G-309e); a file-only place for a model with no ranges.
                var location = SemanticLocation.Of(tree.FilePath, node.Range) ?? new SemanticLocation(tree.FilePath, null);
                if (Resolve(model, node) is not { } target)
                {
                    diagnostics.Add(SemanticDiagnostics.Warning(
                        $"{Describe(node)} OWNER TO {node.OwnerRole}: the project does not declare it, so the owner is ignored.",
                        location,
                        nameof(OwnerDeclarations)));
                    continue;
                }

                var key = OwnerKeys.Create(target.Kind, target.Key);
                if (owners.TryGetValue(key, out var existing) && !string.Equals(existing.OwnerRole, node.OwnerRole, StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Add(SemanticDiagnostics.Warning(
                        $"The owner of {Describe(node)} is stated twice ({existing.OwnerRole} and {node.OwnerRole}); the last one is used.",
                        location,
                        nameof(OwnerDeclarations)));
                }

                owners[key] = new OwnerDefinition(key, $"{target.Kind} {target.Key}")
                {
                    SchemaName = target.SchemaName,
                    ObjectKind = target.Kind,
                    ObjectKey = target.Key,
                    ObjectName = target.Name,
                    OwnerRole = node.OwnerRole,
                    Target = target.Node,
                    DeclarationNode = node,
                };
            }
        }
    }

    private static Target? Resolve(DatabaseModel model, OwnerNode node)
    {
        var name = node.ObjectName;
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        switch (node.ObjectKind)
        {
            case "Schema":
                // A schema is not a node of the model: it exists when the project says so, and its key is its name.
                return new Target("schema", name.Trim(), null, name.Trim(), null);

            case "Table":
                return model.Tables.TryGetValue(DatabaseModel.CreateKey(node.SchemaName, name), out var table)
                    ? new Target("table", table.Key, table.SchemaName, table.Name, table)
                    : null;

            case "View" or "MaterializedView":
                return model.Views.TryGetValue(DatabaseModel.CreateKey(node.SchemaName, name), out var view)
                    ? new Target(view.IsMaterialized ? "materialized view" : "view", view.Key, view.SchemaName, view.Name, view)
                    : null;

            case "Sequence":
                return model.Sequences.TryGetValue(DatabaseModel.CreateKey(node.SchemaName, name), out var sequence)
                    ? new Target("sequence", sequence.Key, sequence.SchemaName, sequence.Name, sequence)
                    : null;

            case "Function":
                return FindRoutine(model.Functions, node, static function => function.Signature?.RenderArgumentTypeSignature()) is { } function
                    ? new Target("function", function.Key, function.SchemaName, function.Name, function)
                    : null;

            case "Procedure":
                return FindRoutine(model.Procedures, node, static procedure => procedure.Signature?.RenderArgumentTypeSignature()) is { } procedure
                    ? new Target("procedure", procedure.Key, procedure.SchemaName, procedure.Name, procedure)
                    : null;

            case "Aggregate":
                return FindRoutine(model.Aggregates, node, static aggregate => AggregateOptions.ArgumentSignature(aggregate.Arguments)) is { } aggregate
                    ? new Target("aggregate", aggregate.Key, aggregate.SchemaName, aggregate.Name, aggregate)
                    : null;

            case "Operator":
                return model.Operators.TryGetValue(OperatorNames.CreateKey(node.SchemaName, name, node.LeftType, node.RightType), out var @operator)
                    ? new Target("operator", @operator.Key, @operator.SchemaName, @operator.Name, @operator)
                    : null;

            case "Type" or "Domain":
                // An extension is modeled as a type but has no owner of its own to set.
                return model.Types.TryGetValue(DatabaseModel.CreateKey(node.SchemaName, name), out var type)
                    && !string.Equals(type.TypeKind, "extension", StringComparison.OrdinalIgnoreCase)
                        ? new Target(string.Equals(type.TypeKind, "domain", StringComparison.OrdinalIgnoreCase) ? "domain" : "type", type.Key, type.SchemaName, type.Name, type)
                        : null;

            case "EventTrigger":
                return model.EventTriggers.TryGetValue(DatabaseModel.CreateEventTriggerKey(name), out var eventTrigger)
                    ? new Target("event trigger", eventTrigger.Key, null, eventTrigger.Name, eventTrigger)
                    : null;

            case "Publication":
                return model.Publications.TryGetValue(PublicationOptions.CreateKey(name), out var publication)
                    ? new Target("publication", publication.Key, null, publication.Name, publication)
                    : null;

            case "Collation":
                return model.Collations.TryGetValue(DatabaseModel.CreateCollationKey(node.SchemaName, name), out var collation)
                    ? new Target("collation", collation.Key, collation.SchemaName, collation.Name, collation)
                    : null;

            case "Statistics":
                return model.Statistics.TryGetValue(DatabaseModel.CreateStatisticsKey(node.SchemaName, name), out var statistics)
                    ? new Target("statistics", statistics.Key, statistics.SchemaName, statistics.Name, statistics)
                    : null;

            case "TextSearchDictionary":
                return model.TextSearchDictionaries.TryGetValue(DatabaseModel.CreateTextSearchDictionaryKey(node.SchemaName, name), out var dictionary)
                    ? new Target("text search dictionary", dictionary.Key, dictionary.SchemaName, dictionary.Name, dictionary)
                    : null;

            case "TextSearchConfiguration":
                return model.TextSearchConfigurations.TryGetValue(DatabaseModel.CreateTextSearchConfigurationKey(node.SchemaName, name), out var configuration)
                    ? new Target("text search configuration", configuration.Key, configuration.SchemaName, configuration.Name, configuration)
                    : null;

            default:
                return null;
        }
    }

    /// <summary>
    /// The routine an owner statement names: the overload with that argument list (compared as types are everywhere, <c>int</c> and
    /// <c>pg_catalog.int4</c> being one), or, when the statement wrote no argument list, the only routine of that name.
    /// </summary>
    private static TDefinition? FindRoutine<TDefinition>(IReadOnlyDictionary<string, TDefinition> definitions, OwnerNode node, Func<TDefinition, string?> signatureOf)
        where TDefinition : class, ISemanticNode
    {
        bool Named(TDefinition candidate)
            => string.Equals(candidate.Name, node.ObjectName, StringComparison.Ordinal)
                && (node.SchemaName is null || string.Equals(candidate.SchemaName, node.SchemaName, StringComparison.Ordinal));

        if (node.ArgumentTypes is not { } types)
        {
            var named = definitions.Values.Where(Named).Take(2).ToArray();
            return named.Length == 1 ? named[0] : null;
        }

        var signature = string.Join(", ", types);
        if (definitions.TryGetValue(DatabaseModel.CreateRoutineKey(node.SchemaName, node.ObjectName!, signature), out var exact))
        {
            return exact;
        }

        var canonical = DatabaseModel.CanonicalArgumentTypeSignature(signature);
        return definitions.Values.FirstOrDefault(candidate =>
            Named(candidate)
            && string.Equals(DatabaseModel.CanonicalArgumentTypeSignature(signatureOf(candidate) ?? string.Empty), canonical, StringComparison.OrdinalIgnoreCase));
    }

    private static string Describe(OwnerNode node)
    {
        var kind = node.ObjectKind switch
        {
            "MaterializedView" => "MATERIALIZED VIEW",
            "EventTrigger" => "EVENT TRIGGER",
            "TextSearchDictionary" => "TEXT SEARCH DICTIONARY",
            "TextSearchConfiguration" => "TEXT SEARCH CONFIGURATION",
            var other => other.ToUpperInvariant(),
        };
        return $"ALTER {kind} {(string.IsNullOrWhiteSpace(node.SchemaName) ? string.Empty : node.SchemaName + ".")}{node.ObjectName}";
    }
}
