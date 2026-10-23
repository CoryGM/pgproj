using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>G-153: a table that names the database's default tablespace is the same as one that names none; one that names any other tablespace is not.</summary>
[TestClass]
public sealed class DefaultTablespaceTests
{
    private const string Sql = "CREATE TABLE public.a (x int) TABLESPACE pg_default; CREATE TABLE public.b (x int) TABLESPACE other; CREATE TABLE public.c (x int);";

    [TestMethod]
    public void ATableNamingTheDefaultTablespaceLosesTheClause()
    {
        var model = Build(Sql);

        DefaultTablespace.Apply(model, "pg_default");

        Assert.IsNull(model.Tables["public.a"].TablespaceName);
        Assert.AreEqual("other", model.Tables["public.b"].TablespaceName);
        Assert.IsNull(model.Tables["public.c"].TablespaceName);
    }

    [TestMethod]
    public void InADatabaseWhoseDefaultIsAnotherTablespacePgDefaultIsARealOne()
    {
        var model = Build(Sql);

        DefaultTablespace.Apply(model, "other");

        Assert.AreEqual("pg_default", model.Tables["public.a"].TablespaceName);
        Assert.IsNull(model.Tables["public.b"].TablespaceName);
    }

    [TestMethod]
    public void TheNameIsComparedExactlyAndAnUnknownDefaultChangesNothing()
    {
        var model = Build(Sql);

        DefaultTablespace.Apply(model, null);
        Assert.AreEqual("pg_default", model.Tables["public.a"].TablespaceName);

        // A tablespace name is compared exactly, as PostgreSQL compares it (G-206): PG_DEFAULT is another tablespace.
        DefaultTablespace.Apply(model, "PG_DEFAULT");
        Assert.AreEqual("pg_default", model.Tables["public.a"].TablespaceName);

        DefaultTablespace.Apply(model, "pg_default");
        Assert.IsNull(model.Tables["public.a"].TablespaceName);
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
