using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-136: a typed table is created <c>OF</c> its type, after it, listing only the columns it gives something; a field changed on the type is written with <c>CASCADE</c> when a typed
/// table is of it, and the table itself is left to what a table can change (a default, NOT NULL), never a column added, dropped, or retyped, which the type carries; a table that
/// becomes typed, or changes its type, is converted with <c>OF</c> and <c>NOT OF</c>.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class TypedTableDeployTests
{
    private const string Type = "CREATE TYPE public.r AS (k integer, v text); ";

    [TestMethod]
    public void ATypedTableIsCreatedOfItsTypeAfterItListingOnlyWhatItGivesItsColumns()
    {
        var statements = Statements(DatabaseModel.Empty, Build("CREATE TABLE public.t OF public.r (k WITH OPTIONS PRIMARY KEY, v WITH OPTIONS DEFAULT 'x' NOT NULL); " + Type));

        var typeAt = statements.FindIndex(static s => s.StartsWith("CREATE TYPE", StringComparison.Ordinal));
        var tableAt = statements.FindIndex(static s => s.StartsWith("CREATE TABLE \"public\".\"t\"", StringComparison.Ordinal));
        Assert.IsTrue(typeAt >= 0 && tableAt > typeAt, string.Join("\n---\n", statements));
        StringAssert.StartsWith(statements[tableAt], "CREATE TABLE \"public\".\"t\" OF \"public\".\"r\" (\n    \"k\" WITH OPTIONS NOT NULL,\n    \"v\" WITH OPTIONS NOT NULL DEFAULT 'x'\n);");
    }

    [TestMethod]
    public void ATypedTableWithNothingToListHasNoParentheses()
    {
        var statements = Statements(DatabaseModel.Empty, Build(Type + "CREATE TABLE public.t OF public.r;"));

        StringAssert.StartsWith(statements.Single(static s => s.StartsWith("CREATE TABLE", StringComparison.Ordinal)), "CREATE TABLE \"public\".\"t\" OF \"public\".\"r\";");
    }

    [TestMethod]
    public void AFieldAddedDroppedOrRetypedOnTheTypeIsWrittenWithCascadeWhenATypedTableIsOfIt()
    {
        var script = string.Join("\n", Sql(Generate(
            Build(Type + "CREATE TABLE public.t OF public.r;"),
            Build("CREATE TYPE public.r AS (k bigint, extra text); CREATE TABLE public.t OF public.r;"))));

        StringAssert.Contains(script, "ALTER TYPE \"public\".\"r\" DROP ATTRIBUTE \"v\" CASCADE;");
        StringAssert.Contains(script, "ALTER TYPE \"public\".\"r\" ALTER ATTRIBUTE \"k\" TYPE bigint CASCADE;");
        StringAssert.Contains(script, "ALTER TYPE \"public\".\"r\" ADD ATTRIBUTE \"extra\" text CASCADE;");
    }

    [TestMethod]
    public void WithNoTypedTableThereIsNoCascade()
    {
        var script = string.Join("\n", Sql(Generate(
            Build(Type + "CREATE TABLE public.t (a public.r);"),
            Build("CREATE TYPE public.r AS (k integer, v text, extra text); CREATE TABLE public.t (a public.r);"))));

        StringAssert.Contains(script, "ADD ATTRIBUTE \"extra\" text;");
        Assert.IsFalse(script.Contains("CASCADE", StringComparison.Ordinal));
    }

    [TestMethod]
    public void TheTypedTableIsNotGivenTheColumnItsTypeCarriesButItsDefaultIsSet()
    {
        var script = string.Join("\n", Sql(Generate(
            Build(Type + "CREATE TABLE public.t OF public.r;"),
            Build("CREATE TYPE public.r AS (k integer, v text, extra text); CREATE TABLE public.t OF public.r (extra WITH OPTIONS DEFAULT 'none' NOT NULL);"))));

        Assert.IsFalse(script.Contains("ADD COLUMN", StringComparison.Ordinal), script);
        Assert.IsFalse(script.Contains("DROP COLUMN", StringComparison.Ordinal), script);
        StringAssert.Contains(script, "ALTER TABLE \"public\".\"t\" ALTER COLUMN \"extra\" SET DEFAULT 'none'");
        StringAssert.Contains(script, "ALTER TABLE \"public\".\"t\" ALTER COLUMN \"extra\" SET NOT NULL");
    }

    [TestMethod]
    public void ATypedTablesDefaultChangesInPlace()
    {
        var script = string.Join("\n", Sql(Generate(
            Build(Type + "CREATE TABLE public.t OF public.r (v WITH OPTIONS DEFAULT 'x');"),
            Build(Type + "CREATE TABLE public.t OF public.r (v WITH OPTIONS DEFAULT 'y');"))));

        StringAssert.Contains(script, "ALTER TABLE \"public\".\"t\" ALTER COLUMN \"v\" SET DEFAULT 'y'");
    }

    [TestMethod]
    public void AFieldDroppedFromTheTypeIsTheTypesDestructiveChangeNotTheTablesColumn()
    {
        var batch = Generate(
            Build(Type + "CREATE TABLE public.t OF public.r;"),
            Build("CREATE TYPE public.r AS (k integer); CREATE TABLE public.t OF public.r;"));

        var reasons = batch.GetDestructiveOperations().SelectMany(static d => d.Reasons).ToArray();
        Assert.IsTrue(reasons.Any(static reason => reason.Contains("\"v\"", StringComparison.Ordinal)), string.Join("; ", reasons));
        Assert.IsFalse(reasons.Any(static reason => reason.StartsWith("column", StringComparison.Ordinal)), string.Join("; ", reasons));
    }

    [TestMethod]
    public void ATableThatBecomesTypedOrChangesItsTypeIsConvertedWithOfAndNotOf()
    {
        var types = "CREATE TYPE public.r AS (k integer, v text); CREATE TYPE public.r2 AS (k integer, v text); ";
        var becomes = string.Join("\n", Sql(Generate(Build(types + "CREATE TABLE public.t (k integer, v text);"), Build(types + "CREATE TABLE public.t OF public.r;"))));
        var changes = string.Join("\n", Sql(Generate(Build(types + "CREATE TABLE public.t OF public.r;"), Build(types + "CREATE TABLE public.t OF public.r2;"))));
        var stops = string.Join("\n", Sql(Generate(Build(types + "CREATE TABLE public.t OF public.r;"), Build(types + "CREATE TABLE public.t (k integer, v text);"))));

        StringAssert.Contains(becomes, "ALTER TABLE \"public\".\"t\" OF \"public\".\"r\";");
        Assert.IsFalse(becomes.Contains("NOT OF", StringComparison.Ordinal));
        StringAssert.Contains(changes, "ALTER TABLE \"public\".\"t\" NOT OF;");
        StringAssert.Contains(changes, "ALTER TABLE \"public\".\"t\" OF \"public\".\"r2\";");
        StringAssert.Contains(stops, "ALTER TABLE \"public\".\"t\" NOT OF;");
    }

    [TestMethod]
    public void TheSameTypedTableWrittenTwoWaysDeploysNothing()
    {
        Assert.IsEmpty(Statements(
            Build(Type + "CREATE TABLE public.t OF public.r (v WITH OPTIONS DEFAULT 'x' NOT NULL);"),
            Build(Type + "CREATE TABLE t OF r (v WITH OPTIONS NOT NULL DEFAULT 'x');")));
    }

    private static OperationBatch Generate(DatabaseModel oldModel, DatabaseModel newModel)
        => new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));

    private static List<string> Sql(OperationBatch batch)
        => batch.Operations
            .Where(static operation => operation is not SchemaCreateOperation)
            .Select(static operation => operation.GenerateSql().Trim().Replace("\r\n", "\n", StringComparison.Ordinal))
            .ToList();

    private static List<string> Statements(DatabaseModel oldModel, DatabaseModel newModel) => Sql(Generate(oldModel, newModel));

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
