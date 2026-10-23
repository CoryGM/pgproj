using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-77: real <c>CREATE DOMAIN</c> support - the base type, DEFAULT, NOT NULL, and CHECK constraints, previously a
/// <c>-- TODO: Generate CREATE TYPE SQL</c> placeholder - plus in-place <c>ALTER DOMAIN</c> for the changes that
/// have one, since dropping and recreating a domain fails while any column still uses it.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class DomainOperationTests
{
    [TestMethod]
    public void CreateOperationEmitsFullDomainDefinition()
    {
        var script = new ScriptGenerator().GenerateSqlScript(Generate(
            DatabaseModel.Empty,
            BuildModel("create domain public.positive_amount as numeric(10,2) default 0 constraint positive check (value > 0) not null;")));

        StringAssert.Contains(
            script,
            "CREATE DOMAIN \"public\".\"positive_amount\" AS numeric(10,2) DEFAULT 0 CONSTRAINT \"positive\" CHECK (value > 0) NOT NULL;");
    }

    [TestMethod]
    public void CreateOperationEmitsFriendlyBaseTypeName()
    {
        var script = new ScriptGenerator().GenerateSqlScript(Generate(
            DatabaseModel.Empty,
            BuildModel("create domain public.d as integer;")));

        StringAssert.Contains(script, "CREATE DOMAIN \"public\".\"d\" AS integer;");
    }

    [TestMethod]
    public void CreateOperationEmitsGeneratedNameForUnnamedCheck()
    {
        var script = new ScriptGenerator().GenerateSqlScript(Generate(
            DatabaseModel.Empty,
            BuildModel("create domain public.d as integer check (value > 0);")));

        StringAssert.Contains(script, "CONSTRAINT \"d_check\" CHECK (value > 0)");
    }

    [TestMethod]
    public void DropOperationEmitsDropDomainNotDropType()
    {
        var sql = new DropOperation
        {
            NodeId = "public.d",
            NodeKind = ChangeNodeKind.Type,
            Node = BuildModel("create domain public.d as integer;").Types["public.d"],
        }.GenerateSql();

        Assert.AreEqual("DROP DOMAIN \"public\".\"d\";", sql);
    }

    [TestMethod]
    public void DefaultChangeIsAlteredInPlace()
    {
        var script = new ScriptGenerator().GenerateSqlScript(Generate(
            BuildModel("create domain public.d as integer default 1;"),
            BuildModel("create domain public.d as integer default 2;")));

        StringAssert.Contains(script, "ALTER DOMAIN \"public\".\"d\" SET DEFAULT 2;");
        Assert.IsFalse(script.Contains("DROP DOMAIN", StringComparison.Ordinal), script);
        Assert.IsFalse(script.Contains("CREATE DOMAIN", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void ADefaultChangeNextToAnUnnamedCheckReadBackFromTheServerIsAlteredInPlace()
    {
        // G-98: the live side has the unnamed CHECK under PostgreSQL's default name; it must not count as a changed set of
        // unnamed CHECKs, which would force a DROP DOMAIN that fails while a column uses the domain.
        var script = new ScriptGenerator().GenerateSqlScript(Generate(
            BuildModel("create domain public.d as integer default 1 constraint d_check check ((value > 0));"),
            BuildModel("create domain public.d as integer default 2 check (value > 0);")));

        StringAssert.Contains(script, "ALTER DOMAIN \"public\".\"d\" SET DEFAULT 2;");
        Assert.IsFalse(script.Contains("DROP DOMAIN", StringComparison.Ordinal), script);
        Assert.IsFalse(script.Contains("CONSTRAINT", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void DroppingTheDefaultIsAlteredInPlace()
    {
        var script = new ScriptGenerator().GenerateSqlScript(Generate(
            BuildModel("create domain public.d as integer default 1;"),
            BuildModel("create domain public.d as integer;")));

        StringAssert.Contains(script, "ALTER DOMAIN \"public\".\"d\" DROP DEFAULT;");
    }

    [TestMethod]
    public void NotNullChangeIsAlteredInPlace()
    {
        var script = new ScriptGenerator().GenerateSqlScript(Generate(
            BuildModel("create domain public.d as integer;"),
            BuildModel("create domain public.d as integer not null;")));

        StringAssert.Contains(script, "ALTER DOMAIN \"public\".\"d\" SET NOT NULL;");
    }

    [TestMethod]
    public void NamedCheckConstraintExpressionChangeIsAlteredInPlace()
    {
        var script = new ScriptGenerator().GenerateSqlScript(Generate(
            BuildModel("create domain public.d as integer constraint positive check (value > 0);"),
            BuildModel("create domain public.d as integer constraint positive check (value > 10);")));

        StringAssert.Contains(script, "ALTER DOMAIN \"public\".\"d\" DROP CONSTRAINT \"positive\";");
        StringAssert.Contains(script, "ALTER DOMAIN \"public\".\"d\" ADD CONSTRAINT \"positive\" CHECK (value > 10);");
        Assert.IsFalse(script.Contains("DROP DOMAIN", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void AddingANamedCheckConstraintIsAlteredInPlace()
    {
        var script = new ScriptGenerator().GenerateSqlScript(Generate(
            BuildModel("create domain public.d as integer;"),
            BuildModel("create domain public.d as integer constraint positive check (value > 0);")));

        StringAssert.Contains(script, "ALTER DOMAIN \"public\".\"d\" ADD CONSTRAINT \"positive\" CHECK (value > 0);");
        Assert.IsFalse(script.Contains("DROP DOMAIN", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void ReorderingUnnamedCheckConstraintsProducesNoOperation()
    {
        var batch = Generate(
            BuildModel("create domain public.d as integer check (value > 0) check (value < 100);"),
            BuildModel("create domain public.d as integer check (value < 100) check (value > 0);"));

        Assert.IsEmpty(batch.Operations);
    }

    /// <summary>Base type has no in-place ALTER, so this must fall back to drop-then-recreate (G-77).</summary>
    [TestMethod]
    public void BaseTypeChangeFallsBackToDropAndRecreate()
    {
        var script = new ScriptGenerator().GenerateSqlScript(Generate(
            BuildModel("create domain public.d as varchar(10);"),
            BuildModel("create domain public.d as varchar(20);")));

        StringAssert.Contains(script, "DROP DOMAIN \"public\".\"d\";");
        StringAssert.Contains(script, "CREATE DOMAIN \"public\".\"d\" AS varchar(20);");
    }

    /// <summary>
    /// G-109 (replacing G-77's drop-then-recreate fallback, with the user's approval): adding an unnamed CHECK is done in place, unnamed,
    /// so PostgreSQL gives it the next free default name; positional names are left out of the comparison (G-98), so that converges.
    /// </summary>
    [TestMethod]
    public void AddingAnUnnamedCheckConstraintIsAlteredInPlace()
    {
        var script = new ScriptGenerator().GenerateSqlScript(Generate(
            BuildModel("create domain public.d as integer constraint d_check check ((value > 0));"),
            BuildModel("create domain public.d as integer check (value > 0) check (value < 100);")));

        StringAssert.Contains(script, "ALTER DOMAIN \"public\".\"d\" ADD CHECK (value < 100);");
        Assert.IsFalse(script.Contains("DROP DOMAIN", StringComparison.Ordinal), script);
        Assert.IsFalse(script.Contains("DROP CONSTRAINT", StringComparison.Ordinal), "The surviving CHECK is left alone.\n" + script);
    }

    /// <summary>G-109: a changed unnamed CHECK is dropped under the name the database gave it and added again unnamed.</summary>
    [TestMethod]
    public void ChangingAnUnnamedCheckConstraintIsAlteredInPlaceUnderItsDatabaseName()
    {
        // The live side reads the second CHECK back under the name PostgreSQL gave it (d_check1).
        var script = new ScriptGenerator().GenerateSqlScript(Generate(
            BuildModel("create domain public.d as text constraint d_check check ((value <> ''::text)) constraint d_check1 check ((length(value) < 5));"),
            BuildModel("create domain public.d as text check (value <> '') check (length(value) < 6);")));

        StringAssert.Contains(script, "ALTER DOMAIN \"public\".\"d\" DROP CONSTRAINT \"d_check1\";");
        StringAssert.Contains(script, "ALTER DOMAIN \"public\".\"d\" ADD CHECK (length(value) < 6);");
        Assert.IsFalse(script.Contains("\"d_check\";", StringComparison.Ordinal), "The unchanged CHECK is left alone.\n" + script);
        Assert.IsFalse(script.Contains("DROP DOMAIN", StringComparison.Ordinal), script);
        Assert.IsTrue(script.IndexOf("DROP CONSTRAINT", StringComparison.Ordinal) < script.IndexOf("ADD CHECK", StringComparison.Ordinal), script);
    }

    /// <summary>G-109: a removed unnamed CHECK is dropped under its database name; nothing is added.</summary>
    [TestMethod]
    public void RemovingAnUnnamedCheckConstraintDropsItUnderItsDatabaseName()
    {
        var script = new ScriptGenerator().GenerateSqlScript(Generate(
            BuildModel("create domain public.d as integer constraint d_check check ((value > 0)) constraint d_check1 check ((value < 100));"),
            BuildModel("create domain public.d as integer check (value < 100);")));

        StringAssert.Contains(script, "ALTER DOMAIN \"public\".\"d\" DROP CONSTRAINT \"d_check\";");
        Assert.IsFalse(script.Contains("ADD CHECK", StringComparison.Ordinal), script);
        Assert.IsFalse(script.Contains("DROP DOMAIN", StringComparison.Ordinal), script);
    }

    private static OperationBatch Generate(DatabaseModel oldModel, DatabaseModel newModel)
        => new ScriptGenerator().Generate(
            new DiffEngine().Diff(oldModel, newModel),
            new DependencyAnalyzer().Analyze(oldModel, newModel));

    private static DatabaseModel BuildModel(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
