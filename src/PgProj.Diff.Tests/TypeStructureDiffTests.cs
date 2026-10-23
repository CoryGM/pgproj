using PgProj.Semantics;

namespace PgProj.Diff.Tests;

[TestClass]
public sealed class TypeStructureDiffTests
{
    [TestMethod]
    public void CompositeFieldChangesProduceTypeModificationDetails()
    {
        var oldModel = CreateModel(CreateType(
            key: "public.account_point",
            name: "account_point",
            schemaName: "public",
            compositeFields:
            [
                new CompositeTypeFieldDefinition("x", "integer", "integer"),
                new CompositeTypeFieldDefinition("y", "integer", "integer")
            ]));

        var newModel = CreateModel(CreateType(
            key: "public.account_point",
            name: "account_point",
            schemaName: "public",
            compositeFields:
            [
                new CompositeTypeFieldDefinition("x", "integer", "integer"),
                new CompositeTypeFieldDefinition("y", "numeric", "numeric")
            ],
            referencedTypes: ["integer", "numeric"]));

        var result = new DiffEngine().Execute(oldModel, newModel);

        Assert.HasCount(1, result.ModifiedTypes);
        var change = result.ModifiedTypes[0];
        Assert.AreEqual("public.account_point", change.NodeId);
        Assert.IsTrue(change.Details.Any(detail =>
            detail.PropertyName == nameof(TypeDefinition.CompositeFields)
            && string.Equals(detail.OldValue, "x:integer, y:integer", StringComparison.Ordinal)
            && string.Equals(detail.NewValue, "x:integer, y:numeric", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void RangeOptionChangesProduceTypeModificationDetails()
    {
        var oldModel = CreateModel(CreateType(
            key: "public.account_period",
            name: "account_period",
            schemaName: "public",
            rangeOptions: new RangeTypeOptionsDefinition("integer", null, null, "public.normalize_period", null),
            referencedTypes: ["integer"],
            referencedFunctions: ["public.normalize_period"]));

        var newModel = CreateModel(CreateType(
            key: "public.account_period",
            name: "account_period",
            schemaName: "public",
            rangeOptions: new RangeTypeOptionsDefinition("numeric", null, null, "public.normalize_period_v2", null),
            referencedTypes: ["numeric"],
            referencedFunctions: ["public.normalize_period_v2"]));

        var result = new DiffEngine().Execute(oldModel, newModel);

        Assert.HasCount(1, result.ModifiedTypes);
        var change = result.ModifiedTypes[0];
        Assert.AreEqual("public.account_period", change.NodeId);
        // Names are shown as compared: as PostgreSQL reads them, without a public or pg_catalog schema (G-115).
        Assert.IsTrue(change.Details.Any(detail =>
            detail.PropertyName == nameof(TypeDefinition.RangeOptions)
            && string.Equals(detail.OldValue, "CanonicalFunction:normalize_period, CollationName:, MultirangeTypeName:account_period_multirange, Subtype:integer, SubtypeDiffFunction:, SubtypeOperatorClass:", StringComparison.Ordinal)
            && string.Equals(detail.NewValue, "CanonicalFunction:normalize_period_v2, CollationName:, MultirangeTypeName:account_period_multirange, Subtype:numeric, SubtypeDiffFunction:, SubtypeOperatorClass:", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void EnumLabelChangesProduceTypeModificationDetails()
    {
        var oldModel = CreateModel(CreateType(
            key: "public.mood",
            name: "mood",
            schemaName: "public",
            enumLabels: ["sad", "ok"]));

        var newModel = CreateModel(CreateType(
            key: "public.mood",
            name: "mood",
            schemaName: "public",
            enumLabels: ["sad", "ok", "happy"]));

        var result = new DiffEngine().Execute(oldModel, newModel);

        Assert.HasCount(1, result.ModifiedTypes);
        var change = result.ModifiedTypes[0];
        Assert.AreEqual("public.mood", change.NodeId);
        Assert.IsTrue(change.Details.Any(detail =>
            detail.PropertyName == nameof(TypeDefinition.EnumLabels)
            && string.Equals(detail.OldValue, "sad, ok", StringComparison.Ordinal)
            && string.Equals(detail.NewValue, "sad, ok, happy", StringComparison.Ordinal)));
    }

    private static DatabaseModel CreateModel(TypeDefinition type)
        => DatabaseModel.Empty with
        {
            Types = new Dictionary<string, TypeDefinition>(StringComparer.OrdinalIgnoreCase)
            {
                [type.Key] = type,
            },
        };

    private static TypeDefinition CreateType(
        string key,
        string name,
        string? schemaName,
        IReadOnlyList<CompositeTypeFieldDefinition>? compositeFields = null,
        RangeTypeOptionsDefinition? rangeOptions = null,
        IReadOnlyList<string>? referencedTypes = null,
        IReadOnlyList<string>? referencedFunctions = null,
        IReadOnlyList<string>? enumLabels = null)
        => new(
            key,
            name,
            schemaName,
            "user-defined",
            [],
            [],
            referencedTypes ?? [],
            referencedFunctions ?? [],
            [],
            [],
            [],
            [])
        {
            CompositeFields = compositeFields ?? [],
            RangeOptions = rangeOptions,
            EnumLabels = enumLabels ?? [],
        };
}
