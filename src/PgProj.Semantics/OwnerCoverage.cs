namespace PgProj.Semantics;

/// <summary>
/// Which of a project's objects state an owner (G-146). A project states an owner for the objects it chooses to, and an object whose owner it does not
/// state is not compared (the database has an owner for every object, so comparing one the project says nothing about would propose a change to every
/// object of every project that has no <c>OWNER TO</c>). So that this is seen and not silent, <c>build</c>, <c>diff</c>, and <c>publish</c> say how many
/// objects have none, once a project states any.
/// </summary>
public static class OwnerCoverage
{
    /// <summary>The <see cref="SemanticDiagnostic.Source"/> of the notice.</summary>
    public const string DiagnosticSource = "OwnerCoverage";

    /// <summary>
    /// The notice for <paramref name="model"/>, or null: when it states no owner at all (then it does not manage owners and has nothing to say), or
    /// when every object it declares states one.
    /// </summary>
    public static SemanticDiagnostic? Describe(DatabaseModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (model.Owners.Count == 0)
        {
            return null;
        }

        var count = CountWithoutOwner(model);
        return count == 0
            ? null
            : SemanticDiagnostics.Info(
                count == 1
                    ? "1 object states no owner (ALTER ... OWNER TO); its owner is not compared."
                    : $"{count} objects state no owner (ALTER ... OWNER TO); their owners are not compared.",
                source: DiagnosticSource);
    }

    /// <summary>The number of objects <paramref name="model"/> declares that state no owner: schemas (those that hold an object), and every kind of object that has an owner to set.</summary>
    public static int CountWithoutOwner(DatabaseModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        bool Stated(string kind, string key) => model.Owners.ContainsKey(OwnerKeys.Create(kind, key));

        var count = 0;
        var schemas = new HashSet<string>(PgProj.Parser.SqlIdentifiers.NameComparer);
        void Schema(string? schemaName)
        {
            if (!string.IsNullOrWhiteSpace(schemaName))
            {
                schemas.Add(schemaName.Trim());
            }
        }

        foreach (var table in model.Tables.Values)
        {
            Schema(table.SchemaName);
            count += Stated("table", table.Key) ? 0 : 1;
        }

        foreach (var view in model.Views.Values)
        {
            Schema(view.SchemaName);
            count += Stated(view.IsMaterialized ? "materialized view" : "view", view.Key) ? 0 : 1;
        }

        foreach (var sequence in model.Sequences.Values)
        {
            Schema(sequence.SchemaName);

            // A sequence a column owns follows its table and has no owner of its own to state.
            if (!sequence.IsIdentityOwned && string.IsNullOrWhiteSpace(sequence.OwnedByTable))
            {
                count += Stated("sequence", sequence.Key) ? 0 : 1;
            }
        }

        foreach (var function in model.Functions.Values)
        {
            Schema(function.SchemaName);
            count += Stated("function", function.Key) ? 0 : 1;
        }

        foreach (var procedure in model.Procedures.Values)
        {
            Schema(procedure.SchemaName);
            count += Stated("procedure", procedure.Key) ? 0 : 1;
        }

        foreach (var aggregate in model.Aggregates.Values)
        {
            Schema(aggregate.SchemaName);
            count += Stated("aggregate", aggregate.Key) ? 0 : 1;
        }

        foreach (var @operator in model.Operators.Values)
        {
            Schema(@operator.SchemaName);
            count += Stated("operator", @operator.Key) ? 0 : 1;
        }

        foreach (var type in model.Types.Values.Where(static type => !string.Equals(type.TypeKind, "extension", StringComparison.OrdinalIgnoreCase)))
        {
            Schema(type.SchemaName);
            count += Stated(string.Equals(type.TypeKind, "domain", StringComparison.OrdinalIgnoreCase) ? "domain" : "type", type.Key) ? 0 : 1;
        }

        foreach (var collation in model.Collations.Values)
        {
            Schema(collation.SchemaName);
            count += Stated("collation", collation.Key) ? 0 : 1;
        }

        foreach (var statistics in model.Statistics.Values)
        {
            Schema(statistics.SchemaName);
            count += Stated("statistics", statistics.Key) ? 0 : 1;
        }

        foreach (var dictionary in model.TextSearchDictionaries.Values)
        {
            Schema(dictionary.SchemaName);
            count += Stated("text search dictionary", dictionary.Key) ? 0 : 1;
        }

        foreach (var configuration in model.TextSearchConfigurations.Values)
        {
            Schema(configuration.SchemaName);
            count += Stated("text search configuration", configuration.Key) ? 0 : 1;
        }

        count += model.EventTriggers.Values.Count(eventTrigger => !Stated("event trigger", eventTrigger.Key));
        count += model.Publications.Values.Count(publication => !Stated("publication", publication.Key));
        count += schemas.Count(schema => !Stated("schema", schema));
        return count;
    }
}
