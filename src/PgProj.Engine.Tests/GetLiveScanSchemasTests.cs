using PgProj.Core;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Engine.Tests;

/// <summary>
/// G-85: <see cref="LiveComparison.GetLiveScanSchemas"/> computes what <c>diff</c>/<c>publish</c> scope their live
/// database read to. Empty <c>schemas</c> means no restriction at all (unchanged from before this gap). A
/// populated <c>schemas</c> list is always widened to include every schema the project's own parsed SQL actually
/// declares an object in, even one the user forgot to list, so a project's own objects can never become invisible
/// to its own diff by omission. Moved from the CLI's tests with the forward they called (G-320).
/// </summary>
[TestClass]
public sealed class GetLiveScanSchemasTests
{
    [TestMethod]
    public void EmptyProjectSchemasReturnsNoRestrictionRegardlessOfTheModel()
    {
        var project = CreateProject([]);
        var model = Build("CREATE TABLE app.orders (id integer PRIMARY KEY);");

        var scopeSchemas = LiveComparison.GetLiveScanSchemas(project, model);

        Assert.IsEmpty(scopeSchemas);
    }

    [TestMethod]
    public void PopulatedProjectSchemasWithNoOtherModelSchemasReturnsExactlyThatList()
    {
        var project = CreateProject(["app"]);
        var model = Build("CREATE TABLE app.orders (id integer PRIMARY KEY);");

        var scopeSchemas = LiveComparison.GetLiveScanSchemas(project, model);

        CollectionAssert.AreEquivalent(new[] { "app" }, scopeSchemas.ToArray());
    }

    [TestMethod]
    public void AProjectSchemaTheProjectItselfDeclaresObjectsInIsAddedEvenWhenNotListed()
    {
        // The user only listed "app" but the project's own SQL also has a table in "public" - the safety net
        // must include it so this project's own table is never invisible to its own diff.
        var project = CreateProject(["app"]);
        var model = Build("CREATE TABLE public.orders (id integer PRIMARY KEY); CREATE TABLE app.widgets (id integer PRIMARY KEY);");

        var scopeSchemas = LiveComparison.GetLiveScanSchemas(project, model);

        CollectionAssert.AreEquivalent(new[] { "app", "public" }, scopeSchemas.ToArray());
    }

    [TestMethod]
    public void WideningCoversEveryObjectKindNotJustTables()
    {
        var project = CreateProject(["app"]);
        var model = Build(
            """
            CREATE TABLE app.orders (id integer PRIMARY KEY);
            CREATE VIEW reporting.order_view AS SELECT id FROM app.orders;
            CREATE FUNCTION helpers.bump(x integer) RETURNS integer LANGUAGE sql AS $$ SELECT x + 1 $$;
            CREATE SEQUENCE counters.order_seq;
            """);

        var scopeSchemas = LiveComparison.GetLiveScanSchemas(project, model);

        CollectionAssert.AreEquivalent(new[] { "app", "reporting", "helpers", "counters" }, scopeSchemas.ToArray());
    }

    [TestMethod]
    public void TheSameSchemaIsNotAddedTwice_AndOneDifferingByCaseIsAnother()
    {
        // A loaded project holds its schema names as PostgreSQL reads them (G-206): "App" folds to app unless quoted, and a quoted "App" is
        // another schema than app.
        var model = Build("CREATE TABLE app.orders (id integer PRIMARY KEY);");

        Assert.HasCount(1, LiveComparison.GetLiveScanSchemas(CreateProject(["app"]), model));
        Assert.HasCount(2, LiveComparison.GetLiveScanSchemas(CreateProject(["App"]), model));
    }

    private static PgProject CreateProject(IReadOnlyList<string> schemas)
        => new("test", "1.0", schemas, "./sql", null, null, []);

    private static DatabaseModel Build(string sql)
        => new SemanticBuilder().Build([new PgQueryParser().Parse(sql)]);
}
