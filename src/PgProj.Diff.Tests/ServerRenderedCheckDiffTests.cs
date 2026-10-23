using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-172: when PostgreSQL has rendered both sides of a <c>CHECK</c> constraint (<see cref="ConstraintDefinition.ServerRenderedCheck"/>),
/// the diff compares those renderings, so an enum literal PostgreSQL types by its column (<c>'new'::s.st</c>), an <c>IN</c> list it
/// stores as <c>= ANY (ARRAY[...])</c>, and a domain column it casts to its base type are no change, and a real change still is.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ServerRenderedCheckDiffTests
{
    private const string Prelude = "CREATE SCHEMA s; CREATE TYPE s.st AS ENUM ('new', 'cancelled'); CREATE TABLE s.o (id int, status s.st);";

    [TestMethod]
    public void SameRenderingIsNoChangeEvenWhenTheWrittenChecksDiffer()
    {
        // The live form types the literal and uses ANY; the project's is written with IN. Neither the expression nor the derived lists may differ.
        var live = WithRendering(Build("status = ANY (ARRAY['new'::s.st, 'cancelled'::s.st])"), "(status = ANY (ARRAY['new'::s.st, 'cancelled'::s.st]))");
        var project = WithRendering(Build("status IN ('new', 'cancelled')"), "(status = ANY (ARRAY['new'::s.st, 'cancelled'::s.st]))");

        var result = new DiffEngine().Execute(live, project);

        Assert.IsEmpty(result.ModifiedConstraints, string.Join("; ", result.ModifiedConstraints.SelectMany(static change => change.Details.Select(static detail => detail.PropertyName))));
    }

    [TestMethod]
    public void DifferentRenderingIsAChange()
    {
        var live = WithRendering(Build("status = 'new'::s.st"), "(status = 'new'::s.st)");
        var project = WithRendering(Build("status = 'cancelled'"), "(status = 'cancelled'::s.st)");

        var result = new DiffEngine().Execute(live, project);

        Assert.HasCount(1, result.ModifiedConstraints);
        var detail = result.ModifiedConstraints[0].Details.Single(static detail => detail.PropertyName == nameof(ConstraintDefinition.CheckExpressionNode));
        Assert.AreEqual("(status = 'cancelled'::s.st)", detail.NewValue);
    }

    [TestMethod]
    public void WithoutARenderingOnBothSidesTheParsedCheckIsComparedAsBefore()
    {
        var live = Build("id > 0");
        var project = WithRendering(Build("id > 0"), "(id > 0)");

        Assert.IsEmpty(new DiffEngine().Execute(live, project).ModifiedConstraints);
        Assert.HasCount(1, new DiffEngine().Execute(live, Build("id > 1")).ModifiedConstraints);
    }

    [TestMethod]
    public void TheSameModelAgainstItselfIsNoChange()
    {
        var model = WithRendering(Build("status IN ('new', 'cancelled')"), "(status = ANY (ARRAY['new'::s.st, 'cancelled'::s.st]))");

        Assert.IsEmpty(new DiffEngine().Execute(model, model).ModifiedConstraints);
    }

    private static DatabaseModel Build(string check)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(Prelude + $"ALTER TABLE s.o ADD CONSTRAINT c1 CHECK ({check});")]);

    private static DatabaseModel WithRendering(DatabaseModel model, string rendering)
    {
        model.Constraints = model.Constraints.ToDictionary(
            static entry => entry.Key,
            entry => entry.Value with { ServerRenderedCheck = rendering },
            StringComparer.OrdinalIgnoreCase);
        return model;
    }
}
