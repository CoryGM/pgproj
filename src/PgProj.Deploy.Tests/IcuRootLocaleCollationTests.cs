using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-298: an ICU collation on the root locale is written <c>locale = ''</c> by <c>pg_dump</c> before PostgreSQL 16 (and <c>und</c> from 16). The empty locale was dropped from
/// <c>CREATE COLLATION</c>, which 14 and 15 refuse, and it never compared equal to <c>und</c>.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class IcuRootLocaleCollationTests
{
    [TestMethod]
    public void AnEmptyIcuLocaleIsDeployedAsWritten()
    {
        var script = Script(DatabaseModel.Empty, Build("CREATE COLLATION public.cs (provider = icu, locale = '');"));

        StringAssert.Contains(script, "LOCALE = ''");
    }

    [TestMethod]
    public void ALibcCollationWithoutALocaleStatesNone()
    {
        var script = Script(DatabaseModel.Empty, Build("CREATE COLLATION public.c2 (provider = libc, lc_collate = 'C', lc_ctype = 'C');"));

        Assert.DoesNotContain("LOCALE =", script);
    }

    [TestMethod]
    public void TheRootLocaleIsTheSameWhicheverWayItIsSpelled()
    {
        var empty = Build("CREATE COLLATION public.cs (provider = icu, locale = '');");
        var und = Build("CREATE COLLATION public.cs (provider = icu, locale = 'und');");
        var french = Build("CREATE COLLATION public.cs (provider = icu, locale = 'fr');");

        Assert.AreEqual(0, ChangeCount(new DiffEngine().Diff(empty, und)));
        Assert.AreEqual(0, ChangeCount(new DiffEngine().Diff(und, empty)));
        Assert.AreEqual(0, ChangeCount(new DiffEngine().Diff(empty, empty)));
        Assert.AreEqual(1, ChangeCount(new DiffEngine().Diff(empty, french)));
        Assert.AreEqual(1, ChangeCount(new DiffEngine().Diff(french, und)));
    }

    private static int ChangeCount(ChangeSet diff) => diff.Added.Count + diff.Removed.Count + diff.Modified.Count;

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);

    private static string Script(DatabaseModel live, DatabaseModel project)
    {
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(live, project), new DependencyAnalyzer().Analyze(live, project));
        return string.Join("\n", batch.Operations.Where(static operation => operation is not SchemaCreateOperation).Select(static operation => operation.GenerateSql().Replace("\r\n", "\n", StringComparison.Ordinal))).Trim();
    }
}
