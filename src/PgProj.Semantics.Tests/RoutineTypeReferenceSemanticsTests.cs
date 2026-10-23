using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-50: routines reference the types in their parameter list and result, and the valid special cases (pseudo-types
/// such as trigger and void, and a table or view used as a row type) must not be reported as missing types.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RoutineTypeReferenceSemanticsTests
{
    private const string Mood = "create type public.mood as enum ('happy', 'sad');";

    [TestMethod]
    [DataRow("m public.mood", "integer")]
    [DataRow("m public.mood[]", "integer")]
    [DataRow("i integer", "public.mood")]
    [DataRow("i integer", "public.mood[]")]
    [DataRow("i integer, m public.mood", "integer")]
    public void FunctionReferencesTheUserDefinedTypesInItsSignature(string parameters, string returns)
    {
        var model = BuildModel($"{Mood} create function public.f({parameters}) returns {returns} language sql as $$ select null $$;");

        Assert.IsFalse(model.HasErrors, Describe(model));
        Assert.IsTrue(
            model.Functions.Values.Single().ReferencedTypes.Any(static reference => reference.EndsWith("mood", StringComparison.OrdinalIgnoreCase)),
            "Referenced types: " + string.Join(", ", model.Functions.Values.Single().ReferencedTypes));
    }

    [TestMethod]
    public void ProcedureReferencesTheUserDefinedTypesInItsSignature()
    {
        var model = BuildModel($"{Mood} create procedure public.p(m public.mood) language sql as $$ select 1 $$;");

        Assert.IsFalse(model.HasErrors, Describe(model));
        Assert.IsTrue(model.Procedures.Values.Single().ReferencedTypes.Any(static reference => reference.EndsWith("mood", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void UnqualifiedTypeInTheFunctionsSchemaIsScopedToIt()
    {
        var model = BuildModel("create type app.mood as enum ('a'); create function app.f(m mood) returns integer language sql as $$ select 1 $$;");

        Assert.IsTrue(model.Functions.Values.Single().ReferencedTypes.Contains("app.mood", StringComparer.OrdinalIgnoreCase), string.Join(", ", model.Functions.Values.Single().ReferencedTypes));
    }

    [TestMethod]
    [DataRow("returns trigger")]
    [DataRow("returns void")]
    [DataRow("returns record")]
    [DataRow("returns setof record")]
    [DataRow("returns event_trigger")]
    [DataRow("returns internal")]
    public void PseudoTypeResultsAreNotMissingTypes(string returns)
    {
        var model = BuildModel($"create function public.f() {returns} language plpgsql as $$ begin return null; end $$;");

        Assert.IsFalse(model.HasErrors, Describe(model));
    }

    [TestMethod]
    public void PolymorphicParametersAreNotMissingTypes()
    {
        var model = BuildModel("create function public.f(a anyelement, b anyarray) returns anyelement language sql as $$ select a $$;");

        Assert.IsFalse(model.HasErrors, Describe(model));
    }

    [TestMethod]
    public void TableAndViewRowTypesAreNotMissingTypes()
    {
        var model = BuildModel("""
            create table public.orders (id bigint primary key);
            create view public.order_ids as select id from public.orders;
            create function public.a(o public.orders) returns setof public.orders language sql as $$ select o $$;
            create function public.b(v public.order_ids) returns integer language sql as $$ select 1 $$;
            """);

        Assert.IsFalse(model.HasErrors, Describe(model));
    }

    [TestMethod]
    public void BuiltInTypesRaiseNothing()
    {
        var model = BuildModel("create function public.f(a integer, b text[], c numeric, d timestamptz, e uuid, f jsonb) returns boolean language sql as $$ select true $$;");

        Assert.IsFalse(model.HasErrors, Describe(model));
    }

    [TestMethod]
    public void AnUnknownTypeInASignatureIsReportedLikeAnUnknownColumnType()
    {
        var model = BuildModel("create function public.f(a nosuchtype) returns integer language sql as $$ select 1 $$;");

        Assert.IsTrue(model.Diagnostics.Any(static diagnostic => diagnostic.Message.Contains("references missing type 'public.nosuchtype'", StringComparison.Ordinal)), Describe(model)); // scoped to the routine's schema, as a column type is (G-90 removed a pass that also re-added it unscoped)
    }

    private static string Describe(DatabaseModel model)
        => string.Join(" | ", model.Diagnostics.Select(static diagnostic => diagnostic.Message));

    private static DatabaseModel BuildModel(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
