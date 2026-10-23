using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-97: when PostgreSQL has rendered both sides of a generated column's expression
/// (<see cref="ColumnDefinition.ServerRenderedGeneratedExpression"/>), the diff compares those renderings, so the implicit cast
/// PostgreSQL stores (<c>((qty)::numeric * price)</c>) is no change and a real change still is. Without a rendering on both
/// sides, the parsed comparison applies as before.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ServerRenderedGeneratedExpressionDiffTests
{
    [TestMethod]
    public void SameRenderingIsNoChangeEvenWhenTheWrittenExpressionsDiffer()
    {
        var live = WithRendering(Build("((qty)::numeric * price)"), "((qty)::numeric * price)");
        var project = WithRendering(Build("qty * price"), "((qty)::numeric * price)");

        Assert.IsEmpty(new DiffEngine().Execute(live, project).ModifiedColumns);
    }

    [TestMethod]
    public void DifferentRenderingIsAChange()
    {
        var live = WithRendering(Build("((qty)::numeric * price)"), "((qty)::numeric * price)");
        var project = WithRendering(Build("qty * price * 2"), "(((qty)::numeric * price) * (2)::numeric)");

        var result = new DiffEngine().Execute(live, project);

        Assert.HasCount(1, result.ModifiedColumns);
        Assert.IsTrue(result.ModifiedColumns[0].Details.Any(static detail => detail.PropertyName == nameof(ColumnDefinition.GeneratedExpression)));
    }

    [TestMethod]
    public void WithoutARenderingOnBothSidesTheParsedFormIsComparedAsBefore()
    {
        // G-71's parsed comparison already treats PostgreSQL's full parenthesization as the same expression.
        var live = Build("((qty + 1))");
        var project = WithRendering(Build("qty + 1"), "(qty + 1)");

        Assert.IsEmpty(new DiffEngine().Execute(live, project).ModifiedColumns);
        Assert.HasCount(1, new DiffEngine().Execute(live, Build("qty + 2")).ModifiedColumns);
    }

    private static DatabaseModel Build(string expression)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse($"CREATE TABLE public.t (qty integer, price numeric, total numeric GENERATED ALWAYS AS ({expression}) STORED);")]);

    private static DatabaseModel WithRendering(DatabaseModel model, string rendering)
    {
        model.Tables = model.Tables.ToDictionary(
            static entry => entry.Key,
            entry => entry.Value with
            {
                Columns = entry.Value.Columns
                    .Select(column => column.IsGenerated ? column with { ServerRenderedGeneratedExpression = rendering } : column)
                    .ToArray(),
            },
            StringComparer.OrdinalIgnoreCase);
        return model;
    }
}
