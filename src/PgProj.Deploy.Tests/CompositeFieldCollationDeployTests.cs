using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-192: a composite field's <c>COLLATE</c> is written after its type, with the collation's name quoted as written (<c>"C"</c>, not <c>"c"</c>, which does not exist), and a
/// changed collation is applied in place with <c>ALTER ATTRIBUTE ... TYPE ... COLLATE</c>. A collation the project creates is created before the type.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class CompositeFieldCollationDeployTests
{
    [TestMethod]
    public void TheCollationIsWrittenAsWritten()
    {
        var statements = Generate(Build(string.Empty), Build("""create type pt as (x float8, label text collate "C", code varchar(10) COLLATE pg_catalog."POSIX");"""));

        Assert.AreEqual("CREATE TYPE \"public\".\"pt\" AS (\n    \"x\" float8,\n    \"label\" text COLLATE \"C\",\n    \"code\" varchar(10) COLLATE \"pg_catalog\".\"POSIX\"\n);", statements.Single());
    }

    [TestMethod]
    public void AChangedCollationIsAppliedInPlace()
    {
        var statements = Generate(Build("""create type pt as (label text collate "C");"""), Build("""create type pt as (label text collate "POSIX");"""));

        Assert.AreEqual("ALTER TYPE \"public\".\"pt\" ALTER ATTRIBUTE \"label\" TYPE text COLLATE \"POSIX\";", statements.Single());
    }

    [TestMethod]
    public void AnAddedCollationIsAppliedInPlace()
    {
        var statements = Generate(Build("create type pt as (label text);"), Build("""create type pt as (label text collate "C");"""));

        Assert.AreEqual("ALTER TYPE \"public\".\"pt\" ALTER ATTRIBUTE \"label\" TYPE text COLLATE \"C\";", statements.Single());
    }

    [TestMethod]
    public void AProjectCollationIsCreatedBeforeTheType()
    {
        var statements = Generate(Build(string.Empty), Build("create type pt as (label text collate ci); create collation ci (provider = icu, locale = 'und-u-ks-level2', deterministic = false);"));

        Assert.HasCount(2, statements, string.Join(Environment.NewLine, statements));
        StringAssert.StartsWith(statements[0], "CREATE COLLATION");
        StringAssert.StartsWith(statements[1], "CREATE TYPE");
    }

    private static List<string> Generate(DatabaseModel oldModel, DatabaseModel newModel)
        => new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel)).Operations
            .Where(static operation => operation is not SchemaCreateOperation)
            .Select(static operation => operation.GenerateSql().Trim())
            .ToList();

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute(string.IsNullOrEmpty(sql) ? [] : [new PgQueryParser().Parse(sql)]);
}
