using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-126: a <c>NOT NULL</c> constraint, named or a table constraint, makes its column not nullable in the model and adds no constraint to it,
/// wherever it is written: on the column, in <c>CREATE TABLE</c>, or in an <c>ALTER TABLE</c> in the same file or another.
/// </summary>
[TestClass]
public sealed class NotNullConstraintSemanticsTests
{
    [TestMethod]
    [DataRow("CREATE TABLE public.t (a int CONSTRAINT nn NOT NULL, b int);")]
    [DataRow("CREATE TABLE public.t (a int, b int, CONSTRAINT nn NOT NULL a);")]
    [DataRow("CREATE TABLE public.t (a int, b int); ALTER TABLE public.t ADD CONSTRAINT nn NOT NULL a;")]
    public void ANamedNotNullMakesItsColumnNotNullAndAddsNoConstraint(string sql)
    {
        // Act
        var model = Build(sql);

        // Assert
        var columns = model.Tables["public.t"].Columns.ToDictionary(static c => c.Name);
        Assert.IsFalse(columns["a"].AllowsNull);
        Assert.IsTrue(columns["b"].AllowsNull);
        Assert.IsEmpty(model.Constraints);
        Assert.AreEqual(0, model.Diagnostics.Count, string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
    }

    [TestMethod]
    public void ANotNullInAnotherFilesAlterTableAppliesToTheTableDeclaredElsewhere()
    {
        // Act
        var model = new SemanticPipeline().Execute([
            new PgQueryParser().Parse("CREATE TABLE public.t (a int, b int);"),
            new PgQueryParser().Parse("ALTER TABLE public.t ADD CONSTRAINT nn NOT NULL b;"),
        ]);

        // Assert
        Assert.IsFalse(model.Tables["public.t"].Columns.Single(static c => c.Name == "b").AllowsNull);
        Assert.IsEmpty(model.Constraints);
    }

    [TestMethod]
    public void ANotNullOnAnUndeclaredTableOrColumnIsReportedAndIgnored()
    {
        // Act
        var model = Build("CREATE TABLE public.t (a int); ALTER TABLE public.t ADD CONSTRAINT n1 NOT NULL nope; ALTER TABLE public.other ADD CONSTRAINT n2 NOT NULL a;");

        // Assert
        Assert.IsTrue(model.Tables["public.t"].Columns.Single().AllowsNull);
        Assert.AreEqual(2, model.Diagnostics.Count(static d => d.Severity == SemanticDiagnosticSeverity.Warning && d.Message.StartsWith("NOT NULL on ", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ANamedNotNullAndAnUnnamedOneAreTheSameColumn()
    {
        // Act
        var named = Build("CREATE TABLE public.t (a int CONSTRAINT nn NOT NULL);").Tables["public.t"].Columns.Single();
        var unnamed = Build("CREATE TABLE public.t (a int NOT NULL);").Tables["public.t"].Columns.Single();

        // Assert
        Assert.AreEqual(unnamed.AllowsNull, named.AllowsNull);
        Assert.AreEqual(unnamed.IsNullable, named.IsNullable);
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
