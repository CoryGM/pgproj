using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-155: PostgreSQL does not print a <c>COLLATE</c> on an index key that equals the collation of the column it indexes, so a project that repeats it is the same index as one that
/// leaves it out. A key whose collation differs from its column's, a key with a collation on a column with none, and an expression key are real differences.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class IndexKeyCollationDiffTests
{
    private static string Sql(string columnCollation, string keyCollation)
        => "CREATE SCHEMA app; CREATE COLLATION app.ci (provider = icu, locale = 'und'); "
            + $"CREATE TABLE app.t (id int, n text{columnCollation}); "
            + $"CREATE INDEX t_n ON app.t (n{keyCollation});";

    [TestMethod]
    [DataRow(" COLLATE app.ci", " COLLATE app.ci")]
    [DataRow(" COLLATE \"C\"", " COLLATE \"C\"")]
    [DataRow(" COLLATE \"C\"", " COLLATE pg_catalog.\"C\"")]
    [DataRow(" COLLATE app.ci", " COLLATE ci")]
    [DataRow("", " COLLATE \"default\"")]
    public void AKeyRepeatingItsColumnsCollationIsTheSameIndexAsOneThatLeavesItOut(string columnCollation, string keyCollation)
    {
        var repeated = Build(Sql(columnCollation, keyCollation));
        var omitted = Build(Sql(columnCollation, string.Empty));

        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(repeated, omitted)));
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(omitted, repeated)));
    }

    [TestMethod]
    [DataRow(" COLLATE \"C\"", " COLLATE \"POSIX\"")]
    [DataRow(" COLLATE app.ci", " COLLATE \"C\"")]
    [DataRow("", " COLLATE \"C\"")]
    public void AKeyWithAnotherCollationThanItsColumnIsADifference(string columnCollation, string keyCollation)
    {
        var changes = new DiffEngine().Diff(Build(Sql(columnCollation, string.Empty)), Build(Sql(columnCollation, keyCollation)));

        Assert.IsFalse(IsEmpty(changes));
    }

    [TestMethod]
    public void AnExpressionKeyKeepsItsCollation()
    {
        var withCollation = Build("CREATE SCHEMA app; CREATE TABLE app.t (n text COLLATE \"C\"); CREATE INDEX t_n ON app.t ((lower(n)) COLLATE \"C\");");
        var without = Build("CREATE SCHEMA app; CREATE TABLE app.t (n text COLLATE \"C\"); CREATE INDEX t_n ON app.t ((lower(n)));");

        Assert.IsFalse(IsEmpty(new DiffEngine().Diff(without, withCollation)));
    }

    private static bool IsEmpty(ChangeSet diff) => diff.Added.Count + diff.Removed.Count + diff.Modified.Count == 0;

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
