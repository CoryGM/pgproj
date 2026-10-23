namespace PgProj.Parser.Tests;

/// <summary>G-180: a view's <c>WITH (...)</c> options and check option are read, in the canonical form of <see cref="ViewOptions"/>.</summary>
[TestClass]
public sealed class ViewOptionsParsingTests
{
    [TestMethod]
    [DataRow("WITH (security_barrier = true)", "security_barrier=true")]
    [DataRow("WITH (security_barrier)", "security_barrier=true")]
    [DataRow("WITH (security_invoker = on)", "security_invoker=true")]
    [DataRow("WITH (security_invoker = 1)", "security_invoker=true")]
    [DataRow("WITH (SECURITY_INVOKER = 'yes')", "security_invoker=true")]
    [DataRow("WITH (security_barrier = false)", "")]
    [DataRow("WITH (security_barrier = off, security_invoker = on)", "security_invoker=true")]
    [DataRow("WITH (security_invoker, security_barrier)", "security_barrier=true;security_invoker=true")]
    [DataRow("", "")]
    public void TheWithClauseIsReadInCanonicalForm(string withClause, string expected)
    {
        var tree = new PgQueryParser().Parse($"CREATE VIEW public.v {withClause} AS SELECT id FROM public.t;");

        Assert.IsFalse(tree.HasErrors, string.Join(Environment.NewLine, tree.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.AreEqual(expected, string.Join(';', tree.Views.Single().Options));
        Assert.AreEqual("SELECT id FROM public.t", tree.Views.Single().RawBodyExpression);
    }

    [TestMethod]
    [DataRow("WITH LOCAL CHECK OPTION", "check_option=local")]
    [DataRow("WITH CASCADED CHECK OPTION", "check_option=cascaded")]
    [DataRow("WITH CHECK OPTION", "check_option=cascaded")]
    [DataRow("", "")]
    public void TheCheckOptionIsAnOption(string checkClause, string expected)
    {
        var tree = new PgQueryParser().Parse($"CREATE VIEW public.v AS SELECT id FROM public.t WHERE id > 0 {checkClause};");

        Assert.IsFalse(tree.HasErrors, string.Join(Environment.NewLine, tree.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.AreEqual(expected, string.Join(';', tree.Views.Single().Options));
    }

    [TestMethod]
    public void AMaterializedViewHasNoViewOptions()
    {
        var tree = new PgQueryParser().Parse("CREATE MATERIALIZED VIEW public.m AS SELECT id FROM public.t;");

        Assert.IsEmpty(tree.Views.Single().Options);
    }
}
