namespace PgProj.Parser.Tests;

/// <summary>
/// G-42: constraints declared without a name must receive the default name PostgreSQL assigns. Expected
/// names below were verified against a real PostgreSQL server (see the Docker-backed
/// <c>UnnamedConstraints_GetTheSameNamesPostgreSqlAssigns</c> test).
/// </summary>
[TestClass]
public sealed class DefaultConstraintNamingTests
{
    [TestMethod]
    public void PrimaryKeyIsNamedTablePkey()
    {
        CollectionAssert.AreEqual(new[] { "t_pkey" }, Names("create table public.t (id bigint primary key);"));
        CollectionAssert.AreEqual(new[] { "t_pkey" }, Names("create table public.t (id bigint, primary key (id));"));
    }

    [TestMethod]
    public void UniqueIsNamedAfterItsColumns()
    {
        CollectionAssert.AreEqual(new[] { "t_a_key" }, Names("create table public.t (a integer unique);"));
        CollectionAssert.AreEqual(new[] { "t_a_b_key" }, Names("create table public.t (a integer, b integer, unique (a, b));"));
    }

    [TestMethod]
    public void AKeysAndAnExclusionConstraintsIncludeColumnsAndExpressionsAreInTheName()
    {
        // G-226: PostgreSQL 16 and 18 name these t_a_b_c_key, t_c_a_excl, t_expr_excl, and t_lower_excl.
        CollectionAssert.AreEqual(
            new[] { "t_a_b_c_key", "t_c_a_excl", "t_expr_excl", "t_lower_excl" },
            Names("create table public.t (a int, b text, c int, unique (a) include (b, c), exclude using btree (c with =) include (a), exclude using btree ((c + 1) with =), exclude using btree (lower(b) with =));"));
    }

    [TestMethod]
    public void CheckIsNamedAfterItsColumnOnlyWhenExactlyOneIsReferenced()
    {
        CollectionAssert.AreEqual(new[] { "t_a_check" }, Names("create table public.t (a integer check (a > 0));"));
        CollectionAssert.AreEqual(new[] { "t_a_check" }, Names("create table public.t (a integer check (a * a > 1));"));
        CollectionAssert.AreEqual(new[] { "t_check" }, Names("create table public.t (a integer, b integer, check (a > b));"));
        CollectionAssert.AreEqual(new[] { "t_check" }, Names("create table public.t (a integer, check (true));"));
    }

    [TestMethod]
    public void ForeignKeyIsNamedAfterItsLocalColumns()
    {
        var names = Names("""
            create table public.p (id integer primary key, x integer, y integer, unique (x, y));
            create table public.c (a integer references public.p (id), x integer, y integer, foreign key (x, y) references public.p (x, y));
            """);

        CollectionAssert.AreEquivalent(new[] { "p_pkey", "p_x_y_key", "c_a_fkey", "c_x_y_fkey" }, names);
    }

    [TestMethod]
    public void ExclusionConstraintIsNamedAfterItsColumns()
    {
        CollectionAssert.AreEqual(new[] { "t_a_excl" }, Names("create table public.t (a integer, exclude using btree (a with =));"));
    }

    [TestMethod]
    public void ConstraintsAddedByAlterTableAreNamedToo()
    {
        var names = Names("""
            create table public.t (a integer, b integer);
            alter table public.t add check (a > 0);
            alter table public.t add unique (b);
            alter table public.t add primary key (a);
            """);

        CollectionAssert.AreEquivalent(new[] { "t_a_check", "t_b_key", "t_pkey" }, names);
    }

    [TestMethod]
    public void RepeatedGeneratedNamesGetNumericSuffixes()
    {
        var names = Names("create table public.t (a integer check (a > 0), check (a <> 5), check (a < 100), check (true), check (true));");

        CollectionAssert.AreEqual(new[] { "t_a_check", "t_a_check1", "t_a_check2", "t_check", "t_check1" }, names);
    }

    [TestMethod]
    public void GeneratedNameAvoidsExplicitNamesInTheSameFile()
    {
        var names = Names("create table public.t (a integer check (a > 0), constraint t_a_check check (a < 100));");

        CollectionAssert.AreEquivalent(new[] { "t_a_check", "t_a_check1" }, names);
    }

    [TestMethod]
    public void ExplicitNamesAreLeftAlone()
    {
        var constraint = Constraints("create table public.t (a integer, constraint my_check check (a > 0));").Single();

        Assert.AreEqual("my_check", constraint.Name);
        Assert.IsFalse(constraint.IsNameGenerated);
    }

    [TestMethod]
    public void GeneratedNamesAreFlagged()
    {
        Assert.IsTrue(Constraints("create table public.t (a integer check (a > 0));").Single().IsNameGenerated);
    }

    [TestMethod]
    public void LongNamesAreTruncatedTheWayPostgreSqlDoes()
    {
        // Expected values come from a real PostgreSQL 16 server.
        var names = Names("""
            create table public.a_rather_long_table_name_used_for_truncation_tests (
                a_similarly_long_column_name_used_for_truncation integer check (a_similarly_long_column_name_used_for_truncation > 0) unique
            );
            """);

        CollectionAssert.AreEquivalent(
            new[]
            {
                "a_rather_long_table_name_use_a_similarly_long_column_name_check",
                "a_rather_long_table_name_used_a_similarly_long_column_name__key",
            },
            names);
        Assert.IsTrue(names.All(static name => name.Length <= 63));
    }

    [TestMethod]
    public void RedundantUniqueInCreateTableIsDiscardedLikePostgreSql()
    {
        var names = Names("create table public.t (id bigint, unique (id), primary key (id), a integer, unique (a), unique (a));");

        // The primary key is considered first, so unique (id) is redundant; unique (a) is declared twice.
        CollectionAssert.AreEquivalent(new[] { "t_pkey", "t_a_key" }, names);
    }

    [TestMethod]
    public void ExplicitNameOfADiscardedDuplicateTransfersToTheSurvivor()
    {
        var namesUnnamedFirst = Names("create table public.t (v integer, unique (v), constraint my_v unique (v));");
        var namesNamedFirst = Names("create table public.t (u integer, constraint my_u unique (u), unique (u));");

        CollectionAssert.AreEqual(new[] { "my_v" }, namesUnnamedFirst);
        CollectionAssert.AreEqual(new[] { "my_u" }, namesNamedFirst);
    }

    [TestMethod]
    public void DuplicateUniqueAddedByAlterTableIsKept()
    {
        var names = Names("""
            create table public.t (a integer, unique (a));
            alter table public.t add unique (a);
            """);

        CollectionAssert.AreEqual(new[] { "t_a_key", "t_a_key1" }, names);
    }

    [TestMethod]
    public void DifferentColumnOrderIsNotRedundant()
    {
        var names = Names("create table public.t (a integer, b integer, unique (a, b), unique (b, a));");

        CollectionAssert.AreEqual(new[] { "t_a_b_key", "t_b_a_key" }, names);
    }

    private static string[] Names(string sql)
        => Constraints(sql).Select(static constraint => constraint.Name!).ToArray();

    private static IReadOnlyList<ConstraintNode> Constraints(string sql)
        => ConstraintNameAssigner.Assign([new PgQueryParser().Parse(sql)])[0].Constraints
            .Where(static constraint => constraint.ConstraintType is not null)
            .ToArray();
}
