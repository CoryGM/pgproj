namespace PgProj.Parser.Tests;

/// <summary>
/// G-78: <c>CREATE TYPE</c>/<c>DOMAIN</c>/<c>EXTENSION</c> are read from pg_query's AST, not regular expressions
/// over the raw SQL text - the same risk class G-65 already found and fixed for grants (a regex can match across
/// a statement boundary, inside a comment, or inside a string/dollar-quoted literal). Text that only looks like
/// one of these statements must never produce a node, and a real one right next to a lookalike must still.
/// </summary>
[TestClass]
public sealed class TypeDeclarationParsingTests
{
    [TestMethod]
    public void EnumTypeIsRead()
    {
        var node = Single<TypeNode>("CREATE TYPE public.mood AS ENUM ('sad', 'ok', 'happy');");

        Assert.AreEqual("mood", node.Name);
        Assert.AreEqual("public", node.SchemaName);
        StringAssert.Contains(node.RawStatementSql, "ENUM");
    }

    [TestMethod]
    public void CompositeTypeIsRead()
    {
        var node = Single<TypeNode>("CREATE TYPE public.point AS (x integer, y integer);");

        Assert.AreEqual("point", node.Name);
        Assert.AreEqual("public", node.SchemaName);
    }

    [TestMethod]
    public void RangeTypeIsRead()
    {
        var node = Single<TypeNode>("CREATE TYPE public.floatrange AS RANGE (subtype = float8);");

        Assert.AreEqual("floatrange", node.Name);
        Assert.AreEqual("public", node.SchemaName);
    }

    [TestMethod]
    public void AShellTypeIsReadAsABaseTypeMarkerNotAType()
    {
        // Replaces ShellTypeIsRead: a shell type was a type with no structure until the user chose not to model base types (G-241, 2026-10-13).
        var node = Single<BaseTypeNode>("CREATE TYPE shell_type;");

        Assert.AreEqual("shell_type", node.Name);
        Assert.AreEqual("public", node.SchemaName, "An unqualified name is the object in public (G-114).");
        Assert.IsTrue(node.IsShell);
        Assert.IsEmpty(node.SupportFunctions);
    }

    [TestMethod]
    public void ABaseTypeIsReadWithTheFunctionsItNamesAsWritten()
    {
        var node = Single<BaseTypeNode>("CREATE TYPE app.xint4 (INTERNALLENGTH = 4, INPUT = app.xint4in, OUTPUT = xint4out, RECEIVE = app.xint4recv, ALIGNMENT = int4, PASSEDBYVALUE);");

        Assert.AreEqual("xint4", node.Name);
        Assert.AreEqual("app", node.SchemaName);
        Assert.IsFalse(node.IsShell);
        CollectionAssert.AreEqual(
            new[] { "app.xint4in", "-.xint4out", "app.xint4recv" },
            node.SupportFunctions.Select(static function => $"{function.SchemaName ?? "-"}.{function.Name}").ToArray());
    }

    [TestMethod]
    public void UnqualifiedTypeTakesTheDefaultSchema()
    {
        var node = Single<TypeNode>("CREATE TYPE mood AS ENUM ('sad');");

        Assert.AreEqual("mood", node.Name);
        Assert.AreEqual("public", node.SchemaName, "An unqualified name is the object in public (G-114).");
    }

    [TestMethod]
    public void DomainIsRead()
    {
        var node = Single<DomainNode>("CREATE DOMAIN public.us_zip AS text CHECK (VALUE ~ '^[0-9]{5}$');");

        Assert.AreEqual("us_zip", node.Name);
        Assert.AreEqual("public", node.SchemaName);
        Assert.AreEqual("text", node.BaseType);
        Assert.IsFalse(node.IsNotNull);
        Assert.IsNull(node.RawDefaultExpression);
        var check = node.CheckConstraints.Single();
        Assert.IsNull(check.Name);
        Assert.AreEqual("VALUE ~ '^[0-9]{5}$'", check.RawExpression);
        Assert.IsNotNull(check.ExpressionAst);
    }

    /// <summary>A domain's base type carries its modifiers and array bounds exactly like a column's does (G-45, G-77).</summary>
    [TestMethod]
    public void DomainBaseTypeCarriesModifiersAndArrayBounds()
    {
        var node = Single<DomainNode>("CREATE DOMAIN public.prices AS numeric(10,2)[];");

        Assert.AreEqual("pg_catalog.numeric", node.BaseType);
        CollectionAssert.AreEqual(new[] { "10", "2" }, node.BaseTypeModifiers.ToArray());
        Assert.AreEqual(1, node.BaseTypeArrayDimensions);
    }

    [TestMethod]
    public void DomainDefaultIsRead()
    {
        var node = Single<DomainNode>("CREATE DOMAIN public.d AS integer DEFAULT 5;");

        Assert.AreEqual("5", node.RawDefaultExpression);
    }

    [TestMethod]
    public void DomainNotNullIsRead()
    {
        var node = Single<DomainNode>("CREATE DOMAIN public.d AS integer NOT NULL;");

        Assert.IsTrue(node.IsNotNull);
    }

    /// <summary>An explicit <c>NULL</c> constraint (the default, spelled out) does not set <see cref="DomainNode.IsNotNull"/>.</summary>
    [TestMethod]
    public void DomainExplicitNullIsNotNotNull()
    {
        var node = Single<DomainNode>("CREATE DOMAIN public.d AS integer NULL;");

        Assert.IsFalse(node.IsNotNull);
    }

