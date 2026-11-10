using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-119 (the user's decisions of 2026-11-09): PostgreSQL refuses <c>ALTER TYPE ... ALTER ATTRIBUTE ... TYPE</c> while a table column uses the type, so a
/// composite type that retypes a field under columns is rebuilt under them, as an enum is (G-191): the old type renamed out of the way, the new one created
/// under its own name, each column moved with its value built field by field, by name, and read back through text, the old type dropped. A narrowing or a
/// dropped field is destructive and a widening is not; a type a domain, another composite, a routine, or a typed table uses is refused, naming why. Adding
/// and dropping fields, and a retype nothing uses, stay in place.
/// </summary>
[TestClass]
public sealed class CompositeFieldRetypeInUseDeployTests
{
    private const string Use = " CREATE TABLE public.t (id int, a public.addr);";

    [TestMethod]
    public void ARetypedFieldOfATypeInUseIsRebuiltUnderItsColumns()
    {
        var batch = Generate(
            Build($"CREATE TYPE public.addr AS (street text, zip varchar(5), n int);{Use}"),
            Build($"CREATE TYPE public.addr AS (street text, zip varchar(10), n int);{Use}"));

        Assert.IsEmpty(batch.GetUnsupportedOperations());
        var sql = batch.Operations.Select(static operation => operation.GenerateSql()).ToList();
        var rename = sql.FindIndex(static text => text == "ALTER TYPE \"public\".\"addr\" RENAME TO \"addr_pgproj_old\";");
        var create = sql.FindIndex(static text => text.StartsWith("CREATE TYPE \"public\".\"addr\"", StringComparison.Ordinal));
        var move = sql.FindIndex(static text => text.Contains(
            "ALTER TABLE \"public\".\"t\" ALTER COLUMN \"a\" TYPE \"public\".\"addr\" USING CASE WHEN \"a\"::text IS NULL THEN NULL ELSE ROW((\"a\").\"street\", (\"a\").\"zip\", (\"a\").\"n\")::text::\"public\".\"addr\" END;",
            StringComparison.Ordinal));
        Assert.IsTrue(rename >= 0 && create > rename && move > create, string.Join(Environment.NewLine, sql));
        StringAssert.EndsWith(sql[move], "DROP TYPE \"public\".\"addr_pgproj_old\";");
        Assert.DoesNotContain("ALTER ATTRIBUTE", string.Join(Environment.NewLine, sql));
        Assert.IsEmpty(batch.GetDestructiveOperations(), "a widening keeps every value");
    }

    [TestMethod]
    public void TheNewTypesFieldsAreTakenByNameAndANewOneIsNull()
    {
        var batch = Generate(
            Build($"CREATE TYPE public.addr AS (street text, zip varchar(5), n int);{Use}"),
            Build($"CREATE TYPE public.addr AS (n bigint, extra int, street text, zip varchar(5));{Use}"));

        var swap = batch.Operations.OfType<CompositeSwapOperation>().Single().GenerateSql();
        StringAssert.Contains(swap, "ROW((\"a\").\"n\", NULL, (\"a\").\"street\", (\"a\").\"zip\")::text::\"public\".\"addr\"");
    }

    [TestMethod]
    public void ANarrowedFieldIsDestructiveAndAWideningIsNot()
    {
        var narrowed = Generate(Build($"CREATE TYPE public.addr AS (zip varchar(10), n int);{Use}"), Build($"CREATE TYPE public.addr AS (zip varchar(5), n int);{Use}"));
        var widened = Generate(Build($"CREATE TYPE public.addr AS (zip varchar(10), n int);{Use}"), Build($"CREATE TYPE public.addr AS (zip text, n bigint);{Use}"));

        StringAssert.Contains(narrowed.GetDestructiveOperations().Single().Reasons.Single(), "field \"zip\" of composite type public.addr changes from varchar(10) to varchar(5)");
        Assert.IsEmpty(widened.GetDestructiveOperations());
    }

    [TestMethod]
    public void AFieldDroppedWithARetypeIsDestructive()
    {
        var batch = Generate(Build($"CREATE TYPE public.addr AS (zip varchar(5), n int);{Use}"), Build($"CREATE TYPE public.addr AS (zip varchar(10));{Use}"));

        var reasons = batch.GetDestructiveOperations().Single().Reasons;
        StringAssert.Contains(reasons.Single(), "field \"n\" of composite type");
        Assert.IsTrue(batch.Operations.Any(static operation => operation is CompositeSwapOperation));
    }

    [TestMethod]
    public void AnArrayColumnWhoseFieldsLineUpMovesThroughItsText()
    {
        const string Table = " CREATE TABLE public.t (id int, past public.addr[] DEFAULT '{}');";
        var batch = Generate(Build($"CREATE TYPE public.addr AS (zip varchar(5), n int);{Table}"), Build($"CREATE TYPE public.addr AS (zip varchar(10), n int);{Table}"));

        var swap = batch.Operations.OfType<CompositeSwapOperation>().Single().GenerateSql();
        StringAssert.Contains(swap, "ALTER COLUMN \"past\" TYPE \"public\".\"addr\"[] USING \"past\"::text::\"public\".\"addr\"[];");
        StringAssert.Contains(swap, "ALTER TABLE \"public\".\"t\" ALTER COLUMN \"past\" SET DEFAULT '{}';");
        Assert.DoesNotContain("pgproj_move", swap);
    }

    [TestMethod]
    public void AnArrayColumnWhoseFieldsDoNotLineUpMovesThroughAFunctionDroppedAfter()
    {
        const string Table = " CREATE TABLE public.t (id int, past public.addr[]);";
        var batch = Generate(Build($"CREATE TYPE public.addr AS (zip varchar(5), n int);{Table}"), Build($"CREATE TYPE public.addr AS (n int, zip varchar(10));{Table}"));

        var swap = batch.Operations.OfType<CompositeSwapOperation>().Single().GenerateSql();
        var statements = SqlStatementSplitter.Split(swap).ToArray();
        Assert.HasCount(4, statements, swap);
        StringAssert.StartsWith(statements[0], "CREATE FUNCTION \"public\".\"addr_pgproj_move\"(v \"public\".\"addr_pgproj_old\"[], c text) RETURNS \"public\".\"addr\"[]");
        StringAssert.Contains(statements[0], "ROW((v[i]).\"n\", (v[i]).\"zip\")::text::\"public\".\"addr\"");
        StringAssert.Contains(statements[1], "USING \"public\".\"addr_pgproj_move\"(\"past\", 'public.t.past');");
        Assert.AreEqual("DROP FUNCTION \"public\".\"addr_pgproj_move\"(\"public\".\"addr_pgproj_old\"[], text)", statements[2].Trim().TrimEnd(';'));
        Assert.AreEqual("DROP TYPE \"public\".\"addr_pgproj_old\"", statements[3].Trim().TrimEnd(';'));
    }

    [TestMethod]
    public void AValueThatDoesNotFitIsNamedInWords()
    {
        var batch = Generate(Build($"CREATE TYPE public.addr AS (zip varchar(10));{Use}"), Build($"CREATE TYPE public.addr AS (zip varchar(5));{Use}"));

        var swap = batch.Operations.OfType<CompositeSwapOperation>().Single().GenerateSql();
        StringAssert.Contains(swap, "EXCEPTION WHEN data_exception OR integrity_constraint_violation THEN");
        StringAssert.Contains(swap, "'the column % holds a value the composite type % cannot take after the change, so the type was not rebuilt: %', 'public.t.a', 'public.addr', SQLERRM");
    }

    [TestMethod]
    [DataRow("CREATE DOMAIN public.home AS public.addr;", "public.home")]
    [DataRow("CREATE TYPE public.pair AS (a public.addr, b int);", "public.pair")]
    [DataRow("CREATE FUNCTION public.f(x public.addr) RETURNS int LANGUAGE sql RETURN 1;", "public.f")]
    public void ATypeADomainACompositeOrARoutineUsesIsRefused(string other, string named)
    {
        var batch = Generate(Build($"CREATE TYPE public.addr AS (zip varchar(5));{Use} {other}"), Build($"CREATE TYPE public.addr AS (zip varchar(10));{Use} {other}"));

        var blocked = batch.GetUnsupportedOperations().Single().GenerateSql();
        StringAssert.Contains(blocked, named);
        StringAssert.Contains(blocked, "The composite type public.addr retypes a field while columns use it");
        Assert.IsFalse(batch.Operations.Any(static operation => operation is CompositeSwapOperation));
        Assert.IsFalse(batch.Operations.Any(static operation => operation.GenerateSql().Contains("ALTER ATTRIBUTE", StringComparison.Ordinal)), "nothing PostgreSQL would refuse is written");
    }

    [TestMethod]
    public void ATypeATypedTableIsOfAndAColumnUsesIsRefused()
    {
        const string Tables = " CREATE TABLE public.typed OF public.addr;";
        var batch = Generate(Build($"CREATE TYPE public.addr AS (zip varchar(5));{Use}{Tables}"), Build($"CREATE TYPE public.addr AS (zip varchar(10));{Use}{Tables}"));

        StringAssert.Contains(batch.GetUnsupportedOperations().Single().GenerateSql(), "a typed table is of it");
    }

    [TestMethod]
    public void ARetypeNoColumnUsesStaysInPlace()
    {
        var batch = Generate(Build("CREATE TYPE public.addr AS (zip varchar(5));"), Build("CREATE TYPE public.addr AS (zip varchar(10));"));

        Assert.AreEqual("ALTER TYPE \"public\".\"addr\" ALTER ATTRIBUTE \"zip\" TYPE varchar(10);", batch.Operations.Single().GenerateSql());
    }

    [TestMethod]
    public void AddingAndDroppingFieldsOfATypeInUseStayInPlace()
    {
        var batch = Generate(Build($"CREATE TYPE public.addr AS (zip varchar(5), n int);{Use}"), Build($"CREATE TYPE public.addr AS (zip varchar(5), extra int);{Use}"));

        Assert.AreEqual(
            "ALTER TYPE \"public\".\"addr\" DROP ATTRIBUTE \"n\";\nALTER TYPE \"public\".\"addr\" ADD ATTRIBUTE \"extra\" int;",
            batch.Operations.Single().GenerateSql().ReplaceLineEndings("\n"));
    }

    [TestMethod]
    public void TheMoveFunctionsNameFitsPostgreSqlsLimit()
    {
        var name = CompositeSwapOperation.MoveFunctionName(new string('n', 60));

        Assert.AreEqual(63, name.Length);
        StringAssert.EndsWith(name, "_pgproj_move");
    }

    private static OperationBatch Generate(DatabaseModel oldModel, DatabaseModel newModel)
        => new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
