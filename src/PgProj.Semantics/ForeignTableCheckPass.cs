namespace PgProj.Semantics;

/// <summary>
/// What PostgreSQL refuses on a foreign table (G-133, design item 3), reported by <c>build</c> rather than when <c>publish</c> runs: an index, and a constraint other than
/// <c>CHECK</c> or <c>NOT NULL</c> (a primary key, unique key, foreign key, or exclusion constraint), since a foreign table keeps no rows to index (checked on
/// PostgreSQL 14 and 18). A new pass, after the others; it changes nothing they do (<c>copilot-semantic.md</c> §2).
/// </summary>
internal sealed class ForeignTableCheckPass
{
    public void Execute(DatabaseModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (model.Diagnostics is not IList<SemanticDiagnostic> diagnostics || !model.Tables.Values.Any(static table => table.IsForeign))
        {
            return;
        }

        var foreign = model.Tables.Values.Where(static table => table.IsForeign).Select(static table => table.Key).ToHashSet(PgProj.Parser.SqlIdentifiers.NameComparer);
        foreach (var constraint in model.Constraints.Values.Where(constraint => constraint.TableName is not null && foreign.Contains(DatabaseModel.CreateKey(constraint.SchemaName, constraint.TableName))))
        {
            var type = (constraint.ConstraintType ?? string.Empty).Trim().ToUpperInvariant();
            if (type is not ("CHECK" or "NOT NULL"))
            {
                diagnostics.Add(SemanticDiagnostics.Error(
                    $"The foreign table {DatabaseModel.CreateKey(constraint.SchemaName, constraint.TableName!)} cannot have the {type.ToLowerInvariant()} constraint {constraint.Name}: PostgreSQL allows only CHECK and NOT NULL on a foreign table, which keeps no rows of its own.",
                    constraint.Location,
                    nameof(ForeignTableCheckPass)));
            }
        }

        foreach (var index in model.Indexes.Values.Where(index => index.TableName is not null && foreign.Contains(DatabaseModel.CreateKey(index.SchemaName, index.TableName))))
        {
            diagnostics.Add(SemanticDiagnostics.Error(
                $"The foreign table {DatabaseModel.CreateKey(index.SchemaName, index.TableName!)} cannot have the index {index.Name}: PostgreSQL does not index a foreign table, which keeps no rows of its own.",
                index.Location,
                nameof(ForeignTableCheckPass)));
        }
    }
}
