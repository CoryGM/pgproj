using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core.Tests;

/// <summary>
/// G-151: the tablespaces a project's tables name that the server lacks are found and named, so that <c>publish --execute</c> can refuse
/// before the server fails partway through the deployment. pgproj never creates a tablespace.
/// </summary>
[TestClass]
public sealed class ServerTablespaceRequirementsTests
{
    private static readonly string[] BuiltIn = ["pg_default", "pg_global"];

    [TestMethod]
    public void AProjectThatNamesNoTablespaceNeedsNone()
    {
        // Arrange
        var model = Build("CREATE TABLE public.t (a int); CREATE TABLE public.u (a int) WITH (fillfactor = 70);");

        // Act / Assert
        Assert.IsEmpty(ServerTablespaceRequirements.FindMissing(model, BuiltIn));
    }

    [TestMethod]
    public void ATablespaceTheServerHasIsNotMissing()
    {
        // Arrange
        var model = Build("CREATE TABLE public.t (a int) TABLESPACE fast_disk; CREATE TABLE public.u (a int) TABLESPACE pg_default;");

        // Act / Assert
        Assert.IsEmpty(ServerTablespaceRequirements.FindMissing(model, [.. BuiltIn, "fast_disk"]));
    }

    [TestMethod]
    public void AMissingTablespaceIsNamedWithEveryTableThatUsesItInOrder()
    {
        // Arrange: two tablespaces missing, one present; tables written in no particular order.
        var model = Build("""
            CREATE TABLE public.z (a int) TABLESPACE slow_disk;
            CREATE TABLE public.b (a int) TABLESPACE fast_disk;
            CREATE TABLE public.a (a int) TABLESPACE fast_disk;
            CREATE TABLE public.ok (a int) TABLESPACE here;
            """);

        // Act
        var missing = ServerTablespaceRequirements.FindMissing(model, [.. BuiltIn, "here"]);

        // Assert: by tablespace name, tables by key.
        Assert.AreEqual(2, missing.Count);
        Assert.AreEqual("fast_disk", missing[0].TablespaceName);
        CollectionAssert.AreEqual(new[] { "public.a", "public.b" }, missing[0].Tables.ToArray());
        Assert.AreEqual("slow_disk", missing[1].TablespaceName);
        CollectionAssert.AreEqual(new[] { "public.z" }, missing[1].Tables.ToArray());
    }

    [TestMethod]
    public void ANameThatDiffersFromTheServersOnlyInCaseIsNotReported()
    {
        // Arrange: identifiers are compared without regard to case everywhere in the model.
        var model = Build("CREATE TABLE public.t (a int) TABLESPACE Fast_Disk;");

        // Act / Assert
        Assert.IsEmpty(ServerTablespaceRequirements.FindMissing(model, [.. BuiltIn, "fast_disk"]));
    }

    [TestMethod]
    public void ATablespaceMissingFromAnEmptyServerListIsReported()
    {
        // Arrange: even a built-in name is missing when the list says so (the list is the server's own answer, never assumed).
        var model = Build("CREATE TABLE public.t (a int) TABLESPACE pg_default;");

        // Act
        var missing = ServerTablespaceRequirements.FindMissing(model, []);

        // Assert
        Assert.AreEqual(1, missing.Count);
        Assert.AreEqual("pg_default", missing[0].TablespaceName);
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
