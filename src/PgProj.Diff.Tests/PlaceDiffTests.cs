using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-309e: where an object was written (its file, line, and column) is never compared (<c>copilot-diff.md</c> §2, the phantom-diff rule): the same objects
/// written in other files, on other lines, or with other spacing between statements diff empty, both ways.
/// </summary>
[TestClass]
public sealed class PlaceDiffTests
{
    private const string Sql = """
        CREATE SCHEMA app;
        CREATE TABLE app.customer (id bigint PRIMARY KEY, email text NOT NULL UNIQUE);
        CREATE TABLE app.orders (id int PRIMARY KEY, customer_id bigint REFERENCES app.customer (id), total numeric CHECK (total >= 0));
        CREATE INDEX orders_customer ON app.orders (customer_id);
        CREATE VIEW app.big AS SELECT id, total FROM app.orders WHERE total > 100;
        CREATE FUNCTION app.f(n int) RETURNS int LANGUAGE sql AS $$ SELECT n + 1 $$;
        CREATE TYPE app.mood AS ENUM ('sad', 'ok');
        CREATE SEQUENCE app.ticket;
        """;

    [TestMethod]
    public void ModelsThatDifferOnlyInWhereTheirObjectsWereWrittenDiffEmpty()
    {
        var first = new SemanticPipeline().Execute([new PgQueryParser().Parse(Sql) with { FilePath = "/a/schema.sql" }]);
        var moved = new SemanticPipeline().Execute([new PgQueryParser().Parse("\n\n-- moved down\n" + Sql.Replace(";\n", ";\n\n", StringComparison.Ordinal)) with { FilePath = "/b/other.sql" }]);

        Assert.AreNotEqual(first.Tables["app.orders"].Location, moved.Tables["app.orders"].Location, "the places differ");
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(first, moved)));
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(moved, first)));
    }

    [TestMethod]
    public void AModelWithPlacesAndOneWithoutDiffEmpty()
    {
        var placed = new SemanticPipeline().Execute([new PgQueryParser().Parse(Sql) with { FilePath = "/a/schema.sql" }]);
        var unplaced = new SemanticPipeline().Execute([new PgQueryParser().Parse(Sql)]);

        Assert.IsNull(unplaced.Tables["app.orders"].Location);
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(placed, unplaced)));
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(unplaced, placed)));
    }

    private static bool IsEmpty(ChangeSet diff)
        => diff.Added.Count + diff.Removed.Count + diff.Modified.Count == 0;
}
