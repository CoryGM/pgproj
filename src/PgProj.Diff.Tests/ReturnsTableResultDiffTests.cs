using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-193: a function declared <c>RETURNS TABLE</c> with more than one column returns <c>pg_catalog.record</c>, a pseudo-type that the project listed among the
/// function's referenced types and a live database does not, so the function was modified after every publish. A changed result or parameter is still a
/// difference; a changed output column is G-198's.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ReturnsTableResultDiffTests
{
    [TestMethod]
    [DataRow("CREATE FUNCTION f(p int) RETURNS TABLE (id int, name text) LANGUAGE sql STABLE AS $$ SELECT 1, 'a' $$;")]
    [DataRow("CREATE OR REPLACE FUNCTION public.f(p integer)\n RETURNS TABLE(id integer, name text)\n LANGUAGE sql\n STABLE\nAS $function$ SELECT 1, 'a' $function$\n;")]
    [DataRow("CREATE FUNCTION f() RETURNS SETOF record LANGUAGE sql AS $$ SELECT 1, 'a' $$;")]
    [DataRow("CREATE FUNCTION f() RETURNS SETOF pg_catalog.record LANGUAGE sql AS $$ SELECT 1, 'a' $$;")]
    public void RecordIsNotAReferencedType(string sql)
    {
        var function = Build(sql).Functions.Values.Single();

        CollectionAssert.DoesNotContain(function.ReferencedTypes.Select(static type => type.ToLowerInvariant()).ToArray(), "record");
        CollectionAssert.DoesNotContain(function.ReferencedTypes.Select(static type => type.ToLowerInvariant()).ToArray(), "pg_catalog.record");
    }

    [TestMethod]
    public void TheWrittenAndThePrintedFormsAreTheSameFunction()
    {
        var project = Build("CREATE FUNCTION f(p int) RETURNS TABLE (id int, name text) LANGUAGE sql STABLE AS $$ SELECT 1, 'a' $$;");
        var live = Build("CREATE OR REPLACE FUNCTION public.f(p integer)\n RETURNS TABLE(id integer, name text)\n LANGUAGE sql\n STABLE\nAS $function$ SELECT 1, 'a' $function$\n;");

        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(live, project)));
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(project, project)));
    }

    [TestMethod]
    [DataRow("f() RETURNS TABLE (id int, name text)", "f() RETURNS SETOF int")]
    [DataRow("f() RETURNS SETOF record", "f() RETURNS SETOF int")]
    [DataRow("f(p int) RETURNS TABLE (id int, name text)", "f(p bigint) RETURNS TABLE (id int, name text)")]
    public void AChangedResultOrParameterIsStillADifference(string before, string after)
    {
        const string Body = "LANGUAGE sql AS $$ SELECT 1, 'a' $$;";

        Assert.IsFalse(IsEmpty(new DiffEngine().Diff(Build($"CREATE FUNCTION {before} {Body}"), Build($"CREATE FUNCTION {after} {Body}"))));
    }

    private static bool IsEmpty(ChangeSet diff) => diff.Added.Count + diff.Removed.Count + diff.Modified.Count == 0;

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
