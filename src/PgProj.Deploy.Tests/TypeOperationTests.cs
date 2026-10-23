using PgProj.Diff;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

[TestClass]
public sealed class TypeOperationTests
{
    [TestMethod]
    public void CreateOperationEmitsRealFieldListForCompositeType()
    {
        var type = CreateType(
            "public.address",
            "address",
            "public",
            compositeFields:
            [
                new CompositeTypeFieldDefinition("street", "text", "text"),
                new CompositeTypeFieldDefinition("city", "text", "text"),
            ]);

        var sql = new CreateOperation
        {
            NodeId = type.Key,
            NodeKind = ChangeNodeKind.Type,
            Node = type,
        }.GenerateSql();

        Assert.AreEqual(
            "CREATE TYPE \"public\".\"address\" AS (\n    \"street\" text,\n    \"city\" text\n);",
            sql);
    }

    [TestMethod]
    public void CreateOperationEmitsRangeSubtypeForRangeType()
    {
        var type = CreateType(
            "public.floatrange",
            "floatrange",
            "public",
            rangeOptions: new RangeTypeOptionsDefinition("float8", null, null, null, null));

        var sql = new CreateOperation
        {
            NodeId = type.Key,
            NodeKind = ChangeNodeKind.Type,
            Node = type,
        }.GenerateSql();

        Assert.AreEqual("CREATE TYPE \"public\".\"floatrange\" AS RANGE (SUBTYPE = float8);", sql);
    }

    [TestMethod]
    public void CreateOperationEmitsFullRangeOptionsWhenPresent()
    {
        var type = CreateType(
            "public.textrange2",
            "textrange2",
            "public",
            rangeOptions: new RangeTypeOptionsDefinition("text", "text_pattern_ops", "\"C\"", "my_canonical", "my_diff"));

        var sql = new CreateOperation
        {
            NodeId = type.Key,
            NodeKind = ChangeNodeKind.Type,
            Node = type,
        }.GenerateSql();

        Assert.AreEqual(
            "CREATE TYPE \"public\".\"textrange2\" AS RANGE (SUBTYPE = text, SUBTYPE_OPCLASS = text_pattern_ops, COLLATION = \"C\", CANONICAL = my_canonical, SUBTYPE_DIFF = my_diff);",
            sql);
    }

    [TestMethod]
    public void CreateOperationEmitsTodoForUnstructuredEnumType()
    {
        var type = CreateType("public.mood", "mood", "public");

        var sql = new CreateOperation
        {
            NodeId = type.Key,
            NodeKind = ChangeNodeKind.Type,
            Node = type,
        }.GenerateSql();

        StringAssert.StartsWith(sql, "-- TODO:");
    }

    [TestMethod]
    public void CreateOperationEmitsRealLabelListForEnumType()
    {
        var type = CreateType("public.mood", "mood", "public", enumLabels: ["sad", "ok", "happy"]);

        var sql = new CreateOperation
        {
            NodeId = type.Key,
            NodeKind = ChangeNodeKind.Type,
            Node = type,
        }.GenerateSql();

        Assert.AreEqual(
            "CREATE TYPE \"public\".\"mood\" AS ENUM ('sad', 'ok', 'happy');",
            sql);
    }

    [TestMethod]
    public void CreateOperationEscapesSingleQuotesInEnumLabels()
    {
        var type = CreateType("public.rating", "rating", "public", enumLabels: ["good", "user's choice"]);

        var sql = new CreateOperation
        {
            NodeId = type.Key,
            NodeKind = ChangeNodeKind.Type,
            Node = type,
        }.GenerateSql();

        Assert.AreEqual(
            "CREATE TYPE \"public\".\"rating\" AS ENUM ('good', 'user''s choice');",
            sql);
    }

    private static TypeDefinition CreateType(
        string key,
        string name,
        string? schemaName,
        IReadOnlyList<CompositeTypeFieldDefinition>? compositeFields = null,
        RangeTypeOptionsDefinition? rangeOptions = null,
        IReadOnlyList<string>? enumLabels = null)
        => new(
            key,
            name,
            schemaName,
            "user-defined",
            [],
            [],
            [],
            [],
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
