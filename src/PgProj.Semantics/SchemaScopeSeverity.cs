using PgProj.Parser;

namespace PgProj.Semantics;

/// <summary>
/// The severity to report an otherwise-unresolvable table/type/function/column reference with (G-83): pgproj
/// cannot always tell "this is missing" apart from "this belongs to a schema another project owns." Downgrading
/// the existing "references missing X" error to a warning is limited to exactly that ambiguous case, the same
/// shape as <see cref="BuiltInFunctions.MissingFunctionSeverity"/> (there, keyed on whether the project declares
/// any extension; here, keyed on whether the reference names a schema the project's own <c>schemas</c> list
/// doesn't include).
/// </summary>
/// <remarks>
/// A project's <c>pgproj.json</c> <c>schemas</c> list is optional. When it is empty or unset (<see
/// cref="DatabaseModel.Schemas"/> has no entries), pgproj has no explicit boundary to check a reference against at
/// all, so <em>every</em> otherwise-unresolvable reference is a warning, regardless of whether it is schema-qualified
/// or not — this is the opposite polarity from <see cref="BuiltInFunctions.MissingFunctionSeverity"/>, where no
/// extension declared stays strict. When <c>schemas</c> is populated, a reference qualified to a schema outside
/// it is a warning (it most likely belongs to another project); an unqualified reference, or one qualified to a
/// schema the project does list, that still doesn't resolve stays an error — populating <c>schemas</c> is what
/// makes checking strict again, only for the schemas actually listed.
/// </remarks>
public static class SchemaScopeSeverity
{
    /// <summary>
    /// The severity for an unresolved reference whose explicit schema qualifier (if any) is <paramref
    /// name="referenceSchemaName"/>. Pass <see langword="null"/> for a reference with no explicit schema (a bare
    /// name, or a <c>table.column</c> form where only the table is schema-qualified elsewhere).
    /// </summary>
    public static SemanticDiagnosticSeverity ForReference(DatabaseModel model, string? referenceSchemaName)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (model.Schemas.Count == 0)
        {
            return SemanticDiagnosticSeverity.Warning;
        }

        if (referenceSchemaName is not null && !ContainsSchema(model.Schemas, referenceSchemaName))
        {
            return SemanticDiagnosticSeverity.Warning;
        }

        return SemanticDiagnosticSeverity.Error;
    }

    /// <summary>
    /// The more lenient of two severities computed for the same diagnostic by independent policies (for example a
    /// function call already downgraded by <see cref="BuiltInFunctions.MissingFunctionSeverity"/> because the
    /// project declares an extension, and this schema-scope policy independently downgrading it too because the
    /// call is schema-qualified out of scope). Either policy choosing to downgrade is enough.
    /// </summary>
    public static SemanticDiagnosticSeverity Lenient(SemanticDiagnosticSeverity first, SemanticDiagnosticSeverity second)
        => first < second ? first : second;

    /// <summary>
    /// The explicit schema qualifier of a table/type/function reference, written as <c>name</c> or
    /// <c>schema.name</c>; <see langword="null"/> when the reference is bare.
    /// </summary>
    public static string? SchemaOfObjectReference(string? reference)
        => SchemaOfQualifiedReference(reference, partsWhenQualified: 2);

    /// <summary>
    /// The explicit schema qualifier of a column reference, written as <c>column</c>, <c>table.column</c> (no
    /// explicit schema - the owning table's own schema applies), or <c>schema.table.column</c>; <see
    /// langword="null"/> for the first two forms.
    /// </summary>
    public static string? SchemaOfColumnReference(string? reference)
        => SchemaOfQualifiedReference(reference, partsWhenQualified: 3);

    private static string? SchemaOfQualifiedReference(string? reference, int partsWhenQualified)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        var parts = SqlIdentifiers.SplitQualifiedName(reference);
        return parts.Count >= partsWhenQualified ? parts[0] : null;
    }

    private static bool ContainsSchema(IReadOnlyList<string> schemas, string schemaName)
    {
        foreach (var schema in schemas)
        {
            if (string.Equals(schema.Trim(), schemaName.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
