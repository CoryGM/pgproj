namespace PgProj.Parser.Tests;

/// <summary>
/// G-285: a comment above a column that looks like <c>-- @renamed-from: old_name</c> and is not one (no colon, an underscore, no name) was ignored without a word,
/// so the rename became a dropped and an added column. It is a warning at the comment.
/// </summary>
[TestClass]
public sealed class RenameAnnotationNearMissTests
{
    [TestMethod]
    [DataRow("-- @renamed-from email")]
    [DataRow("-- @renamed_from: email")]
    [DataRow("-- @renamedfrom: email")]
    [DataRow("-- @renamed-from:")]
    [DataRow("-- @Renamed-From email")]
    public void ANearMissIsAWarningAtTheCommentAndRenamesNothing(string comment)
    {
        var tree = new PgQueryParser().Parse($"CREATE TABLE public.t (\n    id int,\n    {comment}\n    email_address text\n);");

        Assert.IsNull(tree.Columns.Single(static column => column.Name == "email_address").RenamedFrom);
        var warning = tree.Diagnostics.Single(static diagnostic => diagnostic.Message.Contains("not read as a column rename", StringComparison.Ordinal));
        Assert.AreEqual(SqlDiagnosticSeverity.Warning, warning.Severity);
        StringAssert.Contains(warning.Message, comment);
        StringAssert.Contains(warning.Message, "-- @renamed-from: old_name");
        Assert.AreEqual(3, warning.LineNumber);
        Assert.AreEqual(5, warning.Column);

        // G-308: the warning covers the comment, from its `--` to the end of its text.
        Assert.AreEqual(3, warning.EndLine);
        Assert.AreEqual(5 + comment.Length, warning.EndColumn);
    }

    [TestMethod]
    [DataRow("-- @renamed-from: email", "email")]
    [DataRow("-- @RENAMED-FROM: email", "email")]
    public void TheAnnotationItselfIsReadWithoutAWarning(string comment, string oldName)
    {
        var tree = new PgQueryParser().Parse($"CREATE TABLE public.t (\n    id int,\n    {comment}\n    email_address text\n);");

        Assert.AreEqual(oldName, tree.Columns.Single(static column => column.Name == "email_address").RenamedFrom);
        Assert.IsFalse(tree.Diagnostics.Any(static diagnostic => diagnostic.Message.Contains("not read as a column rename", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void AnOrdinaryCommentIsNotAWarning()
    {
        var tree = new PgQueryParser().Parse("CREATE TABLE public.t (\n    -- the address we write to\n    email text\n);");

        Assert.IsEmpty(tree.Diagnostics);
    }
}