    /// <summary>Multiple CHECK constraints, named and unnamed, are each read in declaration order.</summary>
    [TestMethod]
    public void DomainMultipleCheckConstraintsAreReadInOrder()
    {
        var node = Single<DomainNode>(
            "CREATE DOMAIN public.d AS integer DEFAULT 5 CONSTRAINT positive CHECK (VALUE > 0) CHECK (VALUE < 1000) NOT NULL;");

        Assert.AreEqual("5", node.RawDefaultExpression);
        Assert.IsTrue(node.IsNotNull);
        Assert.AreEqual(2, node.CheckConstraints.Count);
        Assert.AreEqual("positive", node.CheckConstraints[0].Name);
        Assert.AreEqual("VALUE > 0", node.CheckConstraints[0].RawExpression);
        Assert.IsNull(node.CheckConstraints[1].Name);
        Assert.AreEqual("VALUE < 1000", node.CheckConstraints[1].RawExpression);
    }

    [TestMethod]
    public void ExtensionIsRead()
    {
        var node = Single<ExtensionNode>("CREATE EXTENSION IF NOT EXISTS citext WITH SCHEMA public;");

        Assert.AreEqual("citext", node.Name);
        Assert.AreEqual("public", node.SchemaName);
    }

    [TestMethod]
    public void ExtensionWithoutAnExplicitSchemaHasNoSchema()
    {
        var node = Single<ExtensionNode>("CREATE EXTENSION citext;");

        Assert.AreEqual("citext", node.Name);
        Assert.IsNull(node.SchemaName);
    }

    /// <summary>An extension name that isn't a valid bare identifier is written quoted; pg_query already unescapes it for us.</summary>
    [TestMethod]
    public void HyphenatedExtensionNameIsRead()
    {
        var node = Single<ExtensionNode>("CREATE EXTENSION \"uuid-ossp\";");

        Assert.AreEqual("uuid-ossp", node.Name);
    }

    /// <summary>Text that merely looks like one of these statements is not one.</summary>
    [TestMethod]
    [DataRow("-- CREATE TYPE public.fake AS ENUM ('x');\nCREATE TABLE public.t (id integer);")]
    [DataRow("/* CREATE DOMAIN public.fake AS text; */ CREATE TABLE public.t (id integer);")]
    [DataRow("CREATE TABLE public.t (note text DEFAULT 'CREATE EXTENSION fake;');")]
    [DataRow("CREATE FUNCTION public.f() RETURNS void LANGUAGE plpgsql AS $$ BEGIN EXECUTE 'CREATE TYPE public.fake AS ENUM (''x'')'; END $$;")]
    [DataRow("CREATE FUNCTION public.f() RETURNS void LANGUAGE plpgsql AS $body$ BEGIN\n-- CREATE EXTENSION fake;\nEND $body$;")]
    public void LookalikesInCommentsStringsAndRoutineBodiesAreIgnored(string sql)
    {
        var tree = new PgQueryParser().Parse(sql);

        Assert.IsEmpty(tree.Types);
        Assert.IsEmpty(tree.Domains);
        Assert.IsEmpty(tree.Extensions);
    }

    [TestMethod]
    public void RealDeclarationNextToALookalikeIsStillRead()
    {
        var tree = new PgQueryParser().Parse(
            "-- CREATE TYPE public.fake AS ENUM ('nope');\n" +
            "CREATE TYPE public.mood AS ENUM ('sad', 'ok', 'happy'); " +
            "/* CREATE DOMAIN public.other_fake AS text; */");

        var type = tree.Types.Single();
        Assert.AreEqual("mood", type.Name);
        Assert.IsEmpty(tree.Domains);
    }

    /// <summary>A composite type's fields must not leak into the model as ordinary table columns (G-78).</summary>
    [TestMethod]
    public void CompositeTypeFieldsDoNotBecomeColumnNodes()
    {
        var tree = new PgQueryParser().Parse("CREATE TYPE public.point AS (x integer, y integer);");

        Assert.IsEmpty(tree.Columns);
    }

    /// <summary>A domain's CHECK constraint must not leak into the model as an ordinary table constraint (G-78).</summary>
    [TestMethod]
    public void DomainCheckConstraintDoesNotBecomeAConstraintNode()
    {
        var tree = new PgQueryParser().Parse("CREATE DOMAIN public.us_zip AS text CHECK (VALUE ~ '^[0-9]{5}$');");

        Assert.IsEmpty(tree.Constraints);
    }

    [TestMethod]
    public void CompositeFieldTypesAreReadFromTheTree()
    {
        // G-99: each field's type is read the way a column's is, so it can be compared the way a column's is.
        var type = Single<TypeNode>("CREATE TYPE s.a AS (zip varchar(10), n int, arr integer[], iv interval day to second, e s.st);");

        var fields = type.CompositeFieldTypes.ToDictionary(static field => field.Name);
        Assert.AreEqual("pg_catalog.varchar", fields["zip"].BaseType);
        CollectionAssert.AreEqual(new[] { "10" }, fields["zip"].Modifiers.ToArray());
        Assert.AreEqual("pg_catalog.int4", fields["n"].BaseType);
        Assert.AreEqual(1, fields["arr"].ArrayDimensions);
        Assert.IsNotNull(fields["iv"].IntervalFields);
        Assert.AreEqual("s.st", fields["e"].BaseType);
        CollectionAssert.AreEqual(new[] { "zip", "n", "arr", "iv", "e" }, type.CompositeFieldTypes.Select(static field => field.Name).ToArray(), "Declaration order is kept.");
    }

    [TestMethod]
    public void OnlyACompositeTypeHasFieldTypes()
        => Assert.IsEmpty(Single<TypeNode>("CREATE TYPE s.m AS ENUM ('a', 'b');").CompositeFieldTypes);

    private static T Single<T>(string sql)
        where T : SqlNode
    {
        var tree = new PgQueryParser().Parse(sql);
        return tree.Nodes.OfType<T>().Single();
    }
}
