using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-177: PostgreSQL stores a storage parameter as <c>fillfactor=70</c>, so the same option written <c>fillfactor = 70</c> is no change,
/// while a different value, a different name, or an added or removed option still is.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class StorageOptionSpacingDiffTests
{
    [TestMethod]
    [DataRow("fillfactor=70", "fillfactor = 70")]
    [DataRow("fillfactor = 70, autovacuum_enabled = false", "autovacuum_enabled=false, fillfactor=70")]
    [DataRow("toast.autovacuum_enabled =true", "toast.autovacuum_enabled= true")]
    public void TheSameOptionsWithDifferentSpacingAreNoChange(string live, string project)
        => Assert.IsEmpty(new DiffEngine().Execute(Build(live), Build(project)).ModifiedTables);

    [TestMethod]
    [DataRow("fillfactor=70", "fillfactor = 80")]
    [DataRow("fillfactor=70", "fillfactor = 70, autovacuum_enabled = false")]
    [DataRow("fillfactor=70, autovacuum_enabled=false", "fillfactor = 70")]
    [DataRow("fillfactor=70", "autovacuum_enabled = false")]
    public void ARealChangeIsStillSeenWhateverTheSpacing(string live, string project)
        => Assert.HasCount(1, new DiffEngine().Execute(Build(live), Build(project)).ModifiedTables);

    [TestMethod]
    [DataRow("fillfactor = 70", "fillfactor=70")]
    [DataRow("fillfactor=70", "fillfactor=70")]
    [DataRow("  toast.autovacuum_enabled   =   true ", "toast.autovacuum_enabled=true")]
    [DataRow("security_barrier", "security_barrier")]
    [DataRow("opt = 'A = B'", "opt='A = B'")]
    public void ComparisonTextRemovesTheSpacingAroundTheFirstEqualsOnly(string written, string expected)
        => Assert.AreEqual(expected, new TableStorageOptionNode(written.Split('=')[0].Trim(), written).ComparisonText);

    private static DatabaseModel Build(string options)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse($"CREATE TABLE public.t (a integer) WITH ({options});")]);
}
