using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-68: a procedure's referenced types were never resolved, so a built-in type written with its short name (<c>int2</c>, <c>float8</c>,
/// <c>bool</c>) stayed qualified with the procedure's own schema (<c>s.int2</c>) while the same types on a function, or read from the live
/// catalog, came out as built-ins. A procedure now resolves its types as a function does.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ProcedureTypeResolutionTests
{
    [TestMethod]
    [DataRow("int2")]
    [DataRow("int4")]
    [DataRow("int8")]
    [DataRow("float4")]
    [DataRow("float8")]
    [DataRow("bool")]
    [DataRow("timestamptz")]
    [DataRow("varchar")]
    [DataRow("text")]
    [DataRow("smallint")]
    [DataRow("double precision")]
    [DataRow("timestamp with time zone")]
    public void BuiltInTypesAreNeverQualifiedWithTheProceduresSchema(string type)
    {
        var procedure = Build($"create procedure s.p(x {type}) language sql as $$ select 1 $$;").Procedures.Values.Single();

        Assert.IsFalse(procedure.ReferencedTypes.Any(static reference => reference.StartsWith("s.", StringComparison.OrdinalIgnoreCase)), string.Join(", ", procedure.ReferencedTypes));
    }

    /// <summary>The same parameter types give the same references on a procedure as on a function.</summary>
    [TestMethod]
    public void ProcedureAndFunctionReferenceTheSameTypes()
    {
        const string parameters = "x int2, y float8, z int4, w int8, v timestamptz, u bool, t varchar";

        var function = Build($"create function s.f({parameters}) returns int4 language sql as $$ select 1 $$;").Functions.Values.Single();
        var procedure = Build($"create procedure s.p({parameters}) language sql as $$ select 1 $$;").Procedures.Values.Single();

        CollectionAssert.AreEquivalent(function.ReferencedTypes.Where(static type => type != "int4").ToArray(), procedure.ReferencedTypes.Where(static type => type != "int4").ToArray());
    }

    /// <summary>What the live catalog reports (long names) and what a project writes (short names) are one set of references.</summary>
    [TestMethod]
    public void ShortAndLongSpellingsGiveTheSameReferences()
    {
        var shortNames = Build("create procedure s.p(x int2, y float8, z bool) language sql as $$ select 1 $$;").Procedures.Values.Single();
        var longNames = Build("create procedure s.p(x smallint, y double precision, z boolean) language sql as $$ select 1 $$;").Procedures.Values.Single();

        CollectionAssert.AreEquivalent(longNames.ReferencedTypes.ToArray(), shortNames.ReferencedTypes.ToArray());
    }

    [TestMethod]
    public void UserDefinedTypeIsStillResolvedAndKeepsItsSchema()
    {
        var model = Build("create schema s; create type s.mood as enum ('a', 'b'); create procedure s.p(x s.mood, n int4) language sql as $$ select 1 $$;");

        var procedure = model.Procedures.Values.Single();
        CollectionAssert.Contains(procedure.ReferencedTypes.ToArray(), "s.mood");
        Assert.IsTrue(procedure.ResolvedTypes.Any(static type => type.Name == "mood"));
    }

    [TestMethod]
    public void UserDefinedTypeInAnotherSchemaKeepsThatSchemaNotTheProceduresOwn()
    {
        var model = Build("create schema a; create schema b; create type a.mood as enum ('x'); create procedure b.p(x a.mood) language sql as $$ select 1 $$;");

        CollectionAssert.Contains(model.Procedures.Values.Single().ReferencedTypes.ToArray(), "a.mood");
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
