using PgProj.Parser;

namespace PgProj.Semantics.Tests;

[TestClass]
[DoNotParallelize]
public sealed class TypeStructureSemanticsTests
{
    [TestMethod]
    public void CompositeTypeFieldsAreCapturedStructurally()
    {
        const string sql = "create type public.account_point as (x integer, y text[]);";

        var model = BuildModel(sql);

        Assert.IsTrue(model.Types.ContainsKey("public.account_point"));
        var type = model.Types["public.account_point"];
        Assert.HasCount(2, type.CompositeFields);
        Assert.AreEqual("x", type.CompositeFields[0].Name);
        Assert.AreEqual("integer", type.CompositeFields[0].DataType);
        Assert.AreEqual("integer", type.CompositeFields[0].RawType);
        Assert.AreEqual("y", type.CompositeFields[1].Name);
        Assert.AreEqual("text[]", type.CompositeFields[1].DataType);
        Assert.AreEqual("text[]", type.CompositeFields[1].RawType);
        Assert.IsNull(type.RangeOptions);
        Assert.IsFalse(model.HasErrors, string.Join(Environment.NewLine, model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        CollectionAssert.Contains(type.ReferencedTypes.ToList(), "integer");
        Assert.IsTrue(type.ReferencedTypes.Any(reference => string.Equals(reference, "text", StringComparison.OrdinalIgnoreCase) || string.Equals(reference, "text[]", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void RangeTypeOptionsAreCapturedStructurally()
    {
        const string sql = "create type public.account_period as range (subtype = timestamp without time zone, canonical = public.normalize_period, subtype_diff = public.period_diff);";

        var model = BuildModel(
            sql,
            new FunctionNode("normalize_period", "public", "sql", "select $1"),
            new FunctionNode("period_diff", "public", "sql", "select 0::double precision"));

        Assert.IsTrue(model.Types.ContainsKey("public.account_period"));
        var type = model.Types["public.account_period"];
        Assert.HasCount(0, type.CompositeFields);
        Assert.IsNotNull(type.RangeOptions);
        Assert.AreEqual("timestamp without time zone", type.RangeOptions.Subtype);
        Assert.AreEqual("public.normalize_period", type.RangeOptions.CanonicalFunction);
        Assert.AreEqual("public.period_diff", type.RangeOptions.SubtypeDiffFunction);
        Assert.IsFalse(model.HasErrors, string.Join(Environment.NewLine, model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        CollectionAssert.Contains(type.ReferencedTypes.ToList(), "timestamp without time zone");
        CollectionAssert.Contains(type.ReferencedFunctions.ToList(), "public.normalize_period");
        CollectionAssert.Contains(type.ReferencedFunctions.ToList(), "public.period_diff");
    }

    [TestMethod]
    public void EnumTypeLabelsAreCapturedStructurally()
    {
        const string sql = "create type public.mood as enum ('sad', 'ok', 'happy');";

        var model = BuildModel(sql);

        Assert.IsTrue(model.Types.ContainsKey("public.mood"));
        var type = model.Types["public.mood"];
        Assert.HasCount(3, type.EnumLabels);
        Assert.AreEqual("sad", type.EnumLabels[0]);
        Assert.AreEqual("ok", type.EnumLabels[1]);
        Assert.AreEqual("happy", type.EnumLabels[2]);
        Assert.HasCount(0, type.CompositeFields);
        Assert.IsNull(type.RangeOptions);
        Assert.IsFalse(model.HasErrors, string.Join(Environment.NewLine, model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    [TestMethod]
    public void EnumTypeLabelWithEscapedQuoteRoundTrips()
    {
        const string sql = "create type public.rating as enum ('good', 'user''s choice');";

        var model = BuildModel(sql);

        var type = model.Types["public.rating"];
        Assert.HasCount(2, type.EnumLabels);
        Assert.AreEqual("good", type.EnumLabels[0]);
        Assert.AreEqual("user's choice", type.EnumLabels[1]);
    }

    [TestMethod]
    public void DomainStructureIsCapturedStructurally()
    {
        const string sql = "create domain public.positive_amount as numeric(10,2) default 0 constraint positive check (value > 0) not null;";

        var model = BuildModel(sql);

        Assert.IsTrue(model.Types.ContainsKey("public.positive_amount"));
        var type = model.Types["public.positive_amount"];
        Assert.AreEqual("domain", type.TypeKind);
        Assert.AreEqual("pg_catalog.numeric", type.DomainBaseType);
        CollectionAssert.AreEqual(new[] { "10", "2" }, type.DomainBaseTypeModifiers.ToArray());
        Assert.AreEqual(0, type.DomainBaseTypeArrayDimensions);
        Assert.IsTrue(type.DomainIsNotNull);
        Assert.AreEqual("0", type.DomainRawDefaultExpression);
        var check = type.DomainCheckConstraints.Single();
        Assert.AreEqual("positive", check.Name);
        Assert.AreEqual("value > 0", check.RawExpression);
        Assert.IsNotNull(check.NormalizedExpression);
        Assert.IsFalse(model.HasErrors, string.Join(Environment.NewLine, model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        CollectionAssert.Contains(type.ReferencedTypes.ToList(), "numeric");
    }

    /// <summary>An unnamed CHECK constraint gets PostgreSQL's own default name (confirmed against a real server, G-77).</summary>
    [TestMethod]
    public void UnnamedDomainCheckConstraintsGetDefaultNames()
    {
        const string sql = "create domain public.d as integer check (value > 0) check (value < 100);";

        var model = BuildModel(sql);

        var type = model.Types["public.d"];
        Assert.HasCount(2, type.DomainCheckConstraints);
        Assert.AreEqual("d_check", type.DomainCheckConstraints[0].Name);
        Assert.AreEqual("d_check1", type.DomainCheckConstraints[1].Name);
    }

    [TestMethod]
    public void DomainWithNoConstraintsHasEmptyStructure()
    {
        const string sql = "create domain public.plain_text as text;";

        var model = BuildModel(sql);

        var type = model.Types["public.plain_text"];
        Assert.AreEqual("text", type.DomainBaseType);
        Assert.IsFalse(type.DomainIsNotNull);
        Assert.IsNull(type.DomainRawDefaultExpression);
        Assert.HasCount(0, type.DomainCheckConstraints);
    }

    /// <summary>
    /// Parses <paramref name="sql"/> through the real parser (G-78: CREATE TYPE is read from pg_query's AST, not
    /// regexed out of a hand-built tree's <c>Sql</c> text) and appends any extra hand-built nodes a test still
    /// needs (a range type's canonical/subtype_diff functions, which the test SQL references but doesn't declare).
    /// </summary>
    private static DatabaseModel BuildModel(string sql, params FunctionNode[] functions)
    {
        var tree = new PgQueryParser().Parse(sql);
        if (functions.Length > 0)
        {
            tree = tree with { Nodes = [.. tree.Nodes, .. functions] };
        }

        return new SemanticPipeline().Execute([tree]);
    }
}