using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-100: when PostgreSQL has rendered both sides of a partial index's predicate (<see cref="IndexDefinition.ServerRenderedPredicate"/>),
/// the diff compares those renderings, so a literal PostgreSQL types by its column (<c>'x'::s.st</c>) is no change and a real change
/// still is. An index's derived reference lists are not compared on their own.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ServerRenderedIndexPredicateDiffTests
{
    private const string Prelude = "CREATE SCHEMA s; CREATE TYPE s.st AS ENUM ('new', 'cancelled'); CREATE TABLE s.o (id int, status s.st);";

    [TestMethod]
    public void SameRenderingIsNoChangeEvenWhenTheWrittenPredicatesDiffer()
    {
        // The live form names the enum type in its cast; the project's does not. Neither the predicate nor the derived types may differ.
        var live = WithRendering(Build("status <> 'cancelled'::s.st"), "(status <> 'cancelled'::s.st)");
        var project = WithRendering(Build("status <> 'cancelled'"), "(status <> 'cancelled'::s.st)");

        var result = new DiffEngine().Execute(live, project);

        Assert.IsEmpty(result.ModifiedIndexes, string.Join("; ", result.ModifiedIndexes.SelectMany(static change => change.Details.Select(static detail => detail.PropertyName))));
    }

    [TestMethod]
    public void DifferentRenderingIsAChange()
    {
        var live = WithRendering(Build("status <> 'cancelled'::s.st"), "(status <> 'cancelled'::s.st)");
        var project = WithRendering(Build("status <> 'new'"), "(status <> 'new'::s.st)");

        var result = new DiffEngine().Execute(live, project);

        Assert.HasCount(1, result.ModifiedIndexes);
        var detail = result.ModifiedIndexes[0].Details.Single(static detail => detail.PropertyName == nameof(IndexDefinition.PredicateExpressionNode));
        Assert.AreEqual("(status <> 'new'::s.st)", detail.NewValue);
    }

    [TestMethod]
    public void WithoutARenderingOnBothSidesTheParsedPredicateIsComparedAsBefore()
    {
        var live = Build("status <> 'cancelled'");
        var project = WithRendering(Build("status <> 'cancelled'"), "(status <> 'cancelled'::s.st)");

        Assert.IsEmpty(new DiffEngine().Execute(live, project).ModifiedIndexes);
        Assert.HasCount(1, new DiffEngine().Execute(live, Build("status <> 'new'")).ModifiedIndexes);
    }

    [TestMethod]
    public void AKeyChangeIsStillReportedWithoutTheDerivedLists()
    {
        var live = Build("status <> 'cancelled'");
        var project = new SemanticPipeline().Execute([new PgQueryParser().Parse(Prelude + "CREATE INDEX i1 ON s.o (status) WHERE status <> 'cancelled';")]);

        Assert.HasCount(1, new DiffEngine().Execute(live, project).ModifiedIndexes);
    }

    private static DatabaseModel Build(string predicate)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(Prelude + $"CREATE INDEX i1 ON s.o (id) WHERE {predicate};")]);

    private static DatabaseModel WithRendering(DatabaseModel model, string rendering)
    {
        model.Indexes = model.Indexes.ToDictionary(
            static entry => entry.Key,
            entry => entry.Value with { ServerRenderedPredicate = rendering },
            StringComparer.OrdinalIgnoreCase);
        return model;
    }
}
