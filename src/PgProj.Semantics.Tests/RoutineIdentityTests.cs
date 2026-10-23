using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-67: a routine's identity was built from its parameter types as written, so <c>f(integer)</c> and <c>f(int4)</c> were two routines
/// (one removed, one added) although PostgreSQL has one, and a project using the short spellings would have been dropped and recreated
/// on every publish. The key uses the canonical types now; different overloads stay different.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RoutineIdentityTests
{
    [TestMethod]
    [DataRow("integer", "int4")]
    [DataRow("integer", "pg_catalog.int4")]
    [DataRow("bigint", "int8")]
    [DataRow("smallint", "int2")]
    [DataRow("integer[]", "int4[]")]
    [DataRow("timestamp with time zone", "timestamptz")]
    [DataRow("timestamp without time zone", "timestamp")]
    [DataRow("boolean", "bool")]
    [DataRow("character varying", "varchar")]
    [DataRow("double precision", "float8")]
    [DataRow("decimal", "numeric")]
    public void EquivalentSpellingsOfAParameterTypeAreOneRoutine(string one, string other)
    {
        Assert.AreEqual(KeyOf($"create function public.f(x {one}) returns integer language sql as $$ select 1 $$;"),
            KeyOf($"create function public.f(x {other}) returns integer language sql as $$ select 1 $$;"));
    }

    [TestMethod]
    public void KeyIsReadableAndUsesTheShortCanonicalTypes()
    {
        Assert.AreEqual("public.f(int4,text,int8[])", KeyOf("create function public.f(a integer, b text, c bigint[]) returns integer language sql as $$ select 1 $$;"));
    }

    [TestMethod]
    public void ModifiersDoNotMakeADifferentRoutineAndCommasInThemDoNotSplitTheList()
    {
        Assert.AreEqual(
            KeyOf("create function public.f(a numeric(10,2), b varchar(5)) returns integer language sql as $$ select 1 $$;"),
            KeyOf("create function public.f(a numeric, b character varying) returns integer language sql as $$ select 1 $$;"));
        Assert.AreEqual("public.f(numeric,varchar)", KeyOf("create function public.f(a numeric(10,2), b varchar(5)) returns integer language sql as $$ select 1 $$;"));
    }

    [TestMethod]
    [DataRow("integer", "bigint")]
    [DataRow("integer", "integer[]")]
    [DataRow("text", "varchar")]
    [DataRow("timestamp", "timestamptz")]
    [DataRow("integer", "text")]
    public void DifferentTypesAreDifferentOverloads(string one, string other)
    {
        Assert.AreNotEqual(KeyOf($"create function public.f(x {one}) returns integer language sql as $$ select 1 $$;"),
            KeyOf($"create function public.f(x {other}) returns integer language sql as $$ select 1 $$;"));
    }

    [TestMethod]
    public void OutParametersAndParameterNamesAreNotPartOfTheIdentity()
    {
        Assert.AreEqual(
            KeyOf("create function public.f(a integer, out b text) language sql as $$ select 1 $$;"),
            KeyOf("create function public.f(z int4) returns integer language sql as $$ select 1 $$;"));
    }

    [TestMethod]
    public void TwoDeclarationsOfTheSameRoutineInDifferentSpellingsAreADuplicate()
    {
        var model = Build(
            "create function public.f(x integer) returns integer language sql as $$ select 1 $$;"
            + "create function public.f(x int4) returns integer language sql as $$ select 2 $$;");

        Assert.IsTrue(model.Diagnostics.Any(static diagnostic => diagnostic.Message.Contains("Duplicate Function definition", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ProceduresFollowTheSameRule()
    {
        Assert.AreEqual(
            Build("create procedure public.p(x integer) language sql as $$ select 1 $$;").Procedures.Keys.Single(),
            Build("create procedure public.p(x int4) language sql as $$ select 1 $$;").Procedures.Keys.Single());
    }

    /// <summary>A comment names the routine in whatever spelling it likes; it must reach the routine that is declared in another.</summary>
    [TestMethod]
    public void CommentOnARoutineFindsItWhateverTheSpelling()
    {
        var model = Build(
            "create function public.f(x integer) returns integer language sql as $$ select 1 $$;"
            + "create function public.f(x text) returns integer language sql as $$ select 1 $$;"
            + "comment on function public.f(int4) is 'the integer one';");

        Assert.AreEqual("the integer one", model.Functions["public.f(int4)"].Comment);
        Assert.IsNull(model.Functions["public.f(text)"].Comment);
    }

    /// <summary>A call from another routine resolves to the declared routine whatever type spelling either uses.</summary>
    [TestMethod]
    public void GrantOnARoutineFindsItWhateverTheSpelling()
    {
        var model = Build(
            "create function public.f(x integer) returns integer language sql as $$ select 1 $$;"
            + "grant execute on function public.f(int4) to r;");

        var grant = model.Grants.Values.Single();
        Assert.AreEqual(GrantExpansion.RoutineObjectName(model.Functions.Values.Single()), grant.ObjectName);
    }

    /// <summary>The declared spelling is kept on the routine: the SQL generated for it must not change.</summary>
    [TestMethod]
    public void TheSignatureAsWrittenIsKept()
    {
        var function = Build("create function public.f(x int4) returns integer language sql as $$ select 1 $$;").Functions.Values.Single();

        Assert.AreEqual("int4", function.Signature!.RenderArgumentTypeSignature());
    }

    private static string KeyOf(string sql) => Build(sql).Functions.Keys.Single();

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
