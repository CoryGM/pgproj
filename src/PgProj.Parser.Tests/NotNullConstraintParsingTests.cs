namespace PgProj.Parser.Tests;

/// <summary>
/// G-126: a <c>NOT NULL</c> is never a constraint of the model. Written on the column, named or not, it is only the column's nullability; written
/// as a table constraint that names its column (PostgreSQL 18: in <c>CREATE TABLE</c> or <c>ALTER TABLE ... ADD CONSTRAINT</c>) it is a marker that
/// makes that column not nullable. It used to become a <see cref="ConstraintNode"/> named after the constraint, which nothing could deploy.
/// </summary>
[TestClass]
public sealed class NotNullConstraintParsingTests
{
    [TestMethod]
    [DataRow("CREATE TABLE s.t (a int CONSTRAINT nn NOT NULL);")]
    [DataRow("CREATE TABLE s.t (a int NOT NULL);")]
    [DataRow("CREATE TABLE s.t (a int CONSTRAINT nn NOT NULL CONSTRAINT ck CHECK (a > 0));")]
    public void ANotNullWrittenOnTheColumnIsItsNullabilityAndNoConstraint(string sql)
    {
        // Act
        var tree = new PgQueryParser().Parse(sql);

        // Assert
        Assert.IsFalse(tree.Nodes.OfType<ConstraintNode>().Any(static c => c.ConstraintType is "CONSTR_NOTNULL" || c.Name == "nn"));
        Assert.IsFalse(tree.Nodes.OfType<NotNullConstraintNode>().Any());
        Assert.AreEqual(false, tree.Nodes.OfType<ColumnNode>().Single(static c => c.Name == "a").IsNullable);
    }

    [TestMethod]
    public void OtherConstraintsWrittenBesideAColumnNotNullAreStillConstraints()
    {
        // Act
        var constraints = new PgQueryParser().Parse("CREATE TABLE s.t (a int CONSTRAINT nn NOT NULL CONSTRAINT ck CHECK (a > 0), b int CONSTRAINT u UNIQUE);").Nodes.OfType<ConstraintNode>().Select(static c => c.Name).Order().ToArray();

        // Assert
        CollectionAssert.AreEqual(new[] { "ck", "u" }, constraints);
    }

    [TestMethod]
    [DataRow("CREATE TABLE s.t (a int, CONSTRAINT nn NOT NULL a);", "nn")]
    [DataRow("CREATE TABLE s.t (a int, NOT NULL a);", null)]
    [DataRow("ALTER TABLE s.t ADD CONSTRAINT nn NOT NULL a;", "nn")]
    public void ANotNullTableConstraintNamesItsColumnInAMarker(string sql, string? name)
    {
        // Act
        var tree = new PgQueryParser().Parse(sql);
        var marker = tree.Nodes.OfType<NotNullConstraintNode>().Single();

        // Assert
        Assert.AreEqual(0, tree.Diagnostics.Count(static d => d.Severity == SqlDiagnosticSeverity.Error));
        Assert.AreEqual("a", marker.ColumnName);
        Assert.AreEqual("t", marker.TableName);
        Assert.AreEqual(name, marker.ConstraintName);
        Assert.IsFalse(tree.Nodes.OfType<ConstraintNode>().Any());
    }

    [TestMethod]
    public void TheMarkerIsGivenTheDefaultSchemaLikeTheTableItNames()
    {
        // Act
        var marker = new PgQueryParser().Parse("ALTER TABLE t ADD CONSTRAINT nn NOT NULL a;").Nodes.OfType<NotNullConstraintNode>().Single();

        // Assert
        Assert.AreEqual("public", marker.SchemaName);
    }
}
