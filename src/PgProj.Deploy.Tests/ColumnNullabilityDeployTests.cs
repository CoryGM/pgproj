using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-62: an explicit <c>NULL</c> and an unspecified nullability are the same to PostgreSQL, so changing between them must emit
/// nothing, while a real change between nullable and <c>NOT NULL</c> must emit the matching statement.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ColumnNullabilityDeployTests
{
    [TestMethod]
    [DataRow("text NULL", "text")]
    [DataRow("text", "text NULL")]
    public void ExplicitNullVersusUnspecifiedEmitsNothing(string oldDefinition, string newDefinition)
    {
        Assert.IsEmpty(Statements(oldDefinition, newDefinition), $"{oldDefinition} vs {newDefinition}");
    }

    [TestMethod]
    [DataRow("text NOT NULL", "text", "DROP NOT NULL")]
    [DataRow("text NOT NULL", "text NULL", "DROP NOT NULL")]
    [DataRow("text", "text NOT NULL", "SET NOT NULL")]
    [DataRow("text NULL", "text NOT NULL", "SET NOT NULL")]
    public void RealNullabilityChangeEmitsTheMatchingStatement(string oldDefinition, string newDefinition, string expected)
    {
        var statements = Statements(oldDefinition, newDefinition);

        Assert.HasCount(1, statements, string.Join("\n", statements));
        Assert.AreEqual($"ALTER TABLE \"public\".\"t\" ALTER COLUMN \"c\" {expected};", statements[0]);
    }

    private static IReadOnlyList<string> Statements(string oldDefinition, string newDefinition)
    {
        var oldModel = Build($"create table public.t (c {oldDefinition});");
        var newModel = Build($"create table public.t (c {newDefinition});");
        return new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel)).Operations
            .Select(static operation => operation.GenerateSql().Trim())
            .ToArray();
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
