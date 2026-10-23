using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>G-121 part 2: whether a generated column is virtual (PostgreSQL 18) reaches the model, from <c>CREATE TABLE</c> and from <c>ALTER TABLE ADD COLUMN</c>.</summary>
[TestClass]
public sealed class VirtualGeneratedColumnSemanticsTests
{
    [TestMethod]
    [DataRow("CREATE TABLE public.t (a int, b int GENERATED ALWAYS AS (a * 2) VIRTUAL);", true)]
    [DataRow("CREATE TABLE public.t (a int, b int GENERATED ALWAYS AS (a * 2));", true)]
    [DataRow("CREATE TABLE public.t (a int, b int GENERATED ALWAYS AS (a * 2) STORED);", false)]
    [DataRow("CREATE TABLE public.t (a int); ALTER TABLE public.t ADD COLUMN b int GENERATED ALWAYS AS (a * 2) VIRTUAL;", true)]
    [DataRow("CREATE TABLE public.t (a int); ALTER TABLE public.t ADD COLUMN b int GENERATED ALWAYS AS (a * 2) STORED;", false)]
    public void TheGenerationKindIsInTheModel(string sql, bool virtualColumn)
    {
        // Act
        var column = Build(sql).Tables["public.t"].Columns.Single(static c => c.Name == "b");

        // Assert
        Assert.IsTrue(column.IsGenerated);
        Assert.AreEqual(virtualColumn, column.IsVirtual);
        Assert.IsNotNull(column.NormalizedGeneratedExpression);
    }

    [TestMethod]
    public void AColumnThatIsNotGeneratedIsNotVirtual()
    {
        // Act
        var columns = Build("CREATE TABLE public.t (a int, b int DEFAULT 1, c int GENERATED ALWAYS AS IDENTITY);").Tables["public.t"].Columns;

        // Assert
        Assert.IsFalse(columns.Any(static c => c.IsVirtual));
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
