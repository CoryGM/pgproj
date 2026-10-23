namespace PgProj.Parser.Tests;

/// <summary>
/// G-121 part 2: PostgreSQL 18's <c>NOT ENFORCED</c> on a CHECK or FOREIGN KEY constraint is read into <see cref="ConstraintNode.IsEnforced"/>,
/// however it is written: in <c>ALTER TABLE ... ADD CONSTRAINT</c>, or in <c>CREATE TABLE</c> as a column or table constraint (where pg_query
/// gives it as a separate attribute entry after the constraint). Every constraint written for an earlier version stays enforced.
/// </summary>
[TestClass]
public sealed class NotEnforcedConstraintParsingTests
{
    [TestMethod]
    [DataRow("ALTER TABLE s.t ADD CONSTRAINT c CHECK (a > 0) NOT ENFORCED;", "c")]
    [DataRow("ALTER TABLE s.t ADD CONSTRAINT c FOREIGN KEY (a) REFERENCES s.p (id) NOT ENFORCED;", "c")]
    [DataRow("ALTER TABLE s.t ADD CONSTRAINT c FOREIGN KEY (a) REFERENCES s.p (id) DEFERRABLE INITIALLY DEFERRED NOT ENFORCED;", "c")]
    [DataRow("CREATE TABLE s.t (a int, CONSTRAINT c CHECK (a > 0) NOT ENFORCED);", "c")]
    [DataRow("CREATE TABLE s.t (a int, CONSTRAINT c FOREIGN KEY (a) REFERENCES s.p (id) NOT ENFORCED);", "c")]
    [DataRow("CREATE TABLE s.t (a int CONSTRAINT c CHECK (a > 0) NOT ENFORCED);", "c")]
    [DataRow("CREATE TABLE s.t (a int CONSTRAINT c REFERENCES s.p (id) NOT ENFORCED);", "c")]
    public void ANotEnforcedConstraintIsReadAsNotEnforced(string sql, string name)
    {
        // Act
        var tree = new PgQueryParser().Parse(sql);

        // Assert
        Assert.AreEqual(0, tree.Diagnostics.Count(static d => d.Severity == SqlDiagnosticSeverity.Error));
        Assert.IsFalse(tree.Nodes.OfType<ConstraintNode>().Single(c => c.Name == name).IsEnforced);
    }

    [TestMethod]
    [DataRow("ALTER TABLE s.t ADD CONSTRAINT c CHECK (a > 0);")]
    [DataRow("ALTER TABLE s.t ADD CONSTRAINT c CHECK (a > 0) ENFORCED;")]
    [DataRow("ALTER TABLE s.t ADD CONSTRAINT c CHECK (a > 0) NOT VALID;")]
    [DataRow("ALTER TABLE s.t ADD CONSTRAINT c FOREIGN KEY (a) REFERENCES s.p (id) ENFORCED;")]
    [DataRow("CREATE TABLE s.t (a int, CONSTRAINT c CHECK (a > 0));")]
    [DataRow("CREATE TABLE s.t (a int CONSTRAINT c CHECK (a > 0) ENFORCED);")]
    [DataRow("CREATE TABLE s.t (a int CONSTRAINT c REFERENCES s.p (id));")]
    public void AnEnforcedConstraintIsReadAsEnforced(string sql)
    {
        // Act
        var tree = new PgQueryParser().Parse(sql);

        // Assert
        Assert.IsTrue(tree.Nodes.OfType<ConstraintNode>().Single(static c => c.Name == "c").IsEnforced);
    }

    [TestMethod]
    [DataRow("ALTER TABLE s.t ADD CONSTRAINT c PRIMARY KEY (a);")]
    [DataRow("ALTER TABLE s.t ADD CONSTRAINT c UNIQUE (a);")]
    [DataRow("ALTER TABLE s.t ADD CONSTRAINT c EXCLUDE USING gist (a WITH =);")]
    public void OnlyACheckOrForeignKeyCanBeNotEnforced(string sql)
    {
        // Act
        var constraint = new PgQueryParser().Parse(sql).Nodes.OfType<ConstraintNode>().Single(static c => c.Name == "c");

        // Assert
        Assert.IsTrue(constraint.IsEnforced);
    }

    [TestMethod]
    public void NotEnforcedAppliesToItsOwnConstraintOnly()
    {
        // Arrange
        const string sql = "CREATE TABLE s.t (a int CONSTRAINT one CHECK (a > 0) NOT ENFORCED, b int CONSTRAINT two CHECK (b > 0), CONSTRAINT three CHECK (a < b));";

        // Act
        var constraints = new PgQueryParser().Parse(sql).Nodes.OfType<ConstraintNode>().ToDictionary(static c => c.Name!);

        // Assert
        Assert.IsFalse(constraints["one"].IsEnforced);
        Assert.IsTrue(constraints["two"].IsEnforced);
        Assert.IsTrue(constraints["three"].IsEnforced);
    }

    [TestMethod]
    public void AlterConstraintEnforcementIsAnUnsupportedTableActionNotAnError()
    {
        // Act
        var diagnostics = new PgQueryParser().Parse("ALTER TABLE s.t ALTER CONSTRAINT c NOT ENFORCED;").Diagnostics;

        // Assert: reported like every other ALTER TABLE action pgproj does not apply (G-47), never as a syntax error.
        Assert.AreEqual(0, diagnostics.Count(static d => d.Severity == SqlDiagnosticSeverity.Error));
    }
}
