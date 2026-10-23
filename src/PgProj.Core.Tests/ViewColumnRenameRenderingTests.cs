using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core.Tests;

/// <summary>
/// G-276: before the project's views are rendered on the server, the columns the project renames with <c>@renamed-from</c> are renamed in the
/// rolled-back transaction, so a view that selects the new name renders and its output columns are known.
/// </summary>
[TestClass]
public sealed class ViewColumnRenameRenderingTests
{
    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);

    [TestMethod]
    public void AColumnRenamedFromALiveColumnIsRenamedFirst()
    {
        var live = Build("CREATE TABLE public.customer (id int PRIMARY KEY, email text, \"Old\" int);");
        var project = Build("""
            CREATE TABLE public.customer (
                id int PRIMARY KEY,
                -- @renamed-from: email
                email_address text,
                -- @renamed-from: "Old"
                "New" int);
            """);

        // The annotation is folded where it is read (G-287), so a quoted one names the quoted column, here as in what publish renames.
        CollectionAssert.AreEqual(
            new[]
            {
                "ALTER TABLE \"public\".\"customer\" RENAME COLUMN \"email\" TO \"email_address\";",
                "ALTER TABLE \"public\".\"customer\" RENAME COLUMN \"Old\" TO \"New\";",
            },
            ServerViewRenderer.ColumnRenameStatements(project, live).ToArray());
    }

    [TestMethod]
    public void NothingIsRenamedWhenTheLiveTableAlreadyHasTheNewNameOrLacksTheOldOne()
    {
        var project = Build("""
            CREATE TABLE public.customer (
                id int PRIMARY KEY,
                -- @renamed-from: email
                email_address text);
            CREATE TABLE public.fresh (
                -- @renamed-from: a
                b int);
            """);

        Assert.IsEmpty(ServerViewRenderer.ColumnRenameStatements(project, Build("CREATE TABLE public.customer (id int PRIMARY KEY, email_address text);")));
        Assert.IsEmpty(ServerViewRenderer.ColumnRenameStatements(project, Build("CREATE TABLE public.customer (id int PRIMARY KEY);")));
    }
}
