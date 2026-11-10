using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Dependencies.Tests;

/// <summary>
/// G-267: a base type and its support functions refer to each other; the edge from a support function to its type is left out, since a shell stands in for the type, so
/// the functions come first, the type after them, and whatever else uses the type after it (a function that takes it but is not one it names, a table).
/// </summary>
[TestClass]
public sealed class BaseTypeDependencyTests
{
    [TestMethod]
    public void TheSupportFunctionsComeFirstThenTheTypeThenWhatUsesIt()
    {
        var model = new SemanticPipeline().Execute([new PgQueryParser().Parse("""
            CREATE TABLE public.t (v public.x);
            CREATE FUNCTION public.x_other(public.x) RETURNS integer LANGUAGE internal AS $$int4out$$;
            CREATE TYPE public.x (INPUT = x_in, OUTPUT = x_out, LIKE = int4);
            CREATE FUNCTION public.x_in(cstring) RETURNS public.x LANGUAGE internal IMMUTABLE STRICT AS $$int4in$$;
            CREATE FUNCTION public.x_out(public.x) RETURNS cstring LANGUAGE internal IMMUTABLE STRICT AS $$int4out$$;
            """)]);

        var order = new DependencyAnalyzer().Analyze(model).TopologicalSort().Select(static node => node.NodeId).ToList();
        int At(string prefix) => order.FindIndex(id => id.StartsWith(prefix, StringComparison.Ordinal));

        Assert.IsLessThan(order.IndexOf("public.x"), At("public.x_in("), string.Join(", ", order));
        Assert.IsLessThan(order.IndexOf("public.x"), At("public.x_out("), string.Join(", ", order));
        Assert.IsGreaterThan(order.IndexOf("public.x"), At("public.x_other("), "A function that takes the type and is not one it names comes after it.");
        Assert.IsGreaterThan(order.IndexOf("public.x"), order.IndexOf("public.t"));
        Assert.IsFalse(model.Diagnostics.Any(static diagnostic => diagnostic.Message.Contains("cycle", StringComparison.OrdinalIgnoreCase)));
    }
}
