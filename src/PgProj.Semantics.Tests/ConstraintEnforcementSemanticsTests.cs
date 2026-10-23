using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-121 part 2: whether a CHECK or FOREIGN KEY constraint is enforced (PostgreSQL 18's <c>NOT ENFORCED</c>) reaches the model, for a
/// constraint written in <c>CREATE TABLE</c>, as a column constraint, or in <c>ALTER TABLE</c>.
/// </summary>
[TestClass]
public sealed class ConstraintEnforcementSemanticsTests
{
    [TestMethod]
    [DataRow("CREATE TABLE public.t (a int, CONSTRAINT c CHECK (a > 0) NOT ENFORCED);")]
    [DataRow("CREATE TABLE public.t (a int CONSTRAINT c CHECK (a > 0) NOT ENFORCED);")]
    [DataRow("CREATE TABLE public.t (a int); ALTER TABLE public.t ADD CONSTRAINT c CHECK (a > 0) NOT ENFORCED;")]
    public void ANotEnforcedCheckIsNotEnforcedInTheModel(string sql)
    {
        // Act
        var constraint = Build(sql).Constraints.Values.Single(static c => c.Name == "c");

        // Assert
        Assert.IsFalse(constraint.IsEnforced);
        Assert.AreEqual("check", constraint.ConstraintType, ignoreCase: true);
    }

    [TestMethod]
    public void ANotEnforcedForeignKeyIsNotEnforcedInTheModelAndKeepsItsTarget()
    {
        // Arrange
        const string sql = "CREATE TABLE public.p (id int PRIMARY KEY); CREATE TABLE public.t (a int, CONSTRAINT c FOREIGN KEY (a) REFERENCES public.p (id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED NOT ENFORCED);";

        // Act
        var constraint = Build(sql).Constraints.Values.Single(static c => c.Name == "c");

        // Assert
        Assert.IsFalse(constraint.IsEnforced);
        Assert.AreEqual("p", constraint.ForeignKeyReferencedTable);
        Assert.AreEqual("c", constraint.ForeignKeyDeleteAction);
        Assert.IsTrue(constraint.IsDeferrable);
        Assert.AreEqual(true, constraint.IsInitiallyDeferred);
    }

    [TestMethod]
    public void EveryOtherConstraintIsEnforced()
    {
        // Arrange
        const string sql = "CREATE TABLE public.p (id int PRIMARY KEY); CREATE TABLE public.t (a int UNIQUE, b int, CONSTRAINT c1 CHECK (a > 0), CONSTRAINT c2 FOREIGN KEY (b) REFERENCES public.p (id), CONSTRAINT c3 CHECK (b > 0) ENFORCED, CONSTRAINT c4 CHECK (b < 9) NOT VALID);";

        // Act
        var constraints = Build(sql).Constraints.Values;

        // Assert
        Assert.IsTrue(constraints.All(static c => c.IsEnforced), string.Join(", ", constraints.Where(static c => !c.IsEnforced).Select(static c => c.Key)));
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
