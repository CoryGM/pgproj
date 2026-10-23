using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-253 (PostgreSQL 18): <c>NOT NULL ... NO INHERIT</c>, a child's own named NOT NULL constraint on a column it only inherits, and a child column that gets its NOT NULL from a parent without
/// repeating it. Each was lost on the way through <c>init</c> and <c>publish</c>.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class NotNullNoInheritTests
{
    [TestMethod]
    public void ANotNullNoInheritOnAColumnIsDeployedAsWritten()
    {
        var script = Script(DatabaseModel.Empty, Build("CREATE TABLE public.p (f1 integer NOT NULL NO INHERIT, f2 integer CONSTRAINT nn NOT NULL NO INHERIT);"));

        StringAssert.Contains(script, "\"f1\" integer NOT NULL NO INHERIT");
        StringAssert.Contains(script, "\"f2\" integer CONSTRAINT \"nn\" NOT NULL NO INHERIT");
    }

    [TestMethod]
    public void ATableConstraintNotNullNoInheritIsKept()
    {
        var script = Script(DatabaseModel.Empty, Build("CREATE TABLE public.p (a integer, CONSTRAINT nn NOT NULL a NO INHERIT);"));

        StringAssert.Contains(script, "\"a\" integer CONSTRAINT \"nn\" NOT NULL NO INHERIT");
    }

    [TestMethod]
    public void AChildDoesNotGetANotNullNoInheritFromItsParent()
    {
        var model = Build("""
            CREATE TABLE public.p (f1 integer NOT NULL NO INHERIT, a integer NOT NULL);
            CREATE TABLE public.c () INHERITS (public.p);
            """);

        var child = model.Tables["public.c"];
        Assert.AreNotEqual(false, child.Columns.Single(static column => column.Name == "f1").IsNullable);
        Assert.AreEqual(false, child.Columns.Single(static column => column.Name == "a").IsNullable);
    }

    [TestMethod]
    public void AChildWithTwoParentsOneOfWhichIsNoInheritIsNotNotNull()
    {
        var model = Build("""
            CREATE TABLE public.mp1 (f1 integer NOT NULL NO INHERIT);
            CREATE TABLE public.mp2 (f1 integer);
            CREATE TABLE public.mc () INHERITS (public.mp1, public.mp2);
            """);

        Assert.AreNotEqual(false, model.Tables["public.mc"].Columns.Single().IsNullable);
    }

    [TestMethod]
    public void AChildsOwnNamedNotNullOnAnInheritedColumnIsAConstraintAmongItsElements()
    {
        var script = Script(DatabaseModel.Empty, Build("""
            CREATE TABLE public.p (a integer);
            CREATE TABLE public.c (CONSTRAINT foo NOT NULL a, b integer CONSTRAINT nn NOT NULL) INHERITS (public.p);
            """));

        StringAssert.Contains(script, "CONSTRAINT \"foo\" NOT NULL \"a\"");
        StringAssert.Contains(script, "\"b\" integer CONSTRAINT \"nn\" NOT NULL");
        Assert.IsFalse(script.Contains("ALTER TABLE \"public\".\"c\"", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void AChildsOwnNotNullNoInheritOnAnInheritedColumnKeepsNoInherit()
    {
        var script = Script(DatabaseModel.Empty, Build("""
            CREATE TABLE public.p (a integer);
            CREATE TABLE public.c (CONSTRAINT foo NOT NULL a NO INHERIT) INHERITS (public.p);
            """));

        StringAssert.Contains(script, "CONSTRAINT \"foo\" NOT NULL \"a\" NO INHERIT");
    }

    [TestMethod]
    public void AChildThatDeclaresNotNullItselfHasItsOwnConstraintNotTheParents()
    {
        var model = Build("""
            CREATE TABLE public.p (partid integer CONSTRAINT p_partid_nn NOT NULL);
            CREATE TABLE public.c (partid integer NOT NULL) INHERITS (public.p);
            """);

        var column = model.Tables["public.c"].Columns.Single(static column => column.Name == "partid");
        Assert.AreEqual(false, column.IsNullable);
        Assert.IsNull(column.NotNullName);
    }

    [TestMethod]
    public void ADeclaredChildColumnDoesNotRepeatANotNullItGetsFromAParent()
    {
        var script = Script(DatabaseModel.Empty, Build("""
            CREATE TABLE public.p (f1 integer NOT NULL);
            CREATE TABLE public.c (f1 integer, a integer NOT NULL) INHERITS (public.p);
            """));

        StringAssert.Contains(script, "\"f1\" integer,");
        StringAssert.Contains(script, "\"a\" integer NOT NULL");
    }

    [TestMethod]
    public void TogglingNoInheritOnAnExistingNotNullAltersTheConstraintInPlace()
    {
        var script = Script(
            Build("CREATE TABLE public.t (a integer CONSTRAINT nn NOT NULL);"),
            Build("CREATE TABLE public.t (a integer CONSTRAINT nn NOT NULL NO INHERIT);"));

        StringAssert.Contains(script, "ALTER TABLE \"public\".\"t\" ALTER CONSTRAINT \"nn\" NO INHERIT;");
        Assert.IsFalse(script.Contains("DROP", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void AddingANotNullNoInheritToANullableColumnAddsTheConstraint()
    {
        var script = Script(
            Build("CREATE TABLE public.t (a integer);"),
            Build("CREATE TABLE public.t (a integer CONSTRAINT nn NOT NULL NO INHERIT);"));

        StringAssert.Contains(script, "ALTER TABLE \"public\".\"t\" ADD CONSTRAINT \"nn\" NOT NULL \"a\" NO INHERIT;");
    }

    [TestMethod]
    public void TheSameConstraintOnBothSidesIsNoChange()
    {
        var model = Build("""
            CREATE TABLE public.p (f1 integer NOT NULL NO INHERIT, a integer);
            CREATE TABLE public.c (CONSTRAINT foo NOT NULL a) INHERITS (public.p);
            """);

        Assert.AreEqual(string.Empty, Script(model, model));
    }

    private static string Script(DatabaseModel oldModel, DatabaseModel newModel)
    {
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));
        return string.Join("\n", batch.Operations.Where(static operation => operation is not SchemaCreateOperation).Select(static operation => operation.GenerateSql().Replace("\r\n", "\n", StringComparison.Ordinal))).Trim();
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
