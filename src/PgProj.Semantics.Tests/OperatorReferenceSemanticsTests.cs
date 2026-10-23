using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-138: an expression that uses one of the project's operators (<c>a === b</c>, <c>OPERATOR(s.===)</c>) records it, so whatever holds the expression (a view, a
/// column default, a CHECK, an index, a domain CHECK, a SQL routine body) is created after the operator and dropped before it. An operator the project does not
/// define, every built-in one, is not a reference.
/// </summary>
[TestClass]
public sealed class OperatorReferenceSemanticsTests
{
    private const string Head = "CREATE FUNCTION public.same(a int, b int) RETURNS boolean LANGUAGE sql IMMUTABLE AS $$ SELECT a = b $$; CREATE OPERATOR public.=== (LEFTARG = int, RIGHTARG = int, FUNCTION = public.same); ";

    private static readonly string Key = OperatorNames.CreateKey("public", "===", "integer", "integer");

    [TestMethod]
    public void AViewThatUsesAnOperatorRecordsIt()
    {
        var model = Build(Head + "CREATE TABLE public.t (n int); CREATE VIEW public.v AS SELECT n === 1 AS one FROM public.t;");

        CollectionAssert.AreEqual(new[] { Key }, model.Views["public.v"].ReferencedOperators.ToArray());
    }

    [TestMethod]
    public void ASchemaQualifiedOperatorIsFoundToo()
    {
        var model = Build(Head + "CREATE TABLE public.t (n int); CREATE VIEW public.v AS SELECT n OPERATOR(public.===) 1 AS one FROM public.t;");

        CollectionAssert.AreEqual(new[] { Key }, model.Views["public.v"].ReferencedOperators.ToArray());
    }

    [TestMethod]
    public void ABuiltInOperatorIsNotAReference()
    {
        var model = Build(Head + "CREATE TABLE public.t (n int); CREATE VIEW public.v AS SELECT n = 1 AS one, n + 1 AS two FROM public.t;");

        Assert.IsEmpty(model.Views["public.v"].ReferencedOperators);
    }

    [TestMethod]
    public void AnOperatorInAnotherSchemaOffTheSearchPathIsNotFoundByItsBareName()
    {
        var model = Build("CREATE SCHEMA app; CREATE FUNCTION app.same(a int, b int) RETURNS boolean LANGUAGE sql AS $$ SELECT a = b $$; CREATE OPERATOR app.=== (LEFTARG = int, RIGHTARG = int, FUNCTION = app.same); "
            + "CREATE TABLE public.t (n int); CREATE VIEW public.v AS SELECT n === 1 AS one FROM public.t;");

        Assert.IsEmpty(model.Views["public.v"].ReferencedOperators);
    }

    [TestMethod]
    public void EveryOperatorOfThatNameIsARequirementBecauseTheOperandsAreNotKnown()
    {
        var model = Build(Head + "CREATE OPERATOR public.=== (LEFTARG = text, RIGHTARG = text, FUNCTION = public.same); CREATE TABLE public.t (n int); CREATE VIEW public.v AS SELECT n === 1 AS one FROM public.t;");

        Assert.AreEqual(2, model.Views["public.v"].ReferencedOperators.Count);
    }

    [TestMethod]
    public void AColumnDefaultACheckAnIndexAndADomainCheckRecordIt()
    {
        var model = Build(Head
            + "CREATE TABLE public.t (id int PRIMARY KEY, code int DEFAULT (CASE WHEN 1 === 1 THEN 1 ELSE 0 END), n int, CONSTRAINT t_ck CHECK (n === n)); "
            + "CREATE INDEX t_idx ON public.t (n) WHERE n === 2; "
            + "CREATE DOMAIN public.pos AS int CHECK (VALUE === VALUE);");

        CollectionAssert.AreEqual(new[] { Key }, model.Tables["public.t"].Columns.Single(static c => c.Name == "code").ReferencedOperators.ToArray());
        CollectionAssert.AreEqual(new[] { Key }, model.Constraints.Values.Single(static c => c.Name == "t_ck").ReferencedOperators.ToArray());
        CollectionAssert.AreEqual(new[] { Key }, model.Indexes.Values.Single(static i => i.Name == "t_idx").ReferencedOperators.ToArray());
        CollectionAssert.AreEqual(new[] { Key }, model.Types["public.pos"].ReferencedOperators.ToArray());
    }

    [TestMethod]
    public void ASqlFunctionBodyRecordsIt()
    {
        var model = Build(Head + "CREATE FUNCTION public.uses(x int) RETURNS boolean LANGUAGE sql BEGIN ATOMIC SELECT x === 1; END;");

        CollectionAssert.AreEqual(new[] { Key }, model.Functions.Values.Single(static f => f.Name == "uses").ReferencedOperators.ToArray());
    }

    [TestMethod]
    public void ADefinitionWithNoOperatorsRecordsNone()
    {
        var model = Build(Head + "CREATE TABLE public.t (n int DEFAULT 1);");

        Assert.IsEmpty(model.Tables["public.t"].Columns.Single().ReferencedOperators);
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
