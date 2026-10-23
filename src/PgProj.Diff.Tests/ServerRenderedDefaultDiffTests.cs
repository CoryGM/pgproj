using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-102: when PostgreSQL has rendered both sides of a column default (<see cref="ColumnDefinition.ServerRenderedDefault"/>),
/// the diff compares those renderings, so a default PostgreSQL rewrites (<c>0::numeric</c> as <c>(0)::numeric</c>) is no
/// change and a real change still is. Without a rendering on both sides, the canonical text comparison applies as before.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ServerRenderedDefaultDiffTests
{
    [TestMethod]
    public void SameRenderingIsNoChangeEvenWhenTheWrittenDefaultsDiffer()
    {
        var live = WithRendering(Build("(0)::numeric"), "(0)::numeric");
        var project = WithRendering(Build("0::numeric"), "(0)::numeric");

        var result = new DiffEngine().Execute(live, project);

        Assert.IsEmpty(result.ModifiedColumns);
    }

    [TestMethod]
    public void DifferentRenderingIsAChangeWithTheRenderedValues()
    {
        var live = WithRendering(Build("(0)::numeric"), "(0)::numeric");
        var project = WithRendering(Build("1::numeric"), "(1)::numeric");

        var result = new DiffEngine().Execute(live, project);

        Assert.HasCount(1, result.ModifiedColumns);
        var detail = result.ModifiedColumns[0].Details.Single(static detail => detail.PropertyName == nameof(ColumnDefinition.DefaultExpressionNode));
        Assert.AreEqual("(0)::numeric", detail.OldValue);
        Assert.AreEqual("(1)::numeric", detail.NewValue);
    }

    [TestMethod]
    public void WithoutARenderingOnBothSidesTheCanonicalFormIsComparedAsBefore()
    {
        // G-56's canonical form already treats 'x'::text and 'x' as the same default; it still applies on its own.
        var live = Build("'x'::text", "text");
        var project = WithRendering(Build("'x'", "text"), "'x'::text");

        Assert.IsEmpty(new DiffEngine().Execute(live, project).ModifiedColumns);
        Assert.HasCount(1, new DiffEngine().Execute(live, Build("'y'", "text")).ModifiedColumns);
    }

    [TestMethod]
    public void TheComparisonHelperIgnoresRenderingsForAGeneratedColumn()
    {
        var generated = new SemanticPipeline().Execute([new PgQueryParser().Parse("CREATE TABLE public.t (a int, c int GENERATED ALWAYS AS (a * 2) STORED);")])
            .Tables["public.t"].Columns.Single(static column => column.Name == "c") with { ServerRenderedDefault = "0" };

        Assert.AreEqual((null, null), ColumnDefinition.GetComparableDefaultExpressions(generated, generated));
    }

    private static DatabaseModel Build(string defaultExpression, string type = "numeric")
        => new SemanticPipeline().Execute([new PgQueryParser().Parse($"CREATE TABLE public.t (id int, c {type} DEFAULT {defaultExpression});")]);

    private static DatabaseModel WithRendering(DatabaseModel model, string rendering)
    {
        model.Tables = model.Tables.ToDictionary(
            static entry => entry.Key,
            entry => entry.Value with
            {
                Columns = entry.Value.Columns
                    .Select(column => column.Name == "c" ? column with { ServerRenderedDefault = rendering } : column)
                    .ToArray(),
            },
            StringComparer.OrdinalIgnoreCase);
        return model;
    }
}
