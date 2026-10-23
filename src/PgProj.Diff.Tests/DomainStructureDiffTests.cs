using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-77: a domain's base type, DEFAULT, NOT NULL, and CHECK constraints must be diffed, not silently ignored - the
/// pre-G-77 model captured only the domain's name and kind.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class DomainStructureDiffTests
{
    [TestMethod]
    public void SelfDiffIsEmpty()
    {
        var model = BuildModel("create domain public.d as numeric(10,2) default 0 constraint positive check (value > 0) not null;");

        Assert.IsEmpty(new DiffEngine().Execute(model, model).ModifiedTypes);
    }

    [TestMethod]
    [DataRow(
        "create domain public.d as numeric(10,2) check (value > 0);",
        "create domain public.d as NUMERIC( 10 , 2 ) CHECK ( VALUE > 0 );")]
    [DataRow(
        "create domain public.d as integer default 1;",
        "create domain public.d as pg_catalog.int4 default 1;")]
    [DataRow(
        "create domain public.d as text constraint c1 check (value ~ 'a') constraint c2 check (value ~ 'b');",
        "create domain public.d as text constraint c2 check (value ~ 'b') constraint c1 check (value ~ 'a');")]
    public void CosmeticOnlyDifferencesProduceNoChange(string oldSql, string newSql)
    {
        var result = new DiffEngine().Execute(BuildModel(oldSql), BuildModel(newSql));

        Assert.IsEmpty(result.ModifiedTypes, Describe(result));
    }

    /// <summary>
    /// Reordering two unnamed CHECK constraints swaps which one PostgreSQL's default name numbers
    /// <c>_check</c> vs <c>_check1</c>; that must not look like a change (G-77).
    /// </summary>
    [TestMethod]
    public void ReorderingUnnamedCheckConstraintsProducesNoChange()
    {
        var result = new DiffEngine().Execute(
            BuildModel("create domain public.d as integer check (value > 0) check (value < 100);"),
            BuildModel("create domain public.d as integer check (value < 100) check (value > 0);"));

        Assert.IsEmpty(result.ModifiedTypes, Describe(result));
    }

    [TestMethod]
    public void BaseTypeModifierChangeIsReported()
    {
        var result = new DiffEngine().Execute(
            BuildModel("create domain public.d as varchar(10);"),
            BuildModel("create domain public.d as varchar(20);"));

        Assert.HasCount(1, result.ModifiedTypes);
        Assert.IsTrue(result.ModifiedTypes[0].Details.Any(detail => detail.PropertyName == nameof(TypeDefinition.DomainBaseTypeModifiers)));
    }

    [TestMethod]
    public void DefaultChangeIsReported()
    {
        var result = new DiffEngine().Execute(
            BuildModel("create domain public.d as integer default 1;"),
            BuildModel("create domain public.d as integer default 2;"));

        Assert.HasCount(1, result.ModifiedTypes);
        Assert.IsTrue(result.ModifiedTypes[0].Details.Any(detail => detail.PropertyName == nameof(TypeDefinition.DomainRawDefaultExpression)));
    }

    [TestMethod]
    public void NotNullChangeIsReported()
    {
        var result = new DiffEngine().Execute(
            BuildModel("create domain public.d as integer;"),
            BuildModel("create domain public.d as integer not null;"));

        Assert.HasCount(1, result.ModifiedTypes);
        Assert.IsTrue(result.ModifiedTypes[0].Details.Any(detail => detail.PropertyName == nameof(TypeDefinition.DomainIsNotNull)));
    }

    [TestMethod]
    public void CheckConstraintExpressionChangeIsReported()
    {
        var result = new DiffEngine().Execute(
            BuildModel("create domain public.d as integer check (value > 0);"),
            BuildModel("create domain public.d as integer check (value > 10);"));

        Assert.HasCount(1, result.ModifiedTypes);
        Assert.IsTrue(result.ModifiedTypes[0].Details.Any(detail => detail.PropertyName == nameof(TypeDefinition.DomainCheckConstraints)));
    }

    [TestMethod]
    public void AddingACheckConstraintIsReported()
    {
        var result = new DiffEngine().Execute(
            BuildModel("create domain public.d as integer check (value > 0);"),
            BuildModel("create domain public.d as integer check (value > 0) check (value < 100);"));

        Assert.HasCount(1, result.ModifiedTypes);
        Assert.IsTrue(result.ModifiedTypes[0].Details.Any(detail => detail.PropertyName == nameof(TypeDefinition.DomainCheckConstraints)));
    }

    // G-98: the live side reads an unnamed CHECK back with PostgreSQL's default name written out, as below.
    [TestMethod]
    [DataRow(
        "create domain public.pi as integer constraint pi_check check ((value > 0));",
        "create domain public.pi as integer check (value > 0);")]
    [DataRow(
        "create domain public.pn as numeric(10,2) constraint pn_check check ((value > (0)::numeric)) constraint pn_check1 check ((value < (1000)::numeric));",
        "create domain public.pn as numeric(10,2) check (value > 0) check (value < 1000);")]
    [DataRow(
        "create domain public.pn as numeric(10,2) constraint pn_check1 check ((value > (0)::numeric)) constraint pn_check check ((value < (1000)::numeric));",
        "create domain public.pn as numeric(10,2) check (value > 0) check (value < 1000);")]
    [DataRow(
        "create domain public.\"Mixed\" as integer constraint \"Mixed_check\" check ((value > 0));",
        "create domain public.\"Mixed\" as integer check (value > 0);")]
    public void AnUnnamedCheckReadBackWithItsDefaultNameIsNoChange(string live, string project)
    {
        var result = new DiffEngine().Execute(BuildModel(live), BuildModel(project));

        Assert.IsEmpty(result.ModifiedTypes, Describe(result));
    }

    [TestMethod]
    public void ACheckWithANameOfItsOwnIsStillComparedByName()
    {
        var result = new DiffEngine().Execute(
            BuildModel("create domain public.pi as integer constraint positive check ((value > 0));"),
            BuildModel("create domain public.pi as integer check (value > 0);"));

        Assert.HasCount(1, result.ModifiedTypes, "A live CHECK named 'positive' is not the project's unnamed one.");
    }

    [TestMethod]
    public void AChangedUnnamedCheckIsStillReported()
    {
        var result = new DiffEngine().Execute(
            BuildModel("create domain public.pi as integer constraint pi_check check ((value > 0));"),
            BuildModel("create domain public.pi as integer check (value > 1);"));

        Assert.HasCount(1, result.ModifiedTypes);
    }

    [TestMethod]
    [DataRow("pi_check", true)]
    [DataRow("pi_check1", true)]
    [DataRow("PI_CHECK12", true)]
    [DataRow("pi_checker", false)]
    [DataRow("pi_check_x", false)]
    [DataRow("other_check", false)]
    [DataRow("positive", false)]
    public void APositionalNameIsTheDomainsDefaultFormOnly(string name, bool expected)
        => Assert.AreEqual(expected, new DomainCheckConstraintDefinition(name, "VALUE > 0").HasPositionalName("pi"));

    [TestMethod]
    public void AGeneratedOrMissingNameIsPositional()
    {
        Assert.IsTrue(new DomainCheckConstraintDefinition("anything", "VALUE > 0") { IsNameGenerated = true }.HasPositionalName("pi"));
        Assert.IsTrue(new DomainCheckConstraintDefinition(null, "VALUE > 0").HasPositionalName("pi"));
    }

    private static string Describe(DiffResult result)
        => string.Join(Environment.NewLine, result.ModifiedTypes.SelectMany(change =>
            change.Details.Select(detail => $"{change.NodeId}.{detail.PropertyName}: '{detail.OldValue}' -> '{detail.NewValue}'")));

    private static DatabaseModel BuildModel(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
