using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-287: <c>-- @renamed-from: "email"</c> or <c>-- @renamed-from: Email</c> was read by <c>diff</c> as a rename (it folded the name, G-206) and published as a
/// dropped and an added column (the deploy layer looked up the raw text), so the column's data was lost once <c>--allow-destructive</c> was given. The name is
/// folded once, where it is read, and every use compares it exactly.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RenameAnnotationFoldingTests
{
    private const string Live = "CREATE TABLE public.t (id int PRIMARY KEY, email text);";

    [TestMethod]
    [DataRow("email", "email")]
    [DataRow("\"email\"", "email")]
    [DataRow("Email", "email")]
    [DataRow("EMAIL", "email")]
    [DataRow("\"Email\"", "Email")]
    [DataRow("\"Old \"\"Name\"\"\"", "Old \"Name\"")]
    public void TheAnnotationIsReadAsPostgreSqlReadsAName(string annotation, string expected)
    {
        var tree = new PgQueryParser().Parse($"CREATE TABLE public.t (\n    id int,\n    -- @renamed-from: {annotation}\n    email_address text\n);");

        Assert.AreEqual(expected, tree.Columns.Single(static column => column.Name == "email_address").RenamedFrom);
    }

    [TestMethod]
    [DataRow("email")]
    [DataRow("\"email\"")]
    [DataRow("Email")]
    public void EachSpellingIsOneRenameColumn(string annotation)
    {
        var statements = Statements(Live, Project(annotation));

        CollectionAssert.AreEqual(new[] { "ALTER TABLE \"public\".\"t\" RENAME COLUMN \"email\" TO \"email_address\";" }, statements, string.Join("\n", statements));
    }

    [TestMethod]
    public void AQuotedMixedCaseColumnIsRenamedByItsQuotedName()
    {
        var statements = Statements(
            "CREATE TABLE public.t (id int PRIMARY KEY, \"Email\" text);",
            "CREATE TABLE public.t (id int PRIMARY KEY,\n    -- @renamed-from: \"Email\"\n    \"EmailAddress\" text);");

        CollectionAssert.AreEqual(new[] { "ALTER TABLE \"public\".\"t\" RENAME COLUMN \"Email\" TO \"EmailAddress\";" }, statements, string.Join("\n", statements));
    }

    [TestMethod]
    public void AnUnquotedAnnotationDoesNotNameAQuotedMixedCaseColumn()
    {
        // `Email` is the column email, which the table does not have, so this is a dropped and an added column, refused without --allow-destructive.
        var statements = Statements(
            "CREATE TABLE public.t (id int PRIMARY KEY, \"Email\" text);",
            "CREATE TABLE public.t (id int PRIMARY KEY,\n    -- @renamed-from: Email\n    \"EmailAddress\" text);");

        Assert.IsFalse(statements.Any(static statement => statement.Contains("RENAME COLUMN", StringComparison.Ordinal)), string.Join("\n", statements));
    }

    [TestMethod]
    public void TheColumnsKeyAndIndexStayWithACapitalizedAnnotation()
    {
        var statements = Statements(
            "CREATE TABLE public.t (id int PRIMARY KEY, email text CONSTRAINT t_email_key UNIQUE); CREATE INDEX t_email_idx ON public.t (lower(email));",
            "CREATE TABLE public.t (id int PRIMARY KEY,\n    -- @renamed-from: Email\n    email_address text CONSTRAINT t_email_key UNIQUE); CREATE INDEX t_email_idx ON public.t (lower(email_address));");

        CollectionAssert.AreEqual(new[] { "ALTER TABLE \"public\".\"t\" RENAME COLUMN \"email\" TO \"email_address\";" }, statements, string.Join("\n", statements));
    }

    private static string Project(string annotation) => $"CREATE TABLE public.t (id int PRIMARY KEY,\n    -- @renamed-from: {annotation}\n    email_address text);";

    private static string[] Statements(string liveSql, string projectSql)
    {
        var live = Build(liveSql);
        var project = Build(projectSql);
        return new ScriptGenerator().Generate(new DiffEngine().Diff(live, project), new DependencyAnalyzer().Analyze(live, project)).Operations
            .Select(static operation => operation.GenerateSql().Trim())
            .Where(static statement => !statement.StartsWith("--", StringComparison.Ordinal))
            .ToArray();
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
