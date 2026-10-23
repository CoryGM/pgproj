using System.Text.Json;
using PgProj.Parser;

namespace PgProj.Semantics;

/// <summary>
/// Reports what a PL/pgSQL routine's body names that the project does not define (G-303a), as a warning. The SQL the body runs (read by <c>PlpgsqlBodyReader</c>) goes
/// through <see cref="QueryAstReader"/> like a <c>LANGUAGE sql</c> body, but nothing it finds becomes a dependency: PostgreSQL does not check a PL/pgSQL body when the routine is created,
/// two routines may call each other, and a body may name a temporary table it creates, run dynamic SQL, or depend on a <c>search_path</c> set while it runs. So an order
/// is never changed and the report is never an error.
/// <para>
/// The reader is conservative, so that a warning can be believed: a relation the body creates itself (<c>CREATE TEMP TABLE</c>, <c>CREATE TABLE ... AS</c>) is not reported; neither
/// is a relation in a schema of PostgreSQL's own, a sequence, a function that is built in, or a project routine, procedure, or aggregate; a relation or function
/// the project defines is resolved as PostgreSQL resolves it, a qualified name in its schema and an unqualified one along the search path. Dynamic SQL
/// (<c>EXECUTE</c>) is not read, because its text is built at run time.
/// </para>
/// </summary>
internal sealed class PlpgsqlBodyReferencePass
{
    public DatabaseModel Execute(DatabaseModel model, IEnumerable<SqlSyntaxTree> syntaxTrees)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(syntaxTrees);

        var diagnostics = new List<SemanticDiagnostic>();
        // A statement trigger's transition tables (REFERENCING NEW TABLE AS new_table) are relations in the function it runs.
        var transitionTables = new HashSet<string>(PgProj.Parser.SqlIdentifiers.NameComparer);
        foreach (var trigger in model.Triggers.Values)
        {
            foreach (var name in new[] { trigger.OldTransitionTable, trigger.NewTransitionTable })
            {
                if (!string.IsNullOrWhiteSpace(name))
                {
                    transitionTables.Add(name);
                }
            }
        }

        var functions = new Dictionary<string, FunctionDefinition>(model.Functions, PgProj.Parser.SqlIdentifiers.NameComparer);
        var procedures = new Dictionary<string, ProcedureDefinition>(model.Procedures, PgProj.Parser.SqlIdentifiers.NameComparer);
        foreach (var node in syntaxTrees.SelectMany(static tree => tree.Nodes))
        {
            switch (node)
            {
                case FunctionNode { PlpgsqlStatementAsts.Count: > 0 } function:
                {
                    var key = DatabaseModel.CreateRoutineKey(function.SchemaName, function.Name ?? "unknown_function", function.Signature?.RenderArgumentTypeSignature());
                    var references = Report(model, diagnostics, transitionTables, "function", key, function.SchemaName, function.PlpgsqlStatementAsts, functions.GetValueOrDefault(key)?.Location);
                    if (functions.TryGetValue(key, out var definition))
                    {
                        functions[key] = definition with { PlpgsqlReferences = references };
                    }

                    break;
                }

                case ProcedureNode { PlpgsqlStatementAsts.Count: > 0 } procedure:
                {
                    var key = DatabaseModel.CreateRoutineKey(procedure.SchemaName, procedure.Name ?? "unknown_procedure", procedure.Signature?.RenderArgumentTypeSignature());
                    var references = Report(model, diagnostics, transitionTables, "procedure", key, procedure.SchemaName, procedure.PlpgsqlStatementAsts, procedures.GetValueOrDefault(key)?.Location);
                    if (procedures.TryGetValue(key, out var definition))
                    {
                        procedures[key] = definition with { PlpgsqlReferences = references };
                    }

                    break;
                }
            }
        }

