using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-146: an owner the project states is a model object of its own, one per object and keyed by the object (never by the role), so a stated owner and a
/// live owner are one node that differs. It finds its object in whichever file declares it, a statement about an object the project does not declare is a
/// warning, and a project that leaves owners out (<c>excludeOwners</c>) has none.
/// </summary>
[TestClass]
public sealed class OwnerSemanticsTests
{
    private const string Objects = """
        CREATE SCHEMA app;
        CREATE TABLE app.t (id int PRIMARY KEY);
        CREATE VIEW app.v AS SELECT id FROM app.t;
        CREATE MATERIALIZED VIEW app.mv AS SELECT id FROM app.t;
        CREATE SEQUENCE app.s;
        CREATE FUNCTION app.f(a integer) RETURNS integer LANGUAGE sql AS $$ SELECT a $$;
        CREATE PROCEDURE app.p() LANGUAGE sql AS $$ SELECT 1 $$;
        CREATE TYPE app.mood AS ENUM ('a', 'b');
        CREATE DOMAIN app.d AS integer;
        CREATE AGGREGATE app.agg(integer) (SFUNC = int4pl, STYPE = integer);
        CREATE OPERATOR app.#+# (LEFTARG = integer, RIGHTARG = integer, FUNCTION = int4pl);
        CREATE FUNCTION app.evt() RETURNS event_trigger LANGUAGE plpgsql AS $$ BEGIN NULL; END $$;
        CREATE EVENT TRIGGER audit ON ddl_command_end EXECUTE FUNCTION app.evt();
        CREATE PUBLICATION pub FOR TABLE app.t;
        """;

