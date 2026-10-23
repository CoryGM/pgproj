using System.Text;
using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-314: the column defaults' and the indexes' column references are resolved through one lookup per run of their pass, not a scan of every column of
/// the model per reference (which made those passes quadratic in a project's columns), and the lookup finds what the scan found.
/// </summary>
[TestClass]
public sealed class ColumnKeyLookupTests
{
    [TestMethod]
    public void TheLookupFindsWhatAScanOfEveryColumnFinds()
    {
        // Arrange: a quoted table named "s.t" whose column's table-qualified name (s.t.c) is the key of s.t's column, declared first, and the
        // other way round for "u.v"; the same column name in several tables; a table with no schema written.
        var model = Build(
            """
            CREATE SCHEMA s;
            CREATE SCHEMA u;
            CREATE TABLE public."s.t" (c int, d int);
            CREATE TABLE s.t (c int, e int);
            CREATE TABLE u.v (c int);
            CREATE TABLE public."u.v" (c int);
            CREATE TABLE plain (c int, "Mixed" int);
            CREATE TABLE s.plain (c int);
            """);
        var lookup = new ColumnKeyLookup(model);
        var columns = model.Tables.Values.SelectMany(static table => table.Columns).ToArray();
        var references = columns.SelectMany(static column => new[] { column.Key, $"{column.TableName}.{column.Name}", column.Name })
            .Concat(["s.t.c", "u.v.c", "public.plain.Mixed", "plain.mixed", "missing", "s.missing.c"])
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        foreach (var reference in references)
        {
            // Act
            var found = lookup.Find(reference);

            // Assert: the scan the passes made before.
            var scanned = columns.FirstOrDefault(column => string.Equals(column.Key, reference, StringComparison.Ordinal)
                || string.Equals(column.TableName is null ? column.Name : $"{column.TableName}.{column.Name}", reference, StringComparison.Ordinal));
            Assert.AreSame(scanned, found, reference);
            Assert.AreSame(columns.FirstOrDefault(column => string.Equals(column.Key, reference, StringComparison.Ordinal)), lookup.FindByKey(reference), reference);
        }
    }

    [TestMethod]
    public void EachPassIndexesTheModelOnceNotOncePerReference()
    {
        // Arrange: many tables, each with a foreign key, a default, and an expression index, so both passes resolve a column reference per table.
        const int TableCount = 300;
        var sql = new StringBuilder();
        for (var i = 0; i < TableCount; i++)
        {
            sql.Append($"CREATE TABLE public.t{i} (id bigint PRIMARY KEY, prev bigint REFERENCES public.t{Math.Max(i - 1, 0)} (id), name text NOT NULL DEFAULT '');\n");
            sql.Append($"CREATE INDEX ix_t{i}_name ON public.t{i} (lower(name));\n");
        }

        var counter = ColumnKeyLookup.StartCounting();

        // Act
        var model = Build(sql.ToString());

        // Assert: the model's columns indexed at most once by each of the two passes; a scan per reference would be hundreds of times the columns.
        var columnCount = model.Tables.Values.Sum(static table => table.Columns.Count);
        Assert.AreEqual(TableCount * 3, columnCount);
        Assert.IsGreaterThan(0, counter.Value, "the passes resolve their references through the lookup");
        Assert.IsLessThanOrEqualTo(2 * columnCount, counter.Value, $"{counter.Value} columns indexed for {columnCount} columns");
    }

    [TestMethod]
    public void AnIndexAndADefaultStillNameTheirColumns()
    {
        var model = Build(
            """
            CREATE TABLE public.a (id int PRIMARY KEY, name text);
            CREATE TABLE public.b (id int PRIMARY KEY, a_id int REFERENCES public.a (id), name text DEFAULT 'x');
            CREATE INDEX ix_b_name ON public.b (lower(name));
            CREATE INDEX ix_a_name ON public.a (upper(name));
            """);

        CollectionAssert.Contains(model.Indexes["public.ix_b_name"].ReferencedColumns.ToArray(), "public.b.name");
        CollectionAssert.Contains(model.Indexes["public.ix_a_name"].ReferencedColumns.ToArray(), "public.a.name");
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
