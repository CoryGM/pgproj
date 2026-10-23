namespace PgProj.Semantics;

/// <summary>
/// Marks the procedures whose name another procedure shares with an <c>OUT</c> parameter (G-232). From PostgreSQL 14 a procedure's <c>OUT</c> parameters are part of its
/// signature, so <c>ptest10(IN a int, IN b int, IN c int)</c> and <c>ptest10(OUT a int, IN b int, IN c int)</c> both exist, and a statement that names either by its types alone
/// (<c>ALTER PROCEDURE ptest10(integer, integer, integer)</c>) is refused as not unique. Such a procedure is named with every parameter's mode written out
/// (<see cref="ProcedureDefinition.ArgumentIdentitySql"/>); every other procedure is named as before.
/// </summary>
internal sealed class ProcedureIdentityPass
{
    public DatabaseModel Execute(DatabaseModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var groups = model.Procedures.Values
            .GroupBy(static procedure => $"{procedure.SchemaName}.{procedure.Name}", PgProj.Parser.SqlIdentifiers.NameComparer)
            .Where(static group => group.Count() > 1 && group.Any(static procedure => procedure.Signature?.HasOutParameter == true))
            .SelectMany(static group => group)
            .Select(static procedure => procedure.Key)
            .ToHashSet(PgProj.Parser.SqlIdentifiers.NameComparer);
        if (groups.Count == 0)
        {
            return model;
        }

        model.Procedures = model.Procedures.ToDictionary(
            static pair => pair.Key,
            pair => groups.Contains(pair.Key) ? pair.Value with { UsesFullIdentity = true } : pair.Value,
            PgProj.Parser.SqlIdentifiers.NameComparer);

        // An owner statement holds the procedure it was declared with, from before this pass; it names the procedure the same way.
        model.Owners = model.Owners.ToDictionary(
            static pair => pair.Key,
            pair => pair.Value.Target is ProcedureDefinition target && groups.Contains(target.Key) ? pair.Value with { Target = target with { UsesFullIdentity = true } } : pair.Value,
            PgProj.Parser.SqlIdentifiers.NameComparer);
        return model;
    }
}
