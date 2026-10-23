using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-121 part 2d: a <c>NOT VALID</c> NOT NULL (PostgreSQL 18) makes its column not nullable and not valid, and keeps the constraint's name (which
/// <c>VALIDATE CONSTRAINT</c> needs); an ordinary NOT NULL keeps no name and is valid.
/// </summary>
[TestClass]
public sealed class NotNullNotValidSemanticsTests
{
    [TestMethod]
    public void ANotValidNotNullIsNotNullAndNotValidWithItsName()
    {
        // Act
        var column = Build("CREATE TABLE public.t (a int, b int); ALTER TABLE public.t ADD CONSTRAINT nn NOT NULL a NOT VALID;").Tables["public.t"].Columns.Single(static c => c.Name == "a");

        // Assert
        Assert.IsFalse(column.AllowsNull);
        Assert.IsTrue(column.NotNullIsNotValid);
        Assert.AreEqual("nn", column.NotNullConstraintName);
    }

    [TestMethod]
    public void ANotValidNotNullWithoutANameHasNoName()
    {
        // Act
        var column = Build("CREATE TABLE public.t (a int); ALTER TABLE public.t ADD NOT NULL a NOT VALID;").Tables["public.t"].Columns.Single();

        // Assert
        Assert.IsTrue(column.NotNullIsNotValid);
        Assert.IsNull(column.NotNullConstraintName);
    }

    [TestMethod]
    [DataRow("CREATE TABLE public.t (a int NOT NULL);")]
    [DataRow("CREATE TABLE public.t (a int CONSTRAINT nn NOT NULL);")]
    [DataRow("CREATE TABLE public.t (a int, CONSTRAINT nn NOT NULL a);")]
    [DataRow("CREATE TABLE public.t (a int); ALTER TABLE public.t ADD CONSTRAINT nn NOT NULL a;")]
    public void AnOrdinaryNotNullIsValidAndKeepsNoName(string sql)
    {
        // Act
        var column = Build(sql).Tables["public.t"].Columns.Single();

        // Assert
        Assert.IsFalse(column.AllowsNull);
        Assert.IsFalse(column.NotNullIsNotValid);
        Assert.IsNull(column.NotNullConstraintName);
    }

    [TestMethod]
    public void ANullableColumnIsNeverNotValid()
    {
        // Act
        var column = Build("CREATE TABLE public.t (a int);").Tables["public.t"].Columns.Single();

        // Assert
        Assert.IsTrue(column.AllowsNull);
        Assert.IsFalse(column.NotNullIsNotValid);
    }

    [TestMethod]
    public void ANotValidNotNullInAnotherFilesAlterTableAppliesToTheTableDeclaredElsewhere()
    {
        // Act
        var model = new SemanticPipeline().Execute([
            new PgQueryParser().Parse("CREATE TABLE public.t (a int, b int);"),
            new PgQueryParser().Parse("ALTER TABLE public.t ADD CONSTRAINT nn NOT NULL b NOT VALID;"),
        ]);

        // Assert
        var column = model.Tables["public.t"].Columns.Single(static c => c.Name == "b");
        Assert.IsTrue(column.NotNullIsNotValid);
        Assert.AreEqual("nn", column.NotNullConstraintName);
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
