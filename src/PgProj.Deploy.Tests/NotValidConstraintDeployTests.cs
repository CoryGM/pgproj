using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-234: a CHECK or FOREIGN KEY written <c>NOT VALID</c> is added <c>NOT VALID</c>. It was added valid, which fails against rows that break it. Compared in
/// one direction: the project stating it valid while the database has it not valid is <c>VALIDATE CONSTRAINT</c>, in place; the project stating it
/// <c>NOT VALID</c> while the database has it validated is no change.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class NotValidConstraintDeployTests
{
    private const string Tables = "CREATE TABLE public.p (id int PRIMARY KEY); CREATE TABLE public.t (b int, p_id int); ";

    [TestMethod]
    public void ACheckAndAForeignKeyAreAddedNotValid()
    {
        var script = Script(DatabaseModel.Empty, Build(Tables
            + "ALTER TABLE public.t ADD CONSTRAINT chk CHECK (b < 50) NOT VALID; ALTER TABLE public.t ADD CONSTRAINT fk FOREIGN KEY (p_id) REFERENCES public.p (id) NOT VALID;"));

        StringAssert.Contains(script, "CHECK (b < 50) NOT VALID;");
        StringAssert.Contains(script, "REFERENCES \"public\".\"p\" (\"id\") NOT VALID;");
    }

    [TestMethod]
    public void NotValidWrittenInsideCreateTableIsKept()
    {
        var script = Script(DatabaseModel.Empty, Build("CREATE TABLE public.t (b int, CONSTRAINT chk CHECK (b < 50) NOT VALID);"));

        StringAssert.Contains(script, "CHECK (b < 50) NOT VALID;");
    }

    [TestMethod]
    public void AValidCheckIsStillAddedValid()
    {
        var script = Script(DatabaseModel.Empty, Build(Tables + "ALTER TABLE public.t ADD CONSTRAINT chk CHECK (b < 50);"));

        Assert.IsFalse(script.Contains("NOT VALID", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void ANotValidConstraintInTheDatabaseThatTheProjectStatesValidIsValidatedInPlace()
    {
        var script = Script(
            Build(Tables + "ALTER TABLE public.t ADD CONSTRAINT chk CHECK (b < 50) NOT VALID;"),
            Build(Tables + "ALTER TABLE public.t ADD CONSTRAINT chk CHECK (b < 50);"));

        StringAssert.Contains(script, "VALIDATE CONSTRAINT \"chk\"");
        Assert.IsFalse(script.Contains("DROP CONSTRAINT", StringComparison.Ordinal), script);
        Assert.IsFalse(script.Contains("ADD CONSTRAINT", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void AValidatedConstraintSatisfiesAProjectThatStatesNotValid()
    {
        Assert.AreEqual(string.Empty, Script(
            Build(Tables + "ALTER TABLE public.t ADD CONSTRAINT chk CHECK (b < 50);"),
            Build(Tables + "ALTER TABLE public.t ADD CONSTRAINT chk CHECK (b < 50) NOT VALID;")));
    }

    [TestMethod]
    public void TheSameConstraintOnBothSidesIsNoChange()
    {
        var model = Build(Tables + "ALTER TABLE public.t ADD CONSTRAINT chk CHECK (b < 50) NOT VALID;");

        Assert.AreEqual(string.Empty, Script(model, model));
        Assert.AreEqual(string.Empty, Script(model, Build(Tables + "ALTER TABLE public.t ADD CONSTRAINT chk CHECK ( b<50 ) NOT VALID;")));
    }

    [TestMethod]
    public void AChangedExpressionIsStillAChange()
    {
        var script = Script(
            Build(Tables + "ALTER TABLE public.t ADD CONSTRAINT chk CHECK (b < 50) NOT VALID;"),
            Build(Tables + "ALTER TABLE public.t ADD CONSTRAINT chk CHECK (b < 60) NOT VALID;"));

        StringAssert.Contains(script, "DROP CONSTRAINT");
        StringAssert.Contains(script, "CHECK (b < 60) NOT VALID");
    }

    private static string Script(DatabaseModel oldModel, DatabaseModel newModel)
    {
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));
        return string.Join("\n", batch.Operations.Where(static operation => operation is not SchemaCreateOperation).Select(static operation => operation.GenerateSql().Replace("\r\n", "\n", StringComparison.Ordinal)));
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
