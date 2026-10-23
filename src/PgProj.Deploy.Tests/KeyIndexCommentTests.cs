using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-295: the index of a primary key, unique, or exclusion constraint has the constraint's name, and <c>COMMENT ON INDEX</c> can comment it (<c>LIKE ... INCLUDING COMMENTS</c> copies it).
/// The statement named no index of the model and was ignored.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class KeyIndexCommentTests
{
    private const string Table = "CREATE TABLE public.t (id int PRIMARY KEY, code text, CONSTRAINT t_code_key UNIQUE (code));\nCREATE INDEX t_code_idx ON public.t (code);\n";

    [TestMethod]
    public void TheCommentIsKeptOnTheConstraintAndAPlainIndexKeepsItsOwn()
    {
        var model = Build(Table + """
            COMMENT ON INDEX public.t_pkey IS 'pkey index';
            COMMENT ON INDEX public.t_code_key IS 'unique index';
            COMMENT ON INDEX public.t_code_idx IS 'plain';
            """);

        Assert.AreEqual("pkey index", model.Constraints.Values.Single(static c => c.Name == "t_pkey").IndexComment);
        Assert.AreEqual("unique index", model.Constraints.Values.Single(static c => c.Name == "t_code_key").IndexComment);
        Assert.AreEqual("plain", model.Indexes.Values.Single().Comment);
        Assert.IsFalse(model.Constraints.Values.Any(static c => c.Name == "t_code_key" && c.CommentStated), "the index comment is not the constraint's comment");
    }

    [TestMethod]
    public void ANewKeyIsFollowedByTheCommentOnItsIndex()
    {
        var script = Script(DatabaseModel.Empty, Build(Table + "COMMENT ON INDEX public.t_pkey IS 'pkey index';"));

        StringAssert.Contains(script, "COMMENT ON INDEX \"public\".\"t_pkey\" IS 'pkey index';");
        Assert.DoesNotContain("COMMENT ON CONSTRAINT", script);
    }

    [TestMethod]
    public void AChangedOrRemovedCommentIsChangedInPlaceAndTheKeyIsNotRecreated()
    {
        var live = Build(Table + "COMMENT ON INDEX public.t_pkey IS 'old';\nCOMMENT ON INDEX public.t_code_key IS 'x';");
        var project = Build(Table + "COMMENT ON INDEX public.t_pkey IS 'new';\nCOMMENT ON INDEX public.t_code_key IS NULL;");

        var script = Script(live, project);

        StringAssert.Contains(script, "COMMENT ON INDEX \"public\".\"t_pkey\" IS 'new';");
        StringAssert.Contains(script, "COMMENT ON INDEX \"public\".\"t_code_key\" IS NULL;");
        Assert.DoesNotContain("DROP", script);
        Assert.DoesNotContain("ADD CONSTRAINT", script);
    }

    [TestMethod]
    public void OnlyACommentTheProjectStatesIsCompared()
    {
        var same = Build(Table + "COMMENT ON INDEX public.t_pkey IS 'a';");
        var unstated = Build(Table);
        var other = Build(Table + "COMMENT ON INDEX public.t_pkey IS 'b';");

        Assert.AreEqual(0, ChangeCount(new DiffEngine().Diff(same, same)));
        Assert.AreEqual(0, ChangeCount(new DiffEngine().Diff(same, unstated)), "a comment the project does not state is left alone");
        Assert.AreEqual(1, ChangeCount(new DiffEngine().Diff(same, other)));
        Assert.AreEqual(1, ChangeCount(new DiffEngine().Diff(unstated, same)));
    }

    private static int ChangeCount(ChangeSet diff) => diff.Added.Count + diff.Removed.Count + diff.Modified.Count;

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);

    private static string Script(DatabaseModel live, DatabaseModel project)
    {
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(live, project), new DependencyAnalyzer().Analyze(live, project));
        return string.Join("\n", batch.Operations.Where(static operation => operation is not SchemaCreateOperation).Select(static operation => operation.GenerateSql().Replace("\r\n", "\n", StringComparison.Ordinal))).Trim();
    }
}
