using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-111: any change to a composite type's fields was deployed as <c>DROP TYPE</c> + <c>CREATE TYPE</c>, which PostgreSQL refuses
/// (<c>2BP01</c>) while anything uses the type. A field change is now deployed in place: <c>DROP ATTRIBUTE</c> (destructive, G-35),
/// then <c>ALTER ATTRIBUTE ... TYPE</c>, then <c>ADD ATTRIBUTE</c>.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class CompositeTypeInPlaceChangeTests
{
    private const string Address = "create schema s; create type s.addr as (street text, zip varchar(10));";

    [TestMethod]
    public void AnAddedFieldIsAddedInPlace()
    {
        var (statements, batch) = Changed(Address, "create schema s; create type s.addr as (street text, zip varchar(10), country text);");

        Assert.HasCount(1, statements, Dump(statements));
        Assert.AreEqual("ALTER TYPE \"s\".\"addr\" ADD ATTRIBUTE \"country\" text;", statements[0]);
        Assert.IsEmpty(batch.GetDestructiveOperations());
    }

    [TestMethod]
    public void AFieldAddedInTheMiddleIsStillAddedInPlace()
    {
        var (statements, _) = Changed(Address, "create schema s; create type s.addr as (street text, country text, zip varchar(10));");

        Assert.HasCount(1, statements, Dump(statements));
        Assert.AreEqual("ALTER TYPE \"s\".\"addr\" ADD ATTRIBUTE \"country\" text;", statements[0]);
    }

    [TestMethod]
    public void ARemovedFieldIsDroppedInPlaceAndIsDestructive()
    {
        var (statements, batch) = Changed(Address, "create schema s; create type s.addr as (street text);");

        Assert.HasCount(1, statements, Dump(statements));
        Assert.AreEqual("ALTER TYPE \"s\".\"addr\" DROP ATTRIBUTE \"zip\";", statements[0]);
        var destructive = batch.GetDestructiveOperations().Single();
        CollectionAssert.AreEqual(
            new[] { "field \"zip\" of composite type \"s\".\"addr\" dropped; the values it holds in stored rows are lost" },
            destructive.Reasons.ToArray());
    }

    [TestMethod]
    public void ARetypedFieldIsRetypedInPlaceAndIsNotDestructive()
    {
        var (statements, batch) = Changed(Address, "create schema s; create type s.addr as (street text, zip varchar(20));");

        Assert.HasCount(1, statements, Dump(statements));
        Assert.AreEqual("ALTER TYPE \"s\".\"addr\" ALTER ATTRIBUTE \"zip\" TYPE varchar(20);", statements[0]);
        Assert.IsEmpty(batch.GetDestructiveOperations());
    }

    [TestMethod]
    public void DropsComeBeforeRetypesAndRetypesBeforeAdds()
    {
        var (statements, batch) = Changed(
            "create schema s; create type s.p as (a int, b int, c int);",
            "create schema s; create type s.p as (d text, b bigint, c int);");

        Assert.HasCount(1, statements, Dump(statements));
        Assert.AreEqual(
            "ALTER TYPE \"s\".\"p\" DROP ATTRIBUTE \"a\";\nALTER TYPE \"s\".\"p\" ALTER ATTRIBUTE \"b\" TYPE bigint;\nALTER TYPE \"s\".\"p\" ADD ATTRIBUTE \"d\" text;",
            statements[0]);
        Assert.HasCount(1, batch.GetDestructiveOperations().Single().Reasons);
    }

    [TestMethod]
    public void ARenamedFieldIsADropAndAnAddAndIsDestructive()
    {
        var (statements, batch) = Changed(Address, "create schema s; create type s.addr as (line1 text, zip varchar(10));");

        Assert.AreEqual("ALTER TYPE \"s\".\"addr\" DROP ATTRIBUTE \"street\";\nALTER TYPE \"s\".\"addr\" ADD ATTRIBUTE \"line1\" text;", statements.Single());
        Assert.HasCount(1, batch.GetDestructiveOperations());
    }

    [TestMethod]
    public void EveryFieldDroppedLeavesTheTypeInPlace()
    {
        var (statements, _) = Changed(Address, "create schema s; create type s.addr as ();");

        Assert.AreEqual("ALTER TYPE \"s\".\"addr\" DROP ATTRIBUTE \"street\";\nALTER TYPE \"s\".\"addr\" DROP ATTRIBUTE \"zip\";", statements.Single());
    }

    [TestMethod]
    public void AQuotedFieldNameAndTypeAreQuoted()
    {
        var (statements, _) = Changed(
            "create schema s; create type s.\"Mood\" as enum ('ok'); create type s.p as (\"Id\" int);",
            "create schema s; create type s.\"Mood\" as enum ('ok'); create type s.p as (\"Id\" bigint, \"Feeling\" s.\"Mood\");");

        Assert.AreEqual(
            "ALTER TYPE \"s\".\"p\" ALTER ATTRIBUTE \"Id\" TYPE bigint;\nALTER TYPE \"s\".\"p\" ADD ATTRIBUTE \"Feeling\" s.\"Mood\";",
            statements.Single());
    }

    [TestMethod]
    public void AFieldOfATypeCreatedInTheSamePublishIsAddedAfterThatType()
    {
        var (statements, _) = Changed(
            "create schema s; create type s.p as (x int);",
            "create schema s; create type s.mood as enum ('ok'); create type s.p as (x int, m s.mood);");

        Assert.HasCount(2, statements, Dump(statements));
        StringAssert.StartsWith(statements[0], "CREATE TYPE \"s\".\"mood\" AS ENUM");
        Assert.AreEqual("ALTER TYPE \"s\".\"p\" ADD ATTRIBUTE \"m\" s.mood;", statements[1]);
    }

    [TestMethod]
    public void ACompositeTypeThatBecomesAnEnumIsStillDroppedAndRecreated()
    {
        var (statements, _) = Changed(Address, "create schema s; create type s.addr as enum ('a');");

        Assert.IsTrue(statements.Any(static statement => statement.StartsWith("DROP TYPE", StringComparison.Ordinal)), Dump(statements));
        Assert.IsTrue(statements.Any(static statement => statement.StartsWith("CREATE TYPE", StringComparison.Ordinal)), Dump(statements));
    }

    [TestMethod]
    [DataRow("create schema s; create type s.addr as (street text, zip varchar(10));")]
    [DataRow("create schema s; create type s.addr as (zip character varying(10), street text);")]
    [DataRow("create schema s; create type s.addr as (STREET TEXT, ZIP VARCHAR(10));")]
    public void ACosmeticallyDifferentOrReorderedTypeIsNoChange(string newSql)
    {
        var (statements, _) = Changed(Address, newSql);

        Assert.IsEmpty(statements, Dump(statements));
    }

    private static (List<string> Statements, OperationBatch Batch) Changed(string oldSql, string newSql)
    {
        var oldModel = Build(oldSql);
        var newModel = Build(newSql);
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));
        var statements = batch.Operations
            .Where(static operation => operation is not SchemaCreateOperation)
            .Select(static operation => operation.GenerateSql().Trim())
            .ToList();
        return (statements, batch);
    }

    private static string Dump(IEnumerable<string> statements) => string.Join(Environment.NewLine, statements);

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
