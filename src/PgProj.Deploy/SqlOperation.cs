using PgProj.Diff;
using PgProj.Semantics;

namespace PgProj.Deploy;

public abstract record class SqlOperation
{
    public required string NodeId { get; init; }

    public required ChangeNodeKind NodeKind { get; init; }

    /// <summary>
    /// Why the operation is in the script when that is not evident from the change set (G-103): written above its statement as a
    /// comment, such as for a view dropped only because a column it uses changes. Null for most operations.
    /// </summary>
    public string? Reason { get; init; }

    public abstract string GenerateSql();

    /// <summary>
    /// Extensions are modeled as a <see cref="TypeDefinition"/> of kind <c>extension</c> but deploy with
    /// <c>CREATE EXTENSION</c>/<c>DROP EXTENSION</c>, not the type statements (G-46).
    /// </summary>
    internal static bool IsExtension(TypeDefinition type)
        => string.Equals(type.TypeKind, "extension", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A domain is modeled as a <see cref="TypeDefinition"/> of kind <c>domain</c> but deploys with
    /// <c>CREATE DOMAIN</c>/<c>DROP DOMAIN</c>, not the generic type statements - PostgreSQL rejects
    /// <c>DROP TYPE</c> on a domain ("Use DROP DOMAIN to remove a domain") (G-77).
    /// </summary>
    internal static bool IsDomain(TypeDefinition type)
        => string.Equals(type.TypeKind, "domain", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Both sides of a change are one composite type (<c>CREATE TYPE ... AS (...)</c>), whose fields have in-place <c>ALTER TYPE</c>
    /// forms (G-111). A composite, enum, and range type all have the kind <c>user-defined</c>, so the form is read from the
    /// structure: fields on at least one side (the new side has none when every field was dropped), and no enum labels or range
    /// options on either.
    /// </summary>
    internal static bool IsCompositeChange(TypeDefinition oldType, TypeDefinition newType)
        => (oldType.CompositeFields.Count > 0 || newType.CompositeFields.Count > 0)
            && string.Equals(oldType.TypeKind, newType.TypeKind, StringComparison.OrdinalIgnoreCase)
            && !IsDomain(oldType) && !IsDomain(newType) && !IsExtension(oldType) && !IsExtension(newType)
            && oldType.EnumLabels.Count == 0 && newType.EnumLabels.Count == 0
            && oldType.RangeOptions is null && newType.RangeOptions is null;
}
