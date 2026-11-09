namespace PgProj.Parser.Tests;

/// <summary>
/// G-191 (the user's decision of 2026-11-09): <c>-- @renamed-label: old -> new</c> on the comment lines above a <c>CREATE TYPE ... AS ENUM</c> says a label was
/// renamed, so <c>publish</c> renames it in place instead of removing one and adding another. pg_query starts a statement at its first word, so the lines
/// are read from the file above it.
/// </summary>
[TestClass]
public sealed class EnumLabelRenameParsingTests
{
    [TestMethod]
    public void TheAnnotationsAboveTheTypeAreItsRenamesInOrder()
    {
        const string Sql = """
            CREATE TABLE before_it (id int);

            -- The moods.
            -- @renamed-label: glad -> happy
            --   @Renamed-Label:  'on hold' -> 'paused'
            -- @renamed-label: 'it''s' -> its
            CREATE TYPE public.mood AS ENUM ('happy', 'paused', 'its');
            """;

        var renames = Type(Sql).EnumLabelRenames;

        CollectionAssert.AreEqual(
            new[] { new EnumLabelRename("glad", "happy"), new EnumLabelRename("on hold", "paused"), new EnumLabelRename("it's", "its") },
            renames.ToArray());
    }

    [TestMethod]
    [DataRow("CREATE TYPE public.mood AS ENUM ('a');")]
    [DataRow("-- @renamed-label: a -> b\nCREATE TABLE t (id int);\nCREATE TYPE public.mood AS ENUM ('b');")]
    [DataRow("SELECT 1; -- @renamed-label: a -> b\nCREATE TYPE public.mood AS ENUM ('b');")]
    [DataRow("-- @renamed-label: a b\n-- @renamed-label: -> b\n-- @renamed-label: a ->\n-- @renamed-label: b -> b\nCREATE TYPE public.mood AS ENUM ('b');")]
    public void OnlyAWellFormedAnnotationRightAboveTheTypeCounts(string sql)
        => Assert.IsEmpty(Type(sql).EnumLabelRenames.ToArray(), "none, one above another statement, one on a line with SQL, or one without a label on each side of ->");

    [TestMethod]
    public void ACommentBetweenStatementsOnTheSameLineIsNotTheTypes()
    {
        const string Sql = "CREATE TABLE t (id int); CREATE TYPE public.mood AS ENUM ('b');";

        Assert.AreEqual(string.Empty, TypeStatementReader.CommentLinesBefore(Sql, Sql.IndexOf("CREATE TYPE", StringComparison.Ordinal)));
    }

    private static TypeNode Type(string sql) => new PgQueryParser().Parse(sql).Nodes.OfType<TypeNode>().Single();
}
