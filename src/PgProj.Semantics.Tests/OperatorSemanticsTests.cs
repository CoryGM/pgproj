using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-129: an operator is a model object of its own, keyed by schema, name, and operand types in comparison form; the function, the estimators, and the
/// user-defined operand types are dependencies; and a commutator or negator that PostgreSQL links both ways is linked both ways in the model.
/// </summary>
[TestClass]
public sealed class OperatorSemanticsTests
{
    private const string Setup = "CREATE TYPE public.mood AS ENUM ('a', 'b'); CREATE FUNCTION public.mood_eq(public.mood, public.mood) RETURNS boolean LANGUAGE sql AS $$ SELECT $1::text = $2::text $$; "
        + "CREATE FUNCTION public.mood_ne(public.mood, public.mood) RETURNS boolean LANGUAGE sql AS $$ SELECT $1::text <> $2::text $$; ";

    [TestMethod]
    public void AnOperatorIsInTheModelKeyedByItsCanonicalOperands()
    {
        // Act
        var model = Build(Setup + "CREATE OPERATOR public.=== (LEFTARG = public.mood, RIGHTARG = public.mood, FUNCTION = public.mood_eq, RESTRICT = eqsel);");

        // Assert
        var definition = model.Operators["public.===(mood,mood)"];
        Assert.AreEqual("===", definition.OperatorName);
        Assert.AreEqual("public.mood_eq", definition.FunctionName);
        Assert.AreEqual("eqsel", definition.RestrictFunction);
        CollectionAssert.AreEquivalent(new[] { "public.mood_eq", "eqsel" }, definition.ReferencedFunctions.ToArray());
        CollectionAssert.AreEqual(new[] { "public.mood", "public.mood" }, definition.ReferencedTypes.ToArray());
        Assert.AreEqual(0, model.Diagnostics.Count, string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
    }

    [TestMethod]
    [DataRow("integer", "pg_catalog.int4")]
    [DataRow("public.mood", "mood")]
    [DataRow("varchar(10)", "character varying")]
    public void TheSameOperandTypeWrittenTwoWaysIsOneKey(string first, string second)
    {
        Assert.AreEqual(OperatorNames.CreateKey("public", "+++", first, "text"), OperatorNames.CreateKey("public", "+++", second, "text"));
    }

    [TestMethod]
    public void AMissingOperandIsPartOfTheKey()
    {
        Assert.AreNotEqual(OperatorNames.CreateKey("public", "+++", null, "text"), OperatorNames.CreateKey("public", "+++", "text", null));
    }

    [TestMethod]
    public void OperatorsWithTheSameNameAndDifferentOperandsAreDifferentOperators()
    {
        // Act
        var model = Build(Setup + "CREATE OPERATOR public.=== (LEFTARG = public.mood, RIGHTARG = public.mood, FUNCTION = public.mood_eq); CREATE OPERATOR public.=== (LEFTARG = public.mood, RIGHTARG = text, FUNCTION = public.mood_eq);");

        // Assert
        Assert.AreEqual(2, model.Operators.Count);
    }

    [TestMethod]
    public void TwoOperatorsWithTheSameNameAndOperandsAreAnError()
    {
        // Act
        var model = Build(Setup + "CREATE OPERATOR public.=== (LEFTARG = mood, RIGHTARG = mood, FUNCTION = public.mood_eq); CREATE OPERATOR === (LEFTARG = public.mood, RIGHTARG = public.mood, FUNCTION = public.mood_ne);");

        // Assert
        Assert.AreEqual(1, model.Diagnostics.Count(static d => d.Severity == SemanticDiagnosticSeverity.Error && d.Message.Contains("Duplicate Operator", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ANegatorNamedOnOneSideIsLinkedOnBoth()
    {
        // Act: the second names its partner; the first names none, and PostgreSQL would still show the link on it.
        var model = Build(Setup
            + "CREATE OPERATOR public.=== (LEFTARG = public.mood, RIGHTARG = public.mood, FUNCTION = public.mood_eq); "
            + "CREATE OPERATOR public.!== (LEFTARG = public.mood, RIGHTARG = public.mood, FUNCTION = public.mood_ne, NEGATOR = OPERATOR(public.===));");

        // Assert
        Assert.AreEqual("public.!==", model.Operators["public.===(mood,mood)"].Negator);
        Assert.AreEqual("public.===", model.Operators["public.!==(mood,mood)"].Negator);
        Assert.IsNull(model.Operators["public.===(mood,mood)"].Commutator);
    }

    [TestMethod]
    public void AnUnqualifiedPartnerIsFoundThroughTheSearchPath()
    {
        // Act
        var model = Build(Setup
            + "CREATE OPERATOR public.=== (LEFTARG = public.mood, RIGHTARG = public.mood, FUNCTION = public.mood_eq); "
            + "CREATE OPERATOR public.!== (LEFTARG = public.mood, RIGHTARG = public.mood, FUNCTION = public.mood_ne, NEGATOR = ===);");

        // Assert
        Assert.AreEqual("public.!==", model.Operators["public.===(mood,mood)"].Negator);
    }

    [TestMethod]
    public void ACommutatorIsTheOperatorWithTheOperandsSwapped()
    {
        // Act
        var model = Build(Setup
            + "CREATE OPERATOR public.<<< (LEFTARG = public.mood, RIGHTARG = text, FUNCTION = public.mood_eq); "
            + "CREATE OPERATOR public.>>> (LEFTARG = text, RIGHTARG = public.mood, FUNCTION = public.mood_eq, COMMUTATOR = OPERATOR(public.<<<));");

        // Assert
        Assert.AreEqual("public.>>>", model.Operators["public.<<<(mood,text)"].Commutator);
    }

    [TestMethod]
    public void AnOperatorIsAModelNodeSoTheGraphSeesIt()
    {
        // Act
        var model = Build(Setup + "CREATE OPERATOR public.=== (LEFTARG = public.mood, RIGHTARG = public.mood, FUNCTION = public.mood_eq);");

        // Assert
        Assert.IsTrue(model.GetAllNodes().Any(static node => node is OperatorDefinition));
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
