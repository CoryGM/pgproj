using PgProj.Dependencies;
using PgProj.Deploy;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Engine.Tests;

/// <summary>
/// G-270 (scoped by the user on 2026-10-13): <c>publish</c> names, before anything runs, each change that rewrites, scans, or blocks writes to a table the database already
/// has, and <c>--lock-timeout</c> takes a PostgreSQL duration.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class LockWarningsTests
{
    private const string Table = "CREATE TABLE public.t (id int PRIMARY KEY, a int, v varchar(10), n numeric(8, 2), s text);";

    [TestMethod]
    [DataRow("CREATE TABLE public.t (id int PRIMARY KEY, a bigint, v varchar(10), n numeric(8, 2), s text);", "ALTER COLUMN a TYPE rewrites the table")]
    [DataRow("CREATE TABLE public.t (id int PRIMARY KEY, a int NOT NULL, v varchar(10), n numeric(8, 2), s text);", "ALTER COLUMN a SET NOT NULL scans the whole table")]
    [DataRow(Table + "ALTER TABLE public.t ADD COLUMN b int GENERATED ALWAYS AS IDENTITY;", "ADD COLUMN b (an identity) rewrites")]
    [DataRow(Table + "ALTER TABLE public.t ADD COLUMN b serial;", "ADD COLUMN b (a serial) rewrites")]
    [DataRow(Table + "ALTER TABLE public.t ADD COLUMN b int GENERATED ALWAYS AS (a * 2) STORED;", "ADD COLUMN b (a stored generated column) rewrites")]
    [DataRow(Table + "ALTER TABLE public.t ADD COLUMN b double precision DEFAULT random();", "ADD COLUMN b (a default calling random()) rewrites")]
    [DataRow("CREATE UNLOGGED TABLE public.t (id int PRIMARY KEY, a int, v varchar(10), n numeric(8, 2), s text);", "SET UNLOGGED rewrites")]
    [DataRow(Table + "CREATE INDEX t_a ON public.t (a);", "CREATE INDEX t_a blocks writes")]
    [DataRow(Table + "ALTER TABLE public.t ADD CONSTRAINT t_a_ck CHECK (a > 0);", "ADD CONSTRAINT t_a_ck CHECK scans the whole table")]
    [DataRow(Table + "CREATE TABLE public.r (id int PRIMARY KEY); ALTER TABLE public.t ADD CONSTRAINT t_a_fk FOREIGN KEY (a) REFERENCES public.r;", "ADD CONSTRAINT t_a_fk FOREIGN KEY scans the whole table")]
    [DataRow(Table + "ALTER TABLE public.t ADD CONSTRAINT t_a_uq UNIQUE (a);", "ADD CONSTRAINT t_a_uq builds an index")]
    public void AChangeThatRewritesScansOrBlocksIsNamed(string projectSql, string expected)
    {
        var warnings = Find(Table, projectSql);

        StringAssert.Contains(LockWarnings.Describe(warnings), "public.t: " + expected);
    }

    [TestMethod]
    [DataRow("CREATE TABLE public.t (id int PRIMARY KEY, a int, v varchar(20), n numeric(8, 2), s text);")]
    [DataRow("CREATE TABLE public.t (id int PRIMARY KEY, a int, v varchar, n numeric(8, 2), s text);")]
    [DataRow("CREATE TABLE public.t (id int PRIMARY KEY, a int, v text, n numeric(8, 2), s text);")]
    [DataRow("CREATE TABLE public.t (id int PRIMARY KEY, a int, v varchar(10), n numeric(12, 2), s text);")]
    [DataRow(Table + "ALTER TABLE public.t ADD COLUMN b int DEFAULT 0;")]
    [DataRow(Table + "ALTER TABLE public.t ADD COLUMN b timestamptz DEFAULT now();")]
    [DataRow(Table + "ALTER TABLE public.t ADD COLUMN b int;")]
    [DataRow(Table + "ALTER TABLE public.t ADD CONSTRAINT t_a_ck CHECK (a > 0) NOT VALID;")]
    [DataRow(Table + "COMMENT ON TABLE public.t IS 'x';")]
    public void AChangeThatNeedsNoRewriteScanOrBuildIsNotNamed(string projectSql)
    {
        Assert.IsEmpty(Find(Table, projectSql), LockWarnings.Describe(Find(Table, projectSql)));
    }

    [TestMethod]
    public void ANewTableIsNotNamedForItsIndexesAndConstraints()
    {
        var warnings = Find(string.Empty, Table + "CREATE INDEX t_a ON public.t (a); ALTER TABLE public.t ADD CONSTRAINT t_a_ck CHECK (a > 0);");

        Assert.IsEmpty(warnings, LockWarnings.Describe(warnings));
    }

    [TestMethod]
    public void ADefaultCallingAProjectFunctionThatIsNotImmutableOrStableIsARewrite()
    {
        const string function = "CREATE FUNCTION public.next_code() RETURNS int LANGUAGE sql AS $$ SELECT 1 $$; CREATE FUNCTION public.fixed_code() RETURNS int LANGUAGE sql IMMUTABLE AS $$ SELECT 1 $$;";

        StringAssert.Contains(LockWarnings.Describe(Find(Table + function, Table + function + "ALTER TABLE public.t ADD COLUMN b int DEFAULT public.next_code();")), "a default calling next_code(), which is volatile");
        Assert.IsEmpty(Find(Table + function, Table + function + "ALTER TABLE public.t ADD COLUMN b int DEFAULT public.fixed_code();"));
    }

    private static IReadOnlyList<LockWarning> Find(string liveSql, string projectSql)
    {
        var live = Build(liveSql);
        var project = Build(projectSql);
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(live, project), new DependencyAnalyzer().Analyze(live, project));
        return LockWarnings.Find(batch, live, project);
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
