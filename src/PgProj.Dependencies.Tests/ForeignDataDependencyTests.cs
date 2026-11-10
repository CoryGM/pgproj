using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Dependencies.Tests;

/// <summary>G-132: a server comes after its wrapper, or the extension that installs it; a user mapping and a grant on a server after the server. G-133: a foreign table after its server.</summary>
[TestClass]
public sealed class ForeignDataDependencyTests
{
    [TestMethod]
    [DataRow("CREATE FOREIGN DATA WRAPPER w;", "foreign data wrapper:w")]
    [DataRow("CREATE EXTENSION w;", "extension:w")]
    public void AServerComesAfterItsWrapperAndItsMappingsAndGrantsAfterIt(string wrapper, string wrapperKey)
    {
        var model = new SemanticPipeline().Execute([new PgQueryParser().Parse(
            "GRANT USAGE ON FOREIGN SERVER s TO app; CREATE USER MAPPING FOR app SERVER s; CREATE SERVER s FOREIGN DATA WRAPPER w; " + wrapper)]);

        var order = new DependencyAnalyzer().Analyze(model).TopologicalSort().Select(static node => node.NodeId).ToList();

        Assert.IsLessThan(order.IndexOf("server:s"), order.IndexOf(wrapperKey), string.Join(", ", order));
        Assert.IsGreaterThan(order.IndexOf("server:s"), order.IndexOf("user mapping:app on s"));
        Assert.IsGreaterThan(order.IndexOf("server:s"), order.FindIndex(static id => id.StartsWith("server:s->", StringComparison.Ordinal)), string.Join(", ", order));
    }

    [TestMethod]
    public void AForeignTableComesAfterItsServerAndAViewOfItAfterTheTable()
    {
        var model = new SemanticPipeline().Execute([new PgQueryParser().Parse(
            "CREATE VIEW public.v AS SELECT a FROM public.ft; CREATE FOREIGN TABLE public.ft (a integer) SERVER s; CREATE SERVER s FOREIGN DATA WRAPPER w; CREATE FOREIGN DATA WRAPPER w;")]);

        var order = new DependencyAnalyzer().Analyze(model).TopologicalSort().Select(static node => node.NodeId).ToList();

        Assert.IsLessThan(order.IndexOf("public.ft"), order.IndexOf("server:s"), string.Join(", ", order));
        Assert.IsGreaterThan(order.IndexOf("public.ft"), order.IndexOf("public.v"), string.Join(", ", order));
    }
}
