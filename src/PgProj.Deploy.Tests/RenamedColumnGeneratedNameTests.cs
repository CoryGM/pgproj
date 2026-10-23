using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-289 (approved as recommended): an unnamed key, unique constraint, <c>CHECK</c>, or index is named after its column (<c>t_email_key</c>, <c>t_email_check</c>,
/// <c>t_code_idx</c>), so after a column rename the project gives it a new name while the database keeps the old one; it was dropped and added again, rebuilding
/// the index under lock. It is renamed in place, after the column, so the database matches a fresh deploy and nothing is rebuilt.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RenamedColumnGeneratedNameTests
{
    [TestMethod]
    public void AnUnnamedUniqueCheckAndIndexAreRenamedNotRebuilt()
    {
        var statements = Statements(
            "CREATE TABLE public.t (id int PRIMARY KEY, email text UNIQUE CHECK (email <> ''), code text); CREATE INDEX ON public.t (code);",
            """
            CREATE TABLE public.t (id int PRIMARY KEY,
                -- @renamed-from: email
                email_address text UNIQUE CHECK (email_address <> ''),
                -- @renamed-from: code
                sku text);
            CREATE INDEX ON public.t (sku);
            """);

        Assert.IsFalse(statements.Any(static s => s.Contains("DROP", StringComparison.Ordinal) || s.Contains("ADD CONSTRAINT", StringComparison.Ordinal) || s.StartsWith("CREATE INDEX", StringComparison.Ordinal)), string.Join("\n", statements));
        CollectionAssert.IsSubsetOf(
            new[]
            {
                "ALTER TABLE \"public\".\"t\" RENAME COLUMN \"email\" TO \"email_address\";",
                "ALTER TABLE \"public\".\"t\" RENAME COLUMN \"code\" TO \"sku\";",
                "ALTER TABLE \"public\".\"t\" RENAME CONSTRAINT \"t_email_key\" TO \"t_email_address_key\";",
                "ALTER TABLE \"public\".\"t\" RENAME CONSTRAINT \"t_email_check\" TO \"t_email_address_check\";",
                "ALTER INDEX \"public\".\"t_code_idx\" RENAME TO \"t_sku_idx\";",
            },
            statements,
            string.Join("\n", statements));
    }

    [TestMethod]
    public void ANamedConstraintKeepsItsNameAndIsNotRenamed()
    {
        var statements = Statements(
            "CREATE TABLE public.t (id int, email text CONSTRAINT t_email_key UNIQUE);",
            "CREATE TABLE public.t (id int,\n    -- @renamed-from: email\n    email_address text CONSTRAINT t_email_key UNIQUE);");

        CollectionAssert.AreEqual(new[] { "ALTER TABLE \"public\".\"t\" RENAME COLUMN \"email\" TO \"email_address\";" }, statements, string.Join("\n", statements));
    }

    [TestMethod]
    public void AnUnnamedConstraintThatAlsoChangesIsStillRebuilt()
    {
        var statements = Statements(
            "CREATE TABLE public.t (id int, email text CHECK (email <> ''));",
            "CREATE TABLE public.t (id int,\n    -- @renamed-from: email\n    email_address text CHECK (email_address <> 'x'));");

        Assert.IsTrue(statements.Any(static s => s.Contains("DROP CONSTRAINT", StringComparison.Ordinal)), string.Join("\n", statements));
    }

    [TestMethod]
    public void WithoutARenameNothingIsPaired()
    {
        var statements = Statements("CREATE TABLE public.t (id int, email text UNIQUE);", "CREATE TABLE public.t (id int, email text UNIQUE);");

        Assert.IsEmpty(statements);
    }

    private static string[] Statements(string liveSql, string projectSql)
    {
        var live = Build(liveSql);
        var project = Build(projectSql);
        return new ScriptGenerator().Generate(new DiffEngine().Diff(live, project), new DependencyAnalyzer().Analyze(live, project)).Operations
            .Select(static operation => operation.GenerateSql().Trim())
            .Where(static statement => statement.Length > 0 && !statement.StartsWith("--", StringComparison.Ordinal))
            .SelectMany(static statement => statement.Split('\n'))
            .ToArray();
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
