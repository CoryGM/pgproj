using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-267: a base type's options in their comparison form (the user's design of 2026-11-09, item 5): PostgreSQL's default filled in for each one not stated (checked on
/// PostgreSQL 14 and 18), a function named <c>schema.name</c> as PostgreSQL resolves it, a type by its comparison signature, <c>LIKE</c>'s four options left for the
/// server unless stated beside it; the statement as written kept for deployment; and no dependency cycle between the type and the functions it names.
/// </summary>
[TestClass]
public sealed class BaseTypeSemanticsTests
{
    private const string Functions = """
        CREATE SCHEMA app;
        CREATE FUNCTION app.t_in(cstring) RETURNS app.t LANGUAGE internal IMMUTABLE STRICT AS $$int4in$$;
        CREATE FUNCTION app.t_out(app.t) RETURNS cstring LANGUAGE internal IMMUTABLE STRICT AS $$int4out$$;

        """;

    [TestMethod]
    public void AnUnstatedOptionHasPostgreSqlsDefault()
    {
        var options = Options("CREATE TYPE app.t (INPUT = app.t_in, OUTPUT = app.t_out);");

        Assert.AreEqual(-1, options.InternalLength);
        Assert.IsFalse(options.PassedByValue);
        Assert.AreEqual("i", options.Alignment);
        Assert.AreEqual("p", options.Storage);
        Assert.AreEqual("U", options.Category);
        Assert.IsFalse(options.Preferred);
        Assert.AreEqual(",", options.Delimiter);
        Assert.IsFalse(options.Collatable);
        Assert.IsNull(options.Default);
        Assert.IsNull(options.Element);
        CollectionAssert.AreEquivalent(new[] { "INPUT", "OUTPUT" }, options.Functions.Keys.ToArray());
    }

    [TestMethod]
    public void EachStatedOptionIsRead()
    {
        var options = Options("CREATE TYPE app.t (INPUT = app.t_in, OUTPUT = app.t_out, INTERNALLENGTH = 8, PASSEDBYVALUE, ALIGNMENT = double, STORAGE = main, CATEGORY = 'N', PREFERRED = true, DEFAULT = 'a''b', ELEMENT = integer, DELIMITER = ';', COLLATABLE = true);");

        Assert.AreEqual(8, options.InternalLength);
        Assert.IsTrue(options.PassedByValue);
        Assert.AreEqual("d", options.Alignment);
        Assert.AreEqual("m", options.Storage);
        Assert.AreEqual("N", options.Category);
        Assert.IsTrue(options.Preferred);
        Assert.AreEqual("a'b", options.Default);
        Assert.AreEqual(Options("CREATE TYPE app.t (INPUT = app.t_in, OUTPUT = app.t_out, ELEMENT = int4);").Element, options.Element, "integer and int4 are one type.");
        Assert.AreEqual(";", options.Delimiter);
        Assert.IsTrue(options.Collatable);
        Assert.AreEqual("pg_catalog.raw_array_subscript_handler", options.Functions["SUBSCRIPT"], "An ELEMENT without a SUBSCRIPT gets PostgreSQL's handler.");
    }

    [TestMethod]
    public void AnUnqualifiedFunctionIsTheProjectsInTheTypesSchemaOrPostgreSqls()
    {
        var options = Options("CREATE TYPE app.t (INPUT = t_in, OUTPUT = t_out, ANALYZE = some_builtin);");

        Assert.AreEqual("app.t_in", options.Functions["INPUT"]);
        Assert.AreEqual("app.t_out", options.Functions["OUTPUT"]);
        Assert.AreEqual("pg_catalog.some_builtin", options.Functions["ANALYZE"]);
        Assert.AreEqual("t_in", options.WrittenFunctions["INPUT"], "As written, for ALTER TYPE ... SET.");
    }

    [TestMethod]
    public void LikeLeavesItsFourOptionsToTheServerUnlessStatedBesideIt()
    {
        var options = Options("CREATE TYPE app.t (INPUT = t_in, OUTPUT = t_out, LIKE = pg_catalog.int8, ALIGNMENT = int4);");

        Assert.IsNotNull(options.Like);
        Assert.AreEqual("pg_catalog.int8", options.WrittenLike);
        Assert.IsNull(options.InternalLength);
        Assert.IsNull(options.PassedByValue);
        Assert.IsNull(options.Storage);
        Assert.AreEqual("i", options.Alignment, "Stated beside LIKE, it stands.");
    }

    [TestMethod]
    public void TheStatementIsKeptAsWrittenAndTheTypeIsCreatedAfterItsFunctionsWithoutACycle()
    {
        const string type = "CREATE TYPE app.t (INPUT = t_in, OUTPUT = t_out)";
        var model = Build(Functions + type + ";");
        var definition = model.Types.Values.Single();

        Assert.AreEqual(type, definition.BaseTypeWrittenText);
        Assert.AreEqual("base", definition.TypeKind);
        CollectionAssert.IsSubsetOf(new[] { "app.t_in", "app.t_out" }, definition.ReferencedFunctions.ToArray());
        Assert.IsFalse(model.Diagnostics.Any(static diagnostic => diagnostic.Message.Contains("cycle", StringComparison.OrdinalIgnoreCase)), string.Join(" | ", model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    [TestMethod]
    public void AFunctionThatUsesTheTypeButIsNotOneItNamesIsStillACycleCandidateAndOrderedAfterIt()
    {
        // Only the type's own functions lose their edge to it: any other function that takes the type is created after it.
        var model = Build(Functions + "CREATE FUNCTION app.other(app.t) RETURNS integer LANGUAGE internal AS $$int4out$$; CREATE TYPE app.t (INPUT = t_in, OUTPUT = t_out);");
        var other = model.Functions.Values.Single(static function => function.Name == "other");

        Assert.IsFalse(BaseTypeSupport.IsShellEdge(model, other, "app.t"));
        Assert.IsTrue(BaseTypeSupport.IsShellEdge(model, model.Functions.Values.Single(static function => function.Name == "t_in"), "app.t"));
    }

    private BaseTypeOptionsDefinition Options(string type) => Build(Functions + type).Types.Values.Single().BaseTypeOptions!;

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
