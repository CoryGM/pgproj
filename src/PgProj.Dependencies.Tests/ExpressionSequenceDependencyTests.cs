using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Dependencies.Tests;

/// <summary>
/// G-174: PostgreSQL resolves a sequence named in a view, a <c>CHECK</c> constraint, an index expression, or a policy when the object
/// is created, so the sequence comes first. Each object is named so that it sorts before the sequence (<c>a_</c> and <c>z_seq</c>),
/// since an order that merely follows the names would pass without the dependency.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ExpressionSequenceDependencyTests
{
    private const string Setup = "CREATE TABLE public.t (id bigint, owner_id bigint); CREATE SEQUENCE public.z_seq; ";

    [TestMethod]
    public void AViewIsCreatedAfterTheSequenceItNames()
        => AssertBefore(Setup + "CREATE VIEW public.a_v AS SELECT nextval('public.z_seq') AS n;", "public.z_seq", "public.a_v");

    [TestMethod]
    public void AMaterializedViewIsCreatedAfterTheSequenceItNames()
        => AssertBefore(Setup + "CREATE MATERIALIZED VIEW public.a_m AS SELECT 1 AS x WHERE false AND currval('public.z_seq'::regclass) > 0;", "public.z_seq", "public.a_m");

    [TestMethod]
    public void ACheckConstraintIsCreatedAfterTheSequenceItNames()
        => AssertBefore(
            "CREATE TABLE public.t (id bigint, CONSTRAINT a_ck CHECK (id < currval('public.z_seq'::regclass))); CREATE SEQUENCE public.z_seq;",
            "public.z_seq",
            "a_ck");

    [TestMethod]
    public void APolicyIsCreatedAfterTheSequenceItNames()
        => AssertBefore(Setup + "CREATE POLICY a_pol ON public.t USING (id < currval('public.z_seq'::regclass));", "public.z_seq", "a_pol");

    [TestMethod]
    public void AnUnqualifiedNameIsFoundInTheObjectsSchema()
        => AssertBefore(
            "CREATE SCHEMA s; CREATE SEQUENCE s.z_seq; CREATE VIEW s.a_v AS SELECT nextval('z_seq') AS n;",
            "s.z_seq",
            "s.a_v");

    [TestMethod]
    public void AViewOverANameThatIsNotAProjectSequenceHasNoSequenceEdge()
    {
        var model = new SemanticPipeline().Execute([new PgQueryParser().Parse(Setup + "CREATE VIEW public.a_v AS SELECT nextval('public.elsewhere_seq') AS n;")]);

        var graph = new DependencyAnalyzer().Analyze(model);

        Assert.IsFalse(graph.GetDependencies("public.a_v").Any(static node => node.NodeId == "public.z_seq"));
    }

    private static void AssertBefore(string sql, string first, string then)
    {
        var model = new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
        var order = new DependencyAnalyzer().Analyze(model).TopologicalSort().Select(static node => node.NodeId).ToList();

        var firstIndex = order.FindIndex(id => Matches(id, first));
        var thenIndex = order.FindIndex(id => Matches(id, then));
        Assert.IsGreaterThanOrEqualTo(0, firstIndex, string.Join(", ", order));
        Assert.IsGreaterThanOrEqualTo(0, thenIndex, string.Join(", ", order));
        Assert.IsLessThan(thenIndex, firstIndex, string.Join(", ", order));
    }

    private static bool Matches(string id, string name)
        => string.Equals(id, name, StringComparison.OrdinalIgnoreCase)
            || id.EndsWith("." + name, StringComparison.OrdinalIgnoreCase);
}
