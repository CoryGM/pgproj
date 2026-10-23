using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-93: an unqualified name is found along the project's search path (<c>public</c> unless <c>pgproj.json</c> sets <c>searchPath</c>), as
/// PostgreSQL finds it. <c>build</c> reports, as an error, a reference that names a project object only outside the path, since PostgreSQL
/// rejects it when the object is created. With a path, an unqualified declaration is in its first schema, a reference to an existing object
/// is found along it, and a type or trigger function the path finds outside <c>public</c> takes that schema.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SearchPathTests
{
    private const string Shop = """
        CREATE SCHEMA shop;
        CREATE TABLE shop.customer (id bigint PRIMARY KEY);
        CREATE FUNCTION shop.order_total(p bigint) RETURNS numeric LANGUAGE sql AS $$ SELECT 1::numeric $$;
        CREATE TYPE shop.mood AS ENUM ('ok');
        """;

    [TestMethod]
    [DataRow("CREATE VIEW shop.v AS SELECT c.id, order_total(c.id) FROM shop.customer c;", "View 'shop.v'", "function 'order_total'")]
    [DataRow("CREATE VIEW shop.v AS SELECT id FROM customer;", "View 'shop.v'", "relation 'customer'")]
    [DataRow("CREATE TABLE shop.t (id int, m mood);", "Column 'shop.t.m'", "type 'mood'")]
    [DataRow("CREATE TABLE shop.t (id numeric DEFAULT order_total(1));", "Column 'shop.t.id'", "function 'order_total'")]
    [DataRow("CREATE TABLE shop.t (id bigint CHECK (order_total(id) > 0));", "Constraint", "function 'order_total'")]
    [DataRow("CREATE TABLE shop.t (id bigint); CREATE INDEX t_idx ON shop.t ((order_total(id)));", "Index 'shop.t_idx'", "function 'order_total'")]
    [DataRow("CREATE FUNCTION shop.f() RETURNS bigint LANGUAGE sql AS $$ SELECT count(*) FROM customer $$;", "Function 'shop.f'", "relation 'customer'")]
    [DataRow("CREATE FUNCTION shop.g(m mood) RETURNS int LANGUAGE plpgsql AS $$ BEGIN RETURN 1; END $$;", "Function 'shop.g'", "type 'mood'")]
    [DataRow("CREATE DOMAIN shop.d AS mood;", "Domain 'shop.d'", "type 'mood'")]
    [DataRow("CREATE TYPE shop.pair AS (a mood, b int);", "Type 'shop.pair'", "type 'mood'")]
    [DataRow("CREATE FUNCTION shop.tf() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$; CREATE TRIGGER trg BEFORE INSERT ON shop.customer FOR EACH ROW EXECUTE FUNCTION tf();", "Trigger 'trg'", "function 'tf'")]
    public void AReferenceFoundOnlyOffThePathIsAnError(string sql, string owner, string reference)
    {
        var error = Build(Shop + sql).Diagnostics.Single(static diagnostic => diagnostic.Source == "SearchPathValidationPass");

        Assert.AreEqual(SemanticDiagnosticSeverity.Error, error.Severity);
        StringAssert.StartsWith(error.Message, owner);
        StringAssert.Contains(error.Message, reference + " without a schema");
        StringAssert.Contains(error.Message, "search path (public)");
        StringAssert.Contains(error.Message, "schema 'shop'");
        StringAssert.Contains(error.Message, "add shop to \"searchPath\"");
    }

    [TestMethod]
    [DataRow("CREATE VIEW shop.v AS SELECT c.id, shop.order_total(c.id) FROM shop.customer c;")]
    [DataRow("CREATE TABLE public.p (id int); CREATE VIEW shop.v AS SELECT id FROM p;")]
    [DataRow("CREATE VIEW shop.v AS SELECT now(), count(*) FROM shop.customer;")]
    [DataRow("CREATE FUNCTION shop.h() RETURNS bigint LANGUAGE plpgsql AS $$ BEGIN RETURN (SELECT count(*) FROM customer); END $$;")]
    [DataRow("CREATE TABLE shop.t (id uuid DEFAULT uuid_generate_v4());")]
    public void AReferencePostgreSqlFindsIsNotReported(string sql)
        => Assert.IsFalse(Build(Shop + sql).Diagnostics.Any(static diagnostic => diagnostic.Source == "SearchPathValidationPass"),
            string.Join(" | ", Build(Shop + sql).Diagnostics.Select(static diagnostic => diagnostic.Message)));

    [TestMethod]
    public void WithTheSchemaOnThePathTheSameReferencesAreFine()
    {
        var model = Build(Shop + """
            CREATE VIEW shop.v AS SELECT c.id, order_total(c.id) FROM customer c;
            CREATE TABLE shop.t (id int, m mood);
            """, "shop", "public");

        Assert.IsFalse(model.Diagnostics.Any(static diagnostic => diagnostic.Source == "SearchPathValidationPass"),
            string.Join(" | ", model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    [TestMethod]
    public void AnUnqualifiedDeclarationIsInTheFirstSchemaAndAReferenceIsFoundAlongThePath()
    {
        var model = Build("""
            CREATE SCHEMA shop;
            CREATE TABLE public.region (id int PRIMARY KEY, name text);
            CREATE TABLE customer (id bigint PRIMARY KEY, region_id int REFERENCES region (id));
            CREATE INDEX region_name_idx ON region (name);
            CREATE INDEX customer_region_idx ON customer (region_id);
            ALTER TABLE region ADD COLUMN note text;
            COMMENT ON TABLE region IS 'regions';
            """, "shop", "public");

        Assert.IsTrue(model.Tables.ContainsKey("shop.customer"));
        Assert.IsTrue(model.Indexes.ContainsKey("public.region_name_idx"), string.Join(", ", model.Indexes.Keys));
        Assert.IsTrue(model.Indexes.ContainsKey("shop.customer_region_idx"), string.Join(", ", model.Indexes.Keys));
        var foreignKey = model.Constraints.Values.Single(static constraint => constraint.ForeignKeyReferencedTable is not null);
        Assert.AreEqual("public", foreignKey.ForeignKeyReferencedSchemaName);
        CollectionAssert.Contains(model.Tables["public.region"].Columns.Select(static column => column.Name).ToList(), "note");
        Assert.AreEqual("regions", model.Tables["public.region"].Comment);
        Assert.IsEmpty(model.Diagnostics, string.Join(" | ", model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    [TestMethod]
    public void ATypeOrTriggerFunctionFoundOutsidePublicTakesItsSchema()
    {
        var model = Build("""
            CREATE SCHEMA shop;
            CREATE TYPE mood AS ENUM ('ok');
            CREATE TABLE t (id int, m mood, tags mood[]);
            CREATE FUNCTION f(m mood) RETURNS text LANGUAGE sql AS $$ SELECT m::text $$;
            CREATE FUNCTION tf() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$;
            CREATE TRIGGER trg BEFORE INSERT ON t FOR EACH ROW EXECUTE FUNCTION tf();
            """, "shop", "public");

        var columns = model.Tables["shop.t"].Columns.ToDictionary(static column => column.Name);
        Assert.AreEqual("shop.mood", columns["m"].DataType);
        Assert.AreEqual("shop.mood", columns["tags"].RawType, "Array bounds are kept apart from the type name.");
        Assert.IsTrue(model.Functions.ContainsKey("shop.f(shop.mood)"), string.Join(", ", model.Functions.Keys));
        Assert.AreEqual("shop.tf", model.Triggers.Values.Single().FunctionName);
    }

    [TestMethod]
    public void WithTheDefaultPathNothingIsRequalified()
    {
        var model = Build("CREATE TYPE mood AS ENUM ('ok'); CREATE TABLE t (m mood); CREATE FUNCTION f(m mood) RETURNS int LANGUAGE sql AS $$ SELECT 1 $$;");

        Assert.AreEqual("mood", model.Tables["public.t"].Columns.Single().DataType);
        Assert.IsTrue(model.Functions.ContainsKey("public.f(mood)"), string.Join(", ", model.Functions.Keys));
    }

    [TestMethod]
    public void AnUnqualifiedCallBindsToTheFunctionThePathFindsFirst()
    {
        var model = Build("""
            CREATE SCHEMA shop;
            CREATE FUNCTION public.label() RETURNS text LANGUAGE sql AS $$ SELECT 'public' $$;
            CREATE FUNCTION shop.label() RETURNS text LANGUAGE sql AS $$ SELECT 'shop' $$;
            CREATE VIEW shop.v AS SELECT label() AS l;
            """);

        Assert.AreEqual("public.label()", FunctionReferences.Resolve(model, "shop", "label")?.Key, "PostgreSQL finds public.label() on the default path, not the view's own schema.");
    }

    private static DatabaseModel Build(string sql, params string[] searchPath)
    {
        var parser = searchPath.Length > 0 ? new PgQueryParser(searchPath[0]) : new PgQueryParser();
        return new SemanticPipeline().Execute([parser.Parse(sql)], searchPath: searchPath.Length > 0 ? searchPath : null);
    }
}
