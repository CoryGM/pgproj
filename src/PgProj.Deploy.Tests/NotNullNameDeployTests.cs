using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-235: on PostgreSQL 18 a column's <c>NOT NULL</c> is a constraint with a name and a comment. The name the project gives it is deployed, a different one
/// is renamed in place, and its comment is set. The name is compared only when the project states one and the database has one, so an unnamed NOT NULL, and
/// every NOT NULL on an older server (which has no name), is unaffected.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class NotNullNameDeployTests
{
    [TestMethod]
    public void ANamedNotNullIsCreatedWithItsNameAndComment()
    {
        var script = Script(DatabaseModel.Empty, Build("""
            CREATE TABLE public.t (a int CONSTRAINT nn NOT NULL, b int NOT NULL);
            COMMENT ON CONSTRAINT nn ON public.t IS 'the a';
            """));

        StringAssert.Contains(script, "\"a\" int CONSTRAINT \"nn\" NOT NULL");
        StringAssert.Contains(script, "\"b\" int NOT NULL");
        StringAssert.Contains(script, "COMMENT ON CONSTRAINT \"nn\" ON \"public\".\"t\" IS 'the a';");
    }

    [TestMethod]
    public void TheTableAndAlterFormsNameTheColumnsNotNullToo()
    {
        var model = Build("""
            CREATE TABLE public.t (a int, b int, CONSTRAINT nn_a NOT NULL a);
            ALTER TABLE public.t ADD CONSTRAINT nn_b NOT NULL b;
            """);

        var columns = model.Tables["public.t"].Columns;
        Assert.AreEqual("nn_a", columns.Single(static c => c.Name == "a").NotNullName);
        Assert.AreEqual("nn_b", columns.Single(static c => c.Name == "b").NotNullName);
    }

    [TestMethod]
    public void ADifferentNameThePostgreSql18DatabaseHasIsRenamedInPlace()
    {
        var script = Script(
            Build("CREATE TABLE public.t (a int CONSTRAINT t_a_not_null NOT NULL);"),
            Build("CREATE TABLE public.t (a int CONSTRAINT nn NOT NULL);"));

        StringAssert.Contains(script, "ALTER TABLE \"public\".\"t\" RENAME CONSTRAINT \"t_a_not_null\" TO \"nn\";");
        Assert.IsFalse(script.Contains("DROP", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void AProjectThatStatesNoNameIsNotComparedWhateverTheDatabaseCallsIt()
    {
        Assert.AreEqual(string.Empty, Script(
            Build("CREATE TABLE public.t (a int CONSTRAINT custom NOT NULL);"),
            Build("CREATE TABLE public.t (a int NOT NULL);")));
    }

    [TestMethod]
    public void ADatabaseWithNoNameLikeAnOlderServerIsNotComparedEither()
    {
        Assert.AreEqual(string.Empty, Script(
            Build("CREATE TABLE public.t (a int NOT NULL);"),
            Build("CREATE TABLE public.t (a int CONSTRAINT nn NOT NULL);")));
    }

    [TestMethod]
    public void TheSameNameOnBothSidesIsNoChange()
    {
        var model = Build("CREATE TABLE public.t (a int CONSTRAINT nn NOT NULL);");

        Assert.AreEqual(string.Empty, Script(model, model));
    }

    [TestMethod]
    public void ACommentTheProjectStatesIsSetAndChanged()
    {
        var script = Script(
            Build("CREATE TABLE public.t (a int CONSTRAINT nn NOT NULL); COMMENT ON CONSTRAINT nn ON public.t IS 'old';"),
            Build("CREATE TABLE public.t (a int CONSTRAINT nn NOT NULL); COMMENT ON CONSTRAINT nn ON public.t IS 'new';"));

        StringAssert.Contains(script, "COMMENT ON CONSTRAINT \"nn\" ON \"public\".\"t\" IS 'new';");
    }

    [TestMethod]
    public void ACommentTheProjectDoesNotStateIsNotCompared()
    {
        Assert.AreEqual(string.Empty, Script(
            Build("CREATE TABLE public.t (a int CONSTRAINT nn NOT NULL); COMMENT ON CONSTRAINT nn ON public.t IS 'old';"),
            Build("CREATE TABLE public.t (a int CONSTRAINT nn NOT NULL);")));
    }

    [TestMethod]
    public void AColumnThatBecomesNotNullIsNamedAfterSetNotNullOnAServerThatStoresTheName()
    {
        var script = Script(
            Build("CREATE TABLE public.t (a int);"),
            Build("CREATE TABLE public.t (a int CONSTRAINT nn NOT NULL); COMMENT ON CONSTRAINT nn ON public.t IS 'c';"));

        StringAssert.Contains(script, "ALTER COLUMN \"a\" SET NOT NULL;");
        StringAssert.Contains(script, "DO $pgproj$");
        StringAssert.Contains(script, "server_version_num')::int >= 180000");
        StringAssert.Contains(script, "RENAME CONSTRAINT %I TO %I");
        StringAssert.Contains(script, "'nn'");
        StringAssert.Contains(script, "COMMENT ON CONSTRAINT \"nn\" ON \"public\".\"t\" IS 'c';");
    }

    [TestMethod]
    public void AColumnThatBecomesNotNullUnderPostgreSqlsOwnNameIsNotRenamed()
    {
        var script = Script(
            Build("CREATE TABLE public.t (a int);"),
            Build("CREATE TABLE public.t (a int CONSTRAINT t_a_not_null NOT NULL);"));

        Assert.IsFalse(script.Contains("DO $pgproj$", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void AChildTakesItsParentsNameForAnInheritedNotNullUnlessItStatesItsOwn()
    {
        var model = Build("""
            CREATE TABLE public.p (a int CONSTRAINT pnn NOT NULL);
            CREATE TABLE public.c1 () INHERITS (public.p);
            CREATE TABLE public.c2 (a int CONSTRAINT own NOT NULL) INHERITS (public.p);
            """);

        Assert.AreEqual("pnn", model.Tables["public.c1"].Columns.Single().NotNullName);
        Assert.AreEqual("own", model.Tables["public.c2"].Columns.Single().NotNullName);
    }

    private static string Script(DatabaseModel oldModel, DatabaseModel newModel)
    {
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));
        return string.Join("\n", batch.Operations.Where(static operation => operation is not SchemaCreateOperation).Select(static operation => operation.GenerateSql().Replace("\r\n", "\n", StringComparison.Ordinal))).Trim();
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
