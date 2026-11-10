using PgProj.Core;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core.Tests;

/// <summary>
/// G-344: a live index with an expression key carries each key as PostgreSQL rendered it, and only an index with an expression key does; the project's
/// expression is sent to the server only when PgQuery reads it as exactly one key expression. (The rendering itself is checked against real servers by the
/// integration tests.)
/// </summary>
[TestClass]
public sealed class IndexKeyRenderingTests
{
    private const string Sql = """
        CREATE TYPE public.color AS ENUM ('r', 'g');
        CREATE TABLE public.t (id int, c public.color);
        CREATE INDEX t_c_r ON public.t (id, (c = 'r'::public.color));
        CREATE INDEX t_id ON public.t (id);
        """;

    [TestMethod]
    public void ALiveIndexWithAnExpressionKeyCarriesItsKeysRenderings()
    {
        var live = new SemanticPipeline().Execute([new PgQueryParser().Parse(Sql)]);

        ServerIndexPredicateRenderer.AttachLiveRenderings(live, [
            new LiveIndexSnapshot("public", "t", "t_c_r", "CREATE INDEX t_c_r ON public.t USING btree (id, ((c = 'r'::color)))") { KeyRenderings = ["id", "(c = 'r'::color) "] },
            new LiveIndexSnapshot("public", "t", "t_id", "CREATE INDEX t_id ON public.t USING btree (id)") { KeyRenderings = ["id"] },
        ]);

        CollectionAssert.AreEqual(new[] { "id", "(c = 'r'::color)" }, live.Indexes.Values.Single(static index => index.Name == "t_c_r").ServerRenderedKeys!.ToArray());
        Assert.IsNull(live.Indexes.Values.Single(static index => index.Name == "t_id").ServerRenderedKeys, "A column index needs no rendering.");
    }

    [TestMethod]
    [DataRow("c = 'r'", true)]
    [DataRow("lower(name)", true)]
    [DataRow("c = 'r')); DROP TABLE t; --", false)]
    [DataRow("c = 'r')) WHERE ((true", false)]
    [DataRow("c)), ((id", false)]
    public void OnlyOneKeyExpressionIsSentToTheServer(string expression, bool sent)
    {
        Assert.AreEqual(sent, new ServerIndexPredicateRenderer(new PgQueryParser()).IsIndexExpression(expression));
    }
}
