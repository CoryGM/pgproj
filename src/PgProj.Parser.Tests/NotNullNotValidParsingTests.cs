namespace PgProj.Parser.Tests;

/// <summary>
/// G-121 part 2d: PostgreSQL 18's <c>NOT NULL ... NOT VALID</c> (added with <c>ALTER TABLE ... ADD CONSTRAINT</c>) is read into the
/// <see cref="NotNullConstraintNode"/> marker's <see cref="NotNullConstraintNode.IsNotValid"/> and the constraint's name.
/// </summary>
[TestClass]
public sealed class NotNullNotValidParsingTests
{
    [TestMethod]
    [DataRow("ALTER TABLE s.t ADD CONSTRAINT nn NOT NULL a NOT VALID;", "nn")]
    [DataRow("ALTER TABLE s.t ADD NOT NULL a NOT VALID;", null)]
    public void ANotValidNotNullIsReadAsNotValidWithItsName(string sql, string? name)
    {
        // Act
        var tree = new PgQueryParser().Parse(sql);
        var marker = tree.Nodes.OfType<NotNullConstraintNode>().Single();

        // Assert
        Assert.AreEqual(0, tree.Diagnostics.Count(static d => d.Severity == SqlDiagnosticSeverity.Error));
        Assert.IsTrue(marker.IsNotValid);
        Assert.AreEqual("a", marker.ColumnName);
        Assert.AreEqual(name, marker.ConstraintName);
        Assert.IsFalse(tree.Nodes.OfType<ConstraintNode>().Any());
    }

    [TestMethod]
    [DataRow("ALTER TABLE s.t ADD CONSTRAINT nn NOT NULL a;")]
    [DataRow("CREATE TABLE s.t (a int, CONSTRAINT nn NOT NULL a);")]
    public void AValidNotNullIsNotMarkedNotValid(string sql)
    {
        // Act
        var marker = new PgQueryParser().Parse(sql).Nodes.OfType<NotNullConstraintNode>().Single();

        // Assert
        Assert.IsFalse(marker.IsNotValid);
    }
}
