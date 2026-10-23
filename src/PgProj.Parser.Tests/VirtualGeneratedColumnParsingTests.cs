namespace PgProj.Parser.Tests;

/// <summary>
/// G-121 part 2: PostgreSQL 18's <c>VIRTUAL</c> generated column is read into <see cref="ColumnNode.IsVirtual"/>. On 18 a generated column that
/// says neither <c>STORED</c> nor <c>VIRTUAL</c> is virtual too. A stored column, and one that is not generated, is not.
/// </summary>
[TestClass]
public sealed class VirtualGeneratedColumnParsingTests
{
    [TestMethod]
    [DataRow("CREATE TABLE s.g (a int, b int GENERATED ALWAYS AS (a * 2) VIRTUAL);")]
    [DataRow("CREATE TABLE s.g (a int, b int GENERATED ALWAYS AS (a * 2));")]
    [DataRow("CREATE TABLE s.g (a int, b int NOT NULL GENERATED ALWAYS AS (a * 2) VIRTUAL);")]
    public void AVirtualGeneratedColumnIsReadAsVirtual(string sql)
    {
        // Act
        var tree = new PgQueryParser().Parse(sql);
        var column = tree.Nodes.OfType<ColumnNode>().Single(static c => c.Name == "b");

        // Assert
        Assert.AreEqual(0, tree.Diagnostics.Count(static d => d.Severity == SqlDiagnosticSeverity.Error));
        Assert.IsTrue(column.IsGenerated);
        Assert.IsTrue(column.IsVirtual);
        StringAssert.Contains(column.GeneratedExpression, "a * 2");
    }

    [TestMethod]
    public void AVirtualColumnAddedByAlterTableIsReadAsVirtual()
    {
        // Act
        var tree = new PgQueryParser().Parse("ALTER TABLE s.g ADD COLUMN b int GENERATED ALWAYS AS (a * 2) VIRTUAL;");
        var column = tree.Nodes.OfType<AlterTableAddColumnNode>().Single().Column;

        // Assert
        Assert.IsTrue(column.IsGenerated);
        Assert.IsTrue(column.IsVirtual);
    }

    [TestMethod]
    [DataRow("CREATE TABLE s.g (a int, b int GENERATED ALWAYS AS (a * 2) STORED);")]
    public void AStoredGeneratedColumnIsNotVirtual(string sql)
    {
        // Act
        var column = new PgQueryParser().Parse(sql).Nodes.OfType<ColumnNode>().Single(static c => c.Name == "b");

        // Assert
        Assert.IsTrue(column.IsGenerated);
        Assert.IsFalse(column.IsVirtual);
    }

    [TestMethod]
    [DataRow("CREATE TABLE s.g (a int, b int DEFAULT 1);")]
    [DataRow("CREATE TABLE s.g (a int, b int GENERATED ALWAYS AS IDENTITY);")]
    [DataRow("CREATE TABLE s.g (a int, b int);")]
    public void AColumnThatIsNotGeneratedIsNotVirtual(string sql)
    {
        // Act
        var column = new PgQueryParser().Parse(sql).Nodes.OfType<ColumnNode>().Single(static c => c.Name == "b");

        // Assert
        Assert.IsFalse(column.IsGenerated && column.GeneratedExpressionAst is not null);
        Assert.IsFalse(column.IsVirtual);
    }

    [TestMethod]
    public void OnlyTheVirtualColumnOfSeveralIsVirtual()
    {
        // Act
        var columns = new PgQueryParser().Parse(
            "CREATE TABLE s.g (a int, s int GENERATED ALWAYS AS (a + 1) STORED, v int GENERATED ALWAYS AS (a + 2) VIRTUAL, u int GENERATED ALWAYS AS (a + 3));")
            .Nodes.OfType<ColumnNode>().ToDictionary(static c => c.Name!);

        // Assert
        Assert.IsFalse(columns["a"].IsVirtual);
        Assert.IsFalse(columns["s"].IsVirtual);
        Assert.IsTrue(columns["v"].IsVirtual);
        Assert.IsTrue(columns["u"].IsVirtual);
    }
}
