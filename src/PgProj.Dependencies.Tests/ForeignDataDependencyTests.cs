using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Dependencies.Tests;

/// <summary>G-132: a server comes after its wrapper, or the extension that installs it; a user mapping and a grant on a server after the server.</summary>
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
}