        return model with { Functions = functions, Procedures = procedures, Diagnostics = diagnostics.Count == 0 ? model.Diagnostics : [.. model.Diagnostics, .. diagnostics] };
    }

    private static PlpgsqlBodyReferences Report(DatabaseModel model, List<SemanticDiagnostic> diagnostics, HashSet<string> transitionTables, string kind, string key, string? schema, IReadOnlyList<string> asts, SemanticLocation? location)
    {
        var created = new HashSet<string>(PgProj.Parser.SqlIdentifiers.NameComparer);
        foreach (var ast in asts)
        {
            CollectCreatedRelations(ast, created);
        }

        var warned = new HashSet<string>(PgProj.Parser.SqlIdentifiers.NameComparer);
        var relations = new List<BodyRelation>();
        var columns = new List<BodyColumn>();
        var functions = new List<BodyFunction>();
        foreach (var references in asts.Select(QueryAstReader.Collect))
        {
            foreach (var relation in references.Relations)
            {
                if ((relation.SchemaName is null && (created.Contains(relation.Name) || transitionTables.Contains(relation.Name)))
                    || QueryBodyBindingPass.IsBuiltIn(relation)
                    || model.FindSequence(relation.SchemaName ?? schema, relation.Name) is not null)
                {
                    continue;
                }

                relations.Add(new BodyRelation(relation.SchemaName, relation.Name));
                if ((relation.SchemaName is not null && !IsProjectSchema(model, relation.SchemaName))
                    || QueryBodyBindingPass.Resolve(model.Tables.Values, relation, schema, model.SearchPathFor(schema)) is not null
                    || QueryBodyBindingPass.Resolve(model.Views.Values, relation, schema, model.SearchPathFor(schema)) is not null)
                {
                    continue;
                }

                if (warned.Add("relation:" + QueryBodyBindingPass.Written(relation)))
                {
                    diagnostics.Add(SemanticDiagnostics.Warning(
                        $"The PL/pgSQL body of {kind} '{key}' names table or view '{QueryBodyBindingPass.Written(relation)}', which the project does not define.",
                        location,
                        nameof(PlpgsqlBodyReferencePass)));
                }
            }

            foreach (var column in references.Columns)
            {
                var relation = column.Relation;
                if ((relation.SchemaName is null && (created.Contains(relation.Name) || transitionTables.Contains(relation.Name))) || QueryBodyBindingPass.IsBuiltIn(relation))
                {
                    continue;
                }

                columns.Add(new BodyColumn(relation.SchemaName, relation.Name, column.Name));
            }

            foreach (var function in references.Functions)
            {
                if (BuiltInFunctions.IsBuiltIn(function))
                {
                    continue;
                }

                var dot = function.LastIndexOf('.');
                functions.Add(dot < 0 ? new BodyFunction(null, function) : new BodyFunction(function[..dot], function[(dot + 1)..]));
                if (FunctionReferences.Resolve(model, schema, function) is not null
                    || IsProjectProcedure(model, function)
                    || AggregateReferences.Find(model, schema, function).Count > 0)
                {
                    continue;
                }

                if (warned.Add("function:" + function))
                {
                    diagnostics.Add(SemanticDiagnostics.Warning(
                        $"The PL/pgSQL body of {kind} '{key}' calls function '{function}', which the project does not define.",
                        location,
                        nameof(PlpgsqlBodyReferencePass)));
                }
            }
        }

        return new PlpgsqlBodyReferences([.. relations.Distinct()], [.. columns.Distinct()], [.. functions.Distinct()]);
    }

    /// <summary>A schema the project has something in: a name qualified with any other is another system's (an extension's, another project's), which the project could not define.</summary>
    private static bool IsProjectSchema(DatabaseModel model, string schema)
        => model.Schemas.Contains(schema, PgProj.Parser.SqlIdentifiers.NameComparer)
            || model.SchemaDefinitions.ContainsKey(schema)
            || model.Tables.Values.Any(table => string.Equals(table.SchemaName, schema, StringComparison.Ordinal))
            || model.Views.Values.Any(view => string.Equals(view.SchemaName, schema, StringComparison.Ordinal))
            || model.Functions.Values.Any(function => string.Equals(function.SchemaName, schema, StringComparison.Ordinal))
            || model.Sequences.Values.Any(sequence => string.Equals(sequence.SchemaName, schema, StringComparison.Ordinal));

    private static bool IsProjectProcedure(DatabaseModel model, string reference)
    {
        var dot = reference.LastIndexOf('.');
        var name = dot < 0 ? reference : reference[(dot + 1)..];
        var schema = dot < 0 ? null : reference[..dot];
        return model.Procedures.Values.Any(procedure => string.Equals(procedure.Name, name, StringComparison.Ordinal)
            && (schema is null || string.Equals(procedure.SchemaName, schema, StringComparison.Ordinal)));
    }

    /// <summary>The names of relations a statement creates (<c>CREATE [TEMP] TABLE</c>, <c>CREATE TABLE ... AS</c>, a view): the body makes them, so they are not missing.</summary>
    private static void CollectCreatedRelations(string astJson, HashSet<string> created)
    {
        try
        {
            using var document = PgQueryJson.Parse(astJson);
            Walk(document.RootElement, created);
        }
        catch (JsonException)
        {
            // Not a tree: nothing is read from it.
        }
    }

    private static void Walk(JsonElement node, HashSet<string> created)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in node.EnumerateObject())
                {
                    if (property.Name is "CreateStmt" or "ViewStmt" or "CreateTableAsStmt" or "IntoClause")
                    {
                        AddRelation(property.Value, created);
                    }

                    Walk(property.Value, created);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in node.EnumerateArray())
                {
                    Walk(item, created);
                }

                break;
        }
    }

    /// <summary><c>CreateStmt.relation</c>, <c>ViewStmt.view</c>, <c>IntoClause.rel</c>, and <c>CreateTableAsStmt.into.rel</c>.</summary>
    private static void AddRelation(JsonElement statement, HashSet<string> created)
    {
        if (statement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var path in new[] { "relation", "view", "rel", "into" })
        {
            if (!statement.TryGetProperty(path, out var target) || target.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var range = target.TryGetProperty("rel", out var inner) ? inner : target;
            if (range.ValueKind == JsonValueKind.Object && range.TryGetProperty("relname", out var name) && name.GetString() is { } relname)
            {
                created.Add(relname);
            }
        }
    }
}