    [TestMethod]
    public void AnOwnerIsFoundForEveryKindOfObject()
    {
        // Act
        var model = Build(Objects + """

            ALTER SCHEMA app OWNER TO lc_owner;
            ALTER TABLE app.t OWNER TO lc_owner;
            ALTER VIEW app.v OWNER TO lc_owner;
            ALTER MATERIALIZED VIEW app.mv OWNER TO lc_owner;
            ALTER SEQUENCE app.s OWNER TO lc_owner;
            ALTER FUNCTION app.f(int) OWNER TO lc_owner;
            ALTER PROCEDURE app.p() OWNER TO lc_owner;
            ALTER TYPE app.mood OWNER TO lc_owner;
            ALTER DOMAIN app.d OWNER TO lc_owner;
            ALTER AGGREGATE app.agg(int) OWNER TO lc_owner;
            ALTER OPERATOR app.#+# (int, int) OWNER TO lc_owner;
            ALTER EVENT TRIGGER audit OWNER TO lc_owner;
            ALTER PUBLICATION pub OWNER TO lc_owner;
            """);

        // Assert: every one resolved to its object, with the right kind; nothing was warned about.
        var kinds = model.Owners.Values.Select(static owner => owner.ObjectKind).Order(StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(
            new[] { "aggregate", "domain", "event trigger", "function", "materialized view", "operator", "procedure", "publication", "schema", "sequence", "table", "type", "view" },
            kinds);
        Assert.IsTrue(model.Owners.Values.All(static owner => owner.OwnerRole == "lc_owner"));
        Assert.AreEqual(0, model.Diagnostics.Count, string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
    }

    [TestMethod]
    public void AnOwnerIsKeyedByTheObjectNotTheRoleAndPointsAtItsDefinition()
    {
        // Act
        var model = Build(Objects + "\nALTER TABLE app.t OWNER TO lc_owner;");
        var owner = model.Owners.Values.Single();

        // Assert: two models that differ only in the role have one key, which is what lets the diff see a change instead of an add and a remove.
        Assert.AreEqual(OwnerKeys.Create("table", "app.t"), owner.Key);
        Assert.AreEqual("owner:table:app.t", owner.Key);
        Assert.AreEqual("app.t", owner.ObjectKey);
        Assert.AreEqual("t", owner.ObjectName);
        Assert.AreEqual("app", owner.SchemaName);
        // The target is the object's definition as declared (later passes replace definitions, so not the same instance); it supplies names and signatures.
        Assert.IsInstanceOfType<TableDefinition>(owner.Target);
        Assert.AreEqual("app.t", owner.Target!.Key);
        Assert.AreEqual("Owner", owner.Kind);
    }

    [TestMethod]
    public void AKeyThatAlreadyStartsWithItsKindIsNotGivenItTwice()
    {
        // Act
        var model = Build(Objects + "\nALTER EVENT TRIGGER audit OWNER TO lc_owner; ALTER PUBLICATION pub OWNER TO lc_owner;");

        // Assert
        CollectionAssert.Contains(model.Owners.Keys.ToArray(), "owner:event trigger:audit");
        CollectionAssert.Contains(model.Owners.Keys.ToArray(), "owner:publication:pub");
    }

    [TestMethod]
    public void AnOwnerMayBeInADifferentFileFromItsObjectInEitherOrder()
    {
        // Arrange
        var parser = new PgQueryParser();
        var owners = parser.Parse("ALTER TABLE app.t OWNER TO lc_owner;");
        var declaration = parser.Parse("CREATE SCHEMA app; CREATE TABLE app.t (id int);");

        // Act
        var ownersFirst = new SemanticPipeline().Execute([owners, declaration]);
        var ownersLast = new SemanticPipeline().Execute([declaration, owners]);

        // Assert
        Assert.AreEqual("lc_owner", ownersFirst.Owners["owner:table:app.t"].OwnerRole);
        Assert.AreEqual("lc_owner", ownersLast.Owners["owner:table:app.t"].OwnerRole);
        Assert.AreEqual(0, ownersFirst.Diagnostics.Count + ownersLast.Diagnostics.Count);
    }

    [TestMethod]
    public void AnOwnerForAnObjectTheProjectDoesNotDeclareIsAWarningAndIsIgnored()
    {
        // Act
        var model = Build("CREATE SCHEMA app; CREATE TABLE app.t (id int); ALTER TABLE app.nope OWNER TO lc_owner;");

        // Assert
        Assert.IsEmpty(model.Owners);
        var warning = model.Diagnostics.Single();
        Assert.AreEqual(SemanticDiagnosticSeverity.Warning, warning.Severity);
        StringAssert.Contains(warning.Message, "ALTER TABLE app.nope OWNER TO lc_owner");
        StringAssert.Contains(warning.Message, "does not declare it");
    }

    [TestMethod]
    public void TheSameObjectOwnedTwiceByDifferentRolesIsOneOwnerWithAWarning()
    {
        // Act
        var model = Build("CREATE SCHEMA app; CREATE TABLE app.t (id int); ALTER TABLE app.t OWNER TO first; ALTER TABLE app.t OWNER TO second;");

        // Assert: PostgreSQL would apply them in order, so the last is the owner.
        Assert.AreEqual("second", model.Owners.Values.Single().OwnerRole);
        Assert.AreEqual(1, model.Diagnostics.Count(static d => d.Message.Contains("stated twice", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void TheSameOwnerStatedTwiceIsNotAWarning()
    {
        // Act
        var model = Build("CREATE SCHEMA app; CREATE TABLE app.t (id int); ALTER TABLE app.t OWNER TO same; ALTER TABLE app.t OWNER TO SAME;");

        // Assert
        Assert.AreEqual(0, model.Diagnostics.Count);
    }

    [TestMethod]
    public void AnOverloadIsFoundByItsArgumentTypesInAnyOfTheirSpellings()
    {
        // Arrange
        const string Overloads = """
            CREATE SCHEMA app;
            CREATE FUNCTION app.f(a integer) RETURNS integer LANGUAGE sql AS $$ SELECT a $$;
            CREATE FUNCTION app.f(a text) RETURNS text LANGUAGE sql AS $$ SELECT a $$;
            """;

        // Act
        var model = Build(Overloads + "ALTER FUNCTION app.f(pg_catalog.int4) OWNER TO ints; ALTER FUNCTION app.f(text) OWNER TO texts;");

        // Assert
        Assert.AreEqual(2, model.Owners.Count);
        Assert.AreEqual("ints", model.Owners.Values.Single(static owner => ((FunctionDefinition)owner.Target!).Signature!.RenderArgumentTypeSignature()!.Contains("int", StringComparison.OrdinalIgnoreCase)).OwnerRole);
        Assert.AreEqual("texts", model.Owners.Values.Single(static owner => ((FunctionDefinition)owner.Target!).Signature!.RenderArgumentTypeSignature()!.Contains("text", StringComparison.OrdinalIgnoreCase)).OwnerRole);
        Assert.AreEqual(0, model.Diagnostics.Count, string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
    }

    [TestMethod]
    public void AnOverloadedRoutineWrittenWithoutArgumentsIsAmbiguousButASingleOneIsNot()
    {
        // Arrange
        const string Overloads = """
            CREATE SCHEMA app;
            CREATE FUNCTION app.f(a integer) RETURNS integer LANGUAGE sql AS $$ SELECT a $$;
            CREATE FUNCTION app.f(a text) RETURNS text LANGUAGE sql AS $$ SELECT a $$;
            CREATE FUNCTION app.only(a integer) RETURNS integer LANGUAGE sql AS $$ SELECT a $$;
            """;

        // Act
        var model = Build(Overloads + "ALTER FUNCTION app.f OWNER TO x; ALTER FUNCTION app.only OWNER TO x;");

        // Assert
        Assert.AreEqual(1, model.Owners.Count);
        Assert.AreEqual("only", model.Owners.Values.Single().ObjectName);
        Assert.AreEqual(1, model.Diagnostics.Count(static d => d.Message.Contains("does not declare it", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void AnExtensionIsNotAnObjectWhoseOwnerCanBeStated()
    {
        // Act
        var model = Build("CREATE EXTENSION citext; ALTER TYPE public.citext OWNER TO lc_owner;");

        // Assert
        Assert.IsEmpty(model.Owners);
    }

    [TestMethod]
    public void AProjectThatLeavesOwnersOutHasNoneAndNoWarnings()
    {
        // Act
        var model = new SemanticPipeline().Execute(
            [new PgQueryParser().Parse("CREATE SCHEMA app; CREATE TABLE app.t (id int); ALTER TABLE app.t OWNER TO lc_owner; ALTER TABLE app.nope OWNER TO lc_owner;")],
            excludeOwners: true);

        // Assert
        Assert.IsEmpty(model.Owners);
        Assert.AreEqual(0, model.Diagnostics.Count);
    }

    [TestMethod]
    public void AnUnqualifiedNameIsFoundAlongTheSearchPath()
    {
        // Act
        var model = new SemanticPipeline().Execute(
            [new PgQueryParser().Parse("CREATE SCHEMA app; CREATE TABLE app.t (id int); ALTER TABLE t OWNER TO lc_owner;")],
            searchPath: ["app", "public"]);

        // Assert
        Assert.AreEqual("owner:table:app.t", model.Owners.Keys.Single());
        Assert.AreEqual(0, model.Diagnostics.Count, string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
    }

    [TestMethod]
    public void AnOwnerIsAnodeWithNoReferencesOfItsOwn()
    {
        // Act
        var owner = Build(Objects + "\nALTER TABLE app.t OWNER TO lc_owner;").Owners.Values.Single();

        // Assert: the dependency on its object is made from ObjectKey (DependencyAnalyzer), not from a reference list.
        Assert.IsEmpty(owner.ReferencedTables);
        Assert.IsEmpty(owner.ReferencedTypes);
        Assert.IsEmpty(owner.ReferencedFunctions);
        Assert.IsTrue(model_Contains(Build(Objects + "\nALTER TABLE app.t OWNER TO lc_owner;"), owner.Key));
    }

    private static bool model_Contains(DatabaseModel model, string key)
        => model.GetAllNodes().Any(node => node.Key == key);

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
