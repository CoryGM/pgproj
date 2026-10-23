using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Dependencies.Tests;

/// <summary>
/// G-290: an object in a tenant template finds an unqualified name along the tenant's own path (the tenant's schema, then the project's <c>searchPath</c>),
/// so it depends on the tenant's own object of that name, not on a shared one, as PostgreSQL resolves it in the session that deploys the tenant.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class TenantTemplateDependencyTests
{
    private const string Shared = """
        CREATE SCHEMA platform;
        CREATE FUNCTION platform.round_money(p numeric) RETURNS numeric LANGUAGE sql IMMUTABLE RETURN round(p, 2);
        CREATE FUNCTION public.label() RETURNS text LANGUAGE sql IMMUTABLE RETURN 'shared';
        CREATE TABLE public.orders (id int);
        CREATE VIEW public.shared_labels AS SELECT label(), id FROM orders;
        """;

    private const string Template = """
        CREATE TABLE orders (id bigint PRIMARY KEY, total numeric);
        CREATE FUNCTION label() RETURNS text LANGUAGE sql IMMUTABLE SET search_path FROM CURRENT RETURN 'tenant';
        CREATE VIEW order_labels AS SELECT id, label(), round_money(total) AS total FROM orders;
        """;

    [TestMethod]
    public void TheTemplatesViewDependsOnTheTenantsOwnObjectsThenTheProjectsPath()
    {
        var dependencies = DependenciesOf("tenant.order_labels");

        CollectionAssert.Contains(dependencies, "tenant.orders");
        CollectionAssert.Contains(dependencies, "tenant.label()");
        CollectionAssert.DoesNotContain(dependencies, "public.label()");
        CollectionAssert.DoesNotContain(dependencies, "public.orders");
        Assert.IsTrue(dependencies.Any(static id => id.StartsWith("platform.round_money", StringComparison.Ordinal)), string.Join(", ", dependencies));
    }

    [TestMethod]
    public void TheSharedViewStillDependsOnTheSharedObjects()
    {
        var dependencies = DependenciesOf("public.shared_labels");

        CollectionAssert.Contains(dependencies, "public.label()");
        CollectionAssert.Contains(dependencies, "public.orders");
        CollectionAssert.DoesNotContain(dependencies, "tenant.label()");
    }

    private static List<string> DependenciesOf(string nodeId)
    {
        var model = new SemanticPipeline().Execute(
            [
                new PgQueryParser().Parse(Shared),
                new PgQueryParser("tenant").Parse(Template) with { SearchPath = ["tenant", "platform", "public"] },
            ],
            searchPath: ["platform", "public"]);

        return new DependencyAnalyzer().Analyze(model).GetDependencies(nodeId).Select(static node => node.NodeId).ToList();
    }
}
