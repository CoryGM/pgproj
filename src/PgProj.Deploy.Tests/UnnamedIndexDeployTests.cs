using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-225: an index written without a name is deployed without one, as written, and PostgreSQL gives it the name the model has; two such indexes on a table
/// are two indexes, not a duplicate.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class UnnamedIndexDeployTests
{
    [TestMethod]
    public void AnUnnamedIndexIsCreatedWithoutAName()
    {
        var model = Build("CREATE TABLE public.t (a int, b text); CREATE INDEX ON public.t (lower(b)); CREATE INDEX ON public.t (a); CREATE INDEX named ON public.t (b);");
        CollectionAssert.AreEquivalent(new[] { "public.t_lower_idx", "public.t_a_idx", "public.named" }, model.Indexes.Keys.ToArray());
        Assert.IsFalse(model.HasErrors, string.Join("\n", model.Diagnostics.Select(static diagnostic => diagnostic.Message)));

        var script = new ScriptGenerator().GenerateSqlScript(new ScriptGenerator().Generate(new DiffEngine().Diff(DatabaseModel.Empty, model), new DependencyAnalyzer().Analyze(DatabaseModel.Empty, model)));
        StringAssert.Contains(script, "CREATE INDEX ON \"public\".\"t\" USING btree ((lower(b)));", script);
        StringAssert.Contains(script, "CREATE INDEX ON \"public\".\"t\" USING btree (\"a\");", script);
        StringAssert.Contains(script, "CREATE INDEX \"named\" ON \"public\".\"t\"", script);
    }

    [TestMethod]
    public void TheIndexAsTheDatabaseNamesItIsNoChange()
        => Assert.IsEmpty(new DiffEngine().Diff(
            Build("CREATE TABLE public.t (a int); CREATE INDEX t_a_idx ON public.t (a);"),
            Build("CREATE TABLE public.t (a int); CREATE INDEX ON public.t (a);")).Modified);

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
