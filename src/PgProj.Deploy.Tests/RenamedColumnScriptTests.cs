using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-277: renaming a column is one <c>RENAME COLUMN</c>. Its unique constraint, <c>CHECK</c>, indexes, and the foreign keys that reference it were
/// dropped and added again around it, rebuilding the index and re-scanning the referencing table under lock.
/// </summary>
[TestClass]
public sealed class RenamedColumnScriptTests
{
    [TestMethod]
    public void ARenameIsOnlyTheRename()
    {
        var live = Build("""
            CREATE TABLE public.a (id int PRIMARY KEY, code text NOT NULL, CONSTRAINT a_code_key UNIQUE (code), CONSTRAINT a_code_check CHECK (code <> ''));
            CREATE UNIQUE INDEX a_code_lower ON public.a (lower(code));
            CREATE INDEX a_code_idx ON public.a (code) WHERE code IS NOT NULL;
            CREATE TABLE public.b (id int PRIMARY KEY, a_code text REFERENCES public.a (code));
            """);
        var project = Build("""
            CREATE TABLE public.a (id int PRIMARY KEY,
                -- @renamed-from: code
                sku text NOT NULL, CONSTRAINT a_code_key UNIQUE (sku), CONSTRAINT a_code_check CHECK (sku <> ''));
            CREATE UNIQUE INDEX a_code_lower ON public.a (lower(sku));
            CREATE INDEX a_code_idx ON public.a (sku) WHERE sku IS NOT NULL;
            CREATE TABLE public.b (id int PRIMARY KEY, a_code text REFERENCES public.a (sku));
            """);

        var statements = new ScriptGenerator().Generate(new DiffEngine().Diff(live, project), new DependencyAnalyzer().Analyze(live, project)).Operations
            .Select(static operation => operation.GenerateSql().Trim())
            .Where(static statement => !statement.StartsWith("--", StringComparison.Ordinal))
            .ToArray();

        CollectionAssert.AreEqual(new[] { "ALTER TABLE \"public\".\"a\" RENAME COLUMN \"code\" TO \"sku\";" }, statements, string.Join("\n", statements));
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
