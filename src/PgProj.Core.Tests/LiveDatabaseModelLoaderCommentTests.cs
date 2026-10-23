using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core.Tests;

[TestClass]
[DoNotParallelize]
public sealed class LiveDatabaseModelLoaderCommentTests
{
    [TestMethod]
    public void BuildModelFromSnapshotSql_PreservesTableAndColumnComments()
    {
        var snapshotSql = LiveDatabaseModelLoader.GenerateSnapshotSql(
            [
                new LiveSchemaSnapshot("public")
            ],
            [
                new LiveTableSnapshot(
                    "public",
                    "accounts",
                    [
                        new LiveColumnSnapshot("id", "uuid", false, null, false, null, null, null, "primary key identifier"),
                        new LiveColumnSnapshot("name", "text", false, null, false, null, null, null, null)
                    ],
                    Comment: "stores customer accounts")
            ],
            [],
            [],
            [],
            [],
            [],
            [],
            []);

        var model = CreateLoader().BuildModelFromSnapshotSql(snapshotSql);

        Assert.IsFalse(model.HasErrors, string.Join(Environment.NewLine, model.Diagnostics.Select(static d => d.Message)));
        Assert.IsTrue(model.Tables.ContainsKey("public.accounts"));

        var table = model.Tables["public.accounts"];
        Assert.AreEqual("stores customer accounts", table.Comment);

        var idColumn = table.Columns.Single(c => c.Name == "id");
        Assert.AreEqual("primary key identifier", idColumn.Comment);

        var nameColumn = table.Columns.Single(c => c.Name == "name");
        Assert.IsNull(nameColumn.Comment);
    }

    [TestMethod]
    public void BuildModelFromSnapshotSql_TableWithoutCommentsHasNullComments()
    {
        var snapshotSql = LiveDatabaseModelLoader.GenerateSnapshotSql(
            [
                new LiveSchemaSnapshot("public")
            ],
            [
                new LiveTableSnapshot(
                    "public",
                    "accounts",
                    [
                        new LiveColumnSnapshot("id", "uuid", false, null, false, null, null)
                    ])
            ],
            [],
            [],
            [],
            [],
            [],
            [],
            []);

        var model = CreateLoader().BuildModelFromSnapshotSql(snapshotSql);

        Assert.IsFalse(model.HasErrors, string.Join(Environment.NewLine, model.Diagnostics.Select(static d => d.Message)));
        var table = model.Tables["public.accounts"];
        Assert.IsNull(table.Comment);
        Assert.IsNull(table.Columns.Single(c => c.Name == "id").Comment);
    }

    private static LiveDatabaseModelLoader CreateLoader()
        => new(new PgQueryParser(), new SemanticBuilder());
}
