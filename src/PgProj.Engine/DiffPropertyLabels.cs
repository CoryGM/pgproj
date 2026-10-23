using System.Text;

namespace PgProj.Engine;

/// <summary>
/// The name <c>diff</c> shows for a changed property (G-285): what a user writes or sees in PostgreSQL (<c>Default</c>, <c>Query</c>, <c>Columns changed</c>)
/// instead of the model's property name (<c>DefaultExpressionNode</c>, <c>SelectExpressionNode</c>, <c>ModifiedColumns</c>). Only the output changes: the change set
/// keeps the model's names, which the deploy layer reads. A name not in the table is written in words (<c>ForeignKeyDeleteAction</c> is
/// <c>Foreign key delete action</c>, <c>IsUnlogged</c> is <c>Unlogged</c>).
/// </summary>
internal static class DiffPropertyLabels
{
    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        ["DefaultExpressionNode"] = "Default",
        ["DomainRawDefaultExpression"] = "Default",
        ["SelectExpressionNode"] = "Query",
        ["BodyExpressionNode"] = "Body",
        ["ServerRenderedBody"] = "Body",
        ["CheckExpressionNode"] = "Check",
        ["PredicateExpressionNode"] = "Where",
        ["RawExclusionPredicateExpression"] = "Where",
        ["ServerRenderedDefinition"] = "Definition",
        ["NormalizedDefinition"] = "Definition",
        ["GeneratedExpression"] = "Generated expression",
        ["DataType"] = "Base type",
        ["RawType"] = "Type",
        ["NormalizedTypeModifiers"] = "Type modifiers",
        ["IsArray"] = "Array",
        ["DomainBaseType"] = "Base type",
        ["DomainBaseTypeModifiers"] = "Base type modifiers",
        ["DomainBaseTypeArrayDimensions"] = "Base type array dimensions",
        ["DomainIsNotNull"] = "Not null",
        ["DomainCollationName"] = "Collation",
        ["DomainIntervalFields"] = "Interval fields",
        ["DomainCheckConstraints"] = "Checks",
        ["DomainCheckConstraintsValidated"] = "Checks validated",
        ["CollationName"] = "Collation",
        ["IsNullable"] = "Nullable",
        ["IdentityGeneration"] = "Identity",
        ["ModifiedColumns"] = "Columns changed",
        ["ModifiedConstraints"] = "Constraints changed",
        ["ModifiedIndexes"] = "Indexes changed",
        ["ReferencedTables"] = "Tables used",
        ["ReferencedColumns"] = "Columns used",
        ["ReferencedTypes"] = "Types used",
        ["ReferencedFunctions"] = "Functions used",
        ["OwnerRole"] = "Owner",
        ["OfTypeKey"] = "Of type",
        ["PartitionBoundRaw"] = "Partition bound",
        ["PartitionOfSchemaName"] = "Partition of (schema)",
        ["PartitionOfTableName"] = "Partition of",
        ["PartitionKeyElements"] = "Partition key",
        ["KeyElements"] = "Key",
        ["KeyColumns"] = "Key columns",
        ["TablespaceName"] = "Tablespace",
        ["StorageOptions"] = "Storage parameters",
        ["IndexStorageOptions"] = "Index storage parameters",
        ["FunctionName"] = "Function",
        ["FunctionArguments"] = "Function arguments",
        ["OutputColumnNames"] = "Output columns",
        ["OutputColumnTypes"] = "Output column types",
        ["Signature.Result"] = "Result type",
        ["Signature.Configuration"] = "Settings",
        ["Signature.Attributes"] = "Attributes",
        ["ConstraintKeys"] = "Constraint keys",
        ["ConstraintTableName"] = "Table",
        ["ForeignKeyReferencedTable"] = "References",
        ["ForeignKeyReferencedSchemaName"] = "References (schema)",
        ["ForeignKeyReferencedColumns"] = "Referenced columns",
        ["ForeignKeyUpdateAction"] = "On update",
        ["ForeignKeyDeleteAction"] = "On delete",
        ["ForeignKeyDeleteSetColumns"] = "On delete set columns",
        ["ForeignKeyMatchType"] = "Match",
        ["IsInitiallyDeferred"] = "Initially deferred",
        ["IsRowLevel"] = "For each row",
        ["EnumLabels"] = "Labels",
        ["ExtensionVersion"] = "Version",
        ["LcCollate"] = "LC_COLLATE",
        ["LcCtype"] = "LC_CTYPE",
        ["CacheSize"] = "Cache",
        ["OwnedByTable"] = "Owned by table",
        ["OwnedByColumn"] = "Owned by column",
    };

    /// <summary>The label for <paramref name="propertyName"/>, a <see cref="PgProj.Diff.ModificationDetail.PropertyName"/>.</summary>
    public static string For(string propertyName)
    {
        ArgumentNullException.ThrowIfNull(propertyName);

        if (Labels.TryGetValue(propertyName, out var label))
        {
            return label;
        }

        // A name with a part after a colon names that part as written: an option (`Option:fillfactor`), a token type's mapping (`Mapping:asciiword`), or a
        // publication's table (`Table:public.t:Columns`).
        var colon = propertyName.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0)
        {
            var parts = propertyName.Split(':');
            return string.Join(' ', parts.Select(static (part, index) => index == 0 ? Words(part) : index == 1 ? part : Words(part).ToLowerInvariant()));
        }

        return Words(propertyName);
    }

    /// <summary><c>ForeignKeyDeleteAction</c> as <c>Foreign key delete action</c>; a leading <c>Is</c> is left out (<c>IsUnlogged</c> is <c>Unlogged</c>).</summary>
    private static string Words(string name)
    {
        var start = name.Length > 2 && name.StartsWith("Is", StringComparison.Ordinal) && char.IsUpper(name[2]) ? 2 : 0;
        var text = new StringBuilder(name.Length + 8);
        for (var i = start; i < name.Length; i++)
        {
            var character = name[i];
            if (i > start && char.IsUpper(character) && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
            {
                text.Append(' ');
            }

            text.Append(text.Length == 0 ? character : char.ToLowerInvariant(character));
        }

        return text.ToString();
    }
}
