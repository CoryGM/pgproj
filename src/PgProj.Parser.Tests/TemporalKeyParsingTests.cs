namespace PgProj.Parser.Tests;

/// <summary>
/// G-121 part 2: PostgreSQL 18's temporal keys are read into <see cref="ConstraintNode.WithoutOverlaps"/> (a PRIMARY KEY or UNIQUE whose last
/// column is <c>WITHOUT OVERLAPS</c>) and <see cref="ConstraintNode.ForeignKeyUsesPeriod"/> (a FOREIGN KEY whose last column is <c>PERIOD</c>),
/// with the range column kept in the column list in its position.
/// </summary>
[TestClass]
public sealed class TemporalKeyParsingTests
{
    [TestMethod]
    [DataRow("ALTER TABLE s.room ADD CONSTRAINT k PRIMARY KEY (id, during WITHOUT OVERLAPS);", "CONSTR_PRIMARY")]
    [DataRow("ALTER TABLE s.room ADD CONSTRAINT k UNIQUE (id, during WITHOUT OVERLAPS);", "CONSTR_UNIQUE")]
    [DataRow("CREATE TABLE s.room (id int, during tsrange, CONSTRAINT k PRIMARY KEY (id, during WITHOUT OVERLAPS));", "CONSTR_PRIMARY")]
    [DataRow("CREATE TABLE s.room (id int, during tsrange, CONSTRAINT k UNIQUE (id, during WITHOUT OVERLAPS));", "CONSTR_UNIQUE")]
    public void ATemporalPrimaryKeyOrUniqueIsWithoutOverlapsAndKeepsItsColumnsInOrder(string sql, string constraintType)
    {
        // Act
        var tree = new PgQueryParser().Parse(sql);
        var constraint = tree.Nodes.OfType<ConstraintNode>().Single(static c => c.Name == "k");

        // Assert
        Assert.AreEqual(0, tree.Diagnostics.Count(static d => d.Severity == SqlDiagnosticSeverity.Error));
        Assert.AreEqual(constraintType, constraint.ConstraintType);
        Assert.IsTrue(constraint.WithoutOverlaps);
        CollectionAssert.AreEqual(new[] { "id", "during" }, constraint.KeyColumns.ToArray());
        Assert.IsFalse(constraint.ForeignKeyUsesPeriod);
    }

    [TestMethod]
    [DataRow("ALTER TABLE s.booking ADD CONSTRAINT k FOREIGN KEY (room_id, PERIOD during) REFERENCES s.room (id, PERIOD during);")]
    [DataRow("CREATE TABLE s.booking (room_id int, during tsrange, CONSTRAINT k FOREIGN KEY (room_id, PERIOD during) REFERENCES s.room (id, PERIOD during));")]
    public void ATemporalForeignKeyUsesPeriodAndKeepsItsColumnsInOrder(string sql)
    {
        // Act
        var tree = new PgQueryParser().Parse(sql);
        var constraint = tree.Nodes.OfType<ConstraintNode>().Single(static c => c.Name == "k");

        // Assert
        Assert.AreEqual(0, tree.Diagnostics.Count(static d => d.Severity == SqlDiagnosticSeverity.Error));
        Assert.IsTrue(constraint.ForeignKeyUsesPeriod);
        CollectionAssert.AreEqual(new[] { "room_id", "during" }, constraint.ForeignKeyColumns.ToArray());
        CollectionAssert.AreEqual(new[] { "id", "during" }, constraint.ForeignKeyReferencedColumns.ToArray());
        Assert.IsFalse(constraint.WithoutOverlaps);
    }

    [TestMethod]
    [DataRow("ALTER TABLE s.room ADD CONSTRAINT k PRIMARY KEY (id, during);")]
    [DataRow("ALTER TABLE s.room ADD CONSTRAINT k UNIQUE (id, during);")]
    [DataRow("ALTER TABLE s.booking ADD CONSTRAINT k FOREIGN KEY (room_id, during) REFERENCES s.room (id, during);")]
    [DataRow("ALTER TABLE s.room ADD CONSTRAINT k CHECK (id > 0);")]
    public void AnOrdinaryConstraintIsNotTemporal(string sql)
    {
        // Act
        var constraint = new PgQueryParser().Parse(sql).Nodes.OfType<ConstraintNode>().Single(static c => c.Name == "k");

        // Assert
        Assert.IsFalse(constraint.WithoutOverlaps);
        Assert.IsFalse(constraint.ForeignKeyUsesPeriod);
    }
}
