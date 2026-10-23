using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-76: row-level security policies are modeled as <see cref="PolicyDefinition"/>, scoped to their table like a trigger
/// (G-36) or constraint (G-38), and canonicalized per <c>copilot-semantic.md</c>: <c>Roles</c> is a set (sorted via
/// <c>NormalizedRoles</c>) since PostgreSQL's own <c>polroles</c> carries no order, and <c>USING</c>/<c>WITH CHECK</c> are kept
/// as written beside their comparison form, the same reasoning as a trigger's <c>WHEN</c> condition (G-40).
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class PolicySemanticsTests
{
    private const string Prefix = """
        CREATE TABLE public.items (id integer, owner_id integer, status text, region varchar(20), "Mixed" integer);
        """;

    [TestMethod]
    public void APolicyIsKeyedBySchemaTableAndPolicyName()
    {
        var policy = Policy("CREATE POLICY p ON public.items USING (true);");

        Assert.AreEqual("public.items.p", policy.Key);
        Assert.AreEqual("p", policy.Name);
        Assert.AreEqual("public", policy.SchemaName);
        Assert.AreEqual("items", policy.TableName);
    }

    [TestMethod]
    public void TheSamePolicyNameOnTwoDifferentTablesIsTwoDistinctPolicies()
    {
        var parser = new PgQueryParser();
        var model = new SemanticPipeline().Execute(
        [
            parser.Parse(Prefix),
            parser.Parse("CREATE TABLE public.other (id integer);"),
            parser.Parse("CREATE POLICY p ON public.items USING (true);"),
            parser.Parse("CREATE POLICY p ON public.other USING (true);"),
        ]);

        Assert.HasCount(2, model.Policies);
        Assert.IsTrue(model.Policies.ContainsKey("public.items.p"));
        Assert.IsTrue(model.Policies.ContainsKey("public.other.p"));
    }

    [TestMethod]
    public void RolesAreASortedSetWhateverOrderTheyWereWritten()
    {
        var one = Policy("CREATE POLICY p ON public.items TO other_role, app_user USING (true);");
        var other = Policy("CREATE POLICY p ON public.items TO app_user, Other_Role USING (true);");

        CollectionAssert.AreEqual(new[] { "app_user", "other_role" }, one.NormalizedRoles.ToArray());
        CollectionAssert.AreEqual(one.NormalizedRoles.ToArray(), other.NormalizedRoles.ToArray());
        // Roles itself (deploy output) stays in the order written.
        CollectionAssert.AreEqual(new[] { "other_role", "app_user" }, one.Roles.ToArray());
    }

    [TestMethod]
    public void TheUsingExpressionIsKeptAsWrittenBesideItsComparisonForm()
    {
        var policy = Policy("CREATE POLICY p ON public.items USING ( owner_id  =  1 );");

        Assert.AreEqual("owner_id  =  1", policy.RawUsingExpression);
        Assert.IsFalse(string.IsNullOrWhiteSpace(policy.NormalizedUsingExpression));
        Assert.AreNotEqual(policy.RawUsingExpression, policy.NormalizedUsingExpression);
    }

    [TestMethod]
    public void APolicyWithoutUsingOrWithCheckHasNeither()
    {
        var policy = Policy("CREATE POLICY p ON public.items;");

        Assert.IsNull(policy.RawUsingExpression);
        Assert.IsNull(policy.NormalizedUsingExpression);
        Assert.IsNull(policy.RawWithCheckExpression);
        Assert.IsNull(policy.NormalizedWithCheckExpression);
    }

    [TestMethod]
    public void TheComparisonFormIgnoresWhitespaceCaseAndParentheses()
    {
        var written = Policy("CREATE POLICY p ON public.items USING (owner_id=1 AND status='active');");
        var rendered = Policy("CREATE POLICY p ON public.items USING (((owner_id = 1) AND (status = 'active')));");

        Assert.AreEqual(written.NormalizedUsingExpression, rendered.NormalizedUsingExpression);
    }

    [TestMethod]
    public void ACastToTextIsFoldedOnlyForAVarcharColumnOfThePolicysOwnTable()
    {
        var varchar = Policy("CREATE POLICY p ON public.items USING (region = 'x');");
        var varcharRendered = Policy("CREATE POLICY p ON public.items USING ((region)::text = 'x'::text);");
        var text = Policy("CREATE POLICY p ON public.items USING (status = 'x');");
        var textCast = Policy("CREATE POLICY p ON public.items USING (status::text = 'x');");

        Assert.AreEqual(varchar.NormalizedUsingExpression, varcharRendered.NormalizedUsingExpression, "PostgreSQL adds ::text to a varchar column itself.");
        Assert.AreNotEqual(text.NormalizedUsingExpression, textCast.NormalizedUsingExpression, "A cast the author wrote on a non-varchar column is kept.");
    }

    [TestMethod]
    public void WithCheckIsIndependentOfUsing()
    {
        var policy = Policy("CREATE POLICY p ON public.items FOR INSERT WITH CHECK (status = 'active');");

        Assert.IsNull(policy.RawUsingExpression);
        Assert.AreEqual("status = 'active'", policy.RawWithCheckExpression);
        Assert.IsFalse(string.IsNullOrWhiteSpace(policy.NormalizedWithCheckExpression));
    }

    private static DatabaseModel BuildModel(string policySql)
    {
        var parser = new PgQueryParser();
        return new SemanticPipeline().Execute([parser.Parse(Prefix), parser.Parse(policySql)]);
    }

    private static PolicyDefinition Policy(string sql)
        => BuildModel(sql).Policies.Values.Single();
}
