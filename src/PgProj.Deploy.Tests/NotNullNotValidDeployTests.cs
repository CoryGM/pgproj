using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-121 part 2d: a <c>NOT VALID</c> NOT NULL (PostgreSQL 18) is created as a plain column and then added with
/// <c>ALTER TABLE ... ADD [CONSTRAINT name] NOT NULL col NOT VALID</c>; a valid one that becomes not valid is dropped and added back, a not valid
/// one that becomes valid is validated by its live name, and an unnamed live one cannot be, so it is a TODO.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class NotNullNotValidDeployTests
{
    private const string Table = "CREATE TABLE public.t (a integer, b integer);";
    private const string Valid = "CREATE TABLE public.t (a integer NOT NULL, b integer);";
    private const string NotValid = Table + " ALTER TABLE public.t ADD CONSTRAINT nn NOT NULL a NOT VALID;";

    [TestMethod]
    public void ANewTableWithANotValidNotNullIsCreatedPlainAndThenAddedNotValid()
    {
        var statements = Statements(DatabaseModel.Empty, Build(NotValid));

        var script = string.Join("\n", statements);
        StringAssert.Contains(script, "\"a\" integer,");
        Assert.IsFalse(script.Contains("\"a\" integer NOT NULL", StringComparison.Ordinal), script);
        StringAssert.Contains(script, "ALTER TABLE \"public\".\"t\" ADD CONSTRAINT \"nn\" NOT NULL \"a\" NOT VALID;");
    }

    [TestMethod]
    public void AnUnnamedNotValidNotNullIsAddedWithoutAName()
    {
        var statements = Statements(DatabaseModel.Empty, Build(Table + " ALTER TABLE public.t ADD NOT NULL a NOT VALID;"));

        StringAssert.Contains(string.Join("\n", statements), "ALTER TABLE \"public\".\"t\" ADD NOT NULL \"a\" NOT VALID;");
    }

    [TestMethod]
    public void ANullableColumnThatBecomesNotValidNotNullIsAddedNotValid()
    {
        var statements = Statements(Build(Table), Build(NotValid));

        var script = string.Join("\n", statements);
        StringAssert.Contains(script, "ALTER TABLE \"public\".\"t\" ADD CONSTRAINT \"nn\" NOT NULL \"a\" NOT VALID;");
        Assert.IsFalse(script.Contains("SET NOT NULL", StringComparison.Ordinal) || script.Contains("DROP NOT NULL", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void AValidNotNullThatBecomesNotValidIsDroppedAndAddedBackNotValid()
    {
        var statements = Statements(Build(Valid), Build(NotValid));

        var script = string.Join("\n", statements);
        Assert.IsTrue(script.IndexOf("ALTER COLUMN \"a\" DROP NOT NULL", StringComparison.Ordinal) is var drop && drop >= 0
            && script.IndexOf("ADD CONSTRAINT \"nn\" NOT NULL \"a\" NOT VALID", StringComparison.Ordinal) > drop, script);
    }

    [TestMethod]
    public void ANotValidNotNullThatBecomesValidIsValidatedByItsLiveName()
    {
        // The old (live) column has the name the server gave it.
        var statements = Statements(Build(Table + " ALTER TABLE public.t ADD CONSTRAINT t_a_not_null NOT NULL a NOT VALID;"), Build(Valid));

        StringAssert.Contains(string.Join("\n", statements), "ALTER TABLE \"public\".\"t\" VALIDATE CONSTRAINT \"t_a_not_null\";");
    }

    [TestMethod]
    public void ANotValidNotNullWithNoKnownNameThatBecomesValidIsATodoNotASilentSkip()
    {
        var statements = Statements(Build(Table + " ALTER TABLE public.t ADD NOT NULL a NOT VALID;"), Build(Valid));

        var script = string.Join("\n", statements);
        StringAssert.Contains(script, "-- TODO:");
        StringAssert.Contains(script, "VALIDATE CONSTRAINT");
    }

    [TestMethod]
    public void ANotValidNotNullThatBecomesNullableIsADropNotNull()
    {
        var statements = Statements(Build(NotValid), Build(Table));

        Assert.IsTrue(statements.Any(static s => s.Contains("ALTER COLUMN \"a\" DROP NOT NULL", StringComparison.Ordinal)), string.Join("\n", statements));
    }

    [TestMethod]
    public void TheSameNotValidNotNullWithNoNameDeploysNothing()
    {
        Assert.IsEmpty(Statements(Build(NotValid), Build(Table + " ALTER TABLE public.t ADD NOT NULL a NOT VALID;")));
    }

    [TestMethod]
    public void TheSameNotValidNotNullUnderAnotherNameIsRenamedInPlace()
    {
        // A name the project states is compared against the database's (G-235); before, a NOT VALID NOT NULL's name was never compared.
        var statements = Statements(Build(NotValid), Build(Table + " ALTER TABLE public.t ADD CONSTRAINT other NOT NULL a NOT VALID;"));

        Assert.AreEqual("ALTER TABLE \"public\".\"t\" RENAME CONSTRAINT \"nn\" TO \"other\";", string.Join(Environment.NewLine, statements));
    }

    [TestMethod]
    public void ANotValidNotNullColumnAddedToAnExistingTableIsAddedThenConstrained()
    {
        var statements = Statements(
            Build("CREATE TABLE public.t (a integer);"),
            Build("CREATE TABLE public.t (a integer, c integer); ALTER TABLE public.t ADD CONSTRAINT nc NOT NULL c NOT VALID;"));

        var script = string.Join("\n", statements);
        StringAssert.Contains(script, "ADD COLUMN \"c\" integer;");
        StringAssert.Contains(script, "ADD CONSTRAINT \"nc\" NOT NULL \"c\" NOT VALID;");
    }

    private static List<string> Statements(DatabaseModel oldModel, DatabaseModel newModel)
    {
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));
        return batch.Operations
            .Where(static operation => operation is not SchemaCreateOperation)
            .Select(static operation => operation.GenerateSql().Trim().Replace("\r\n", "\n", StringComparison.Ordinal))
            .ToList();
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
