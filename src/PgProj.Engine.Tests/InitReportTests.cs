using PgProj.Core;

namespace PgProj.Engine.Tests;

/// <summary>G-157: the Markdown report of <c>init --report</c>: its file name, its sections and their order, the contents list, and what is left out of it.</summary>
[TestClass]
public sealed class InitReportTests
{
    private static readonly DateTime Started = new(2026, 10, 2, 14, 3, 11, DateTimeKind.Utc);

    private static ScaffoldResult Result() => new(
        Files: [],
        ModeledSchemas: ["app", "public"],
        ModeledObjects:
        [
            new ScaffoldedObject("app", "View", "v_customers", "Schema/app/View/v_customers.sql"),
            new ScaffoldedObject("app", "Table", "orders", "Schema/app/Table/orders.sql"),
            new ScaffoldedObject("app", "Table", "customers", "Schema/app/Table/customers.sql"),
            new ScaffoldedObject("app", "Index", "a|b", "Schema/app/Table/customers.sql"),
            new ScaffoldedObject("public", "Extension", "citext", "Extension/citext.sql"),
            new ScaffoldedObject(null, "Publication", "pub", "Publication/pub.sql"),
        ],
        UnsupportedObjects: [new UnsupportedObject("app", "Foreign table", "remote_prices"), new UnsupportedObject(null, "Language", "plv8")],
        ExcludedSchemas: ["audit", "staging"],
        ClusterWideObjects: [new ClusterWideObject("Tablespace", "fast"), new ClusterWideObject("Role", "owner_role")],
        DanglingReferences: [new DanglingReference("Schema/app/Table/orders.sql", "audit", 2)],
        ExcludedOwnerCount: 14,
        ExcludedGrantCount: 0,
        ExcludedRoleMembershipCount: 0);

    private static string Build(ScaffoldResult result, string? schemasOption = "--exclude-schemas", bool excludeOwners = true)
        => InitReport.Build(new InitReportInput(
            Started,
            Started.AddSeconds(2.1),
            [("--source-connection", "Host=h;Password=*****"), ("--exclude-schemas", "audit, staging")],
            "localhost",
            "5432",
            "16.15",
            "shop",
            Path.Combine(Path.GetTempPath(), "proj"),
            Path.Combine(Path.GetTempPath(), "proj", "sql"),
            schemasOption,
            result,
            ["roles and their settings", "subscriptions"],
            ExcludeGrants: false,
            ExcludeRoleMemberships: false,
            ExcludeOwners: excludeOwners));

    [TestMethod]
    public void TheFileNameIsTheStartTimeInUtc()
    {
        Assert.AreEqual("init-report-2026-10-02-14-03-11.md", InitReport.FileName(Started));
        Assert.AreEqual("init-report-2026-10-02-14-03-11.md", InitReport.FileName(new DateTimeOffset(2026, 10, 2, 9, 3, 11, TimeSpan.FromHours(-5)).UtcDateTime));
    }

    [TestMethod]
    public void TheSectionsComeInTheAgreedOrderAndTheContentsListsThem()
    {
        var report = Build(Result());

        var order = new[] { "## Contents", "## Arguments", "## Summary", "### Objects modeled by schema", "## Modeled objects", "## Not modeled: not supported", "## Not modeled: excluded", "### Schemas", "### Categories", "## Not modeled: cluster-wide" };
        var positions = order.Select(heading => report.IndexOf(heading + "\n", StringComparison.Ordinal) is var at and >= 0 ? at : report.IndexOf(heading + "\r\n", StringComparison.Ordinal)).ToArray();
        Assert.IsTrue(positions.All(static at => at >= 0), string.Join(", ", positions));
        CollectionAssert.AreEqual(positions.Order().ToArray(), positions);
        StringAssert.Contains(report, "- [Not modeled: cluster-wide](#not-modeled-cluster-wide)");
        StringAssert.Contains(report, "  - [Schema app](#schema-app)");
        StringAssert.Contains(report, "    - [app (4 objects)](#app-4-objects)");
    }

    [TestMethod]
    public void TheSummaryHasTheTimesTheServerTheSchemasAndACountPerTypeOnItsOwnRow()
    {
        var report = Build(Result());

        StringAssert.Contains(report, "| Started (UTC) | 2026-10-02 14:03:11 |");
        StringAssert.Contains(report, "| Ended (UTC) | 2026-10-02 14:03:13 |");
        StringAssert.Contains(report, "| Elapsed | 2.1 s |");
        StringAssert.Contains(report, "| Server | `localhost:5432` |");
        StringAssert.Contains(report, "| PostgreSQL version | 16.15 |");
        StringAssert.Contains(report, "| Database | `shop` |");
        StringAssert.Contains(report, "| Schemas modeled | `app`, `public` |");
        StringAssert.Contains(report, "| Objects modeled | 6 |");
        StringAssert.Contains(report, "#### app (4 objects)");
        StringAssert.Contains(report, "#### public (1 object)");
        StringAssert.Contains(report, "| Table | 2 |");
        StringAssert.Contains(report, "| Index | 1 |");
    }

    [TestMethod]
    public void ModeledObjectsAreBySchemaThenTypeThenNameWithTheirFileRelativeToTheProject()
    {
        var report = Build(Result());

        var at = report.IndexOf("### Schema app", StringComparison.Ordinal);
        var section = report[at..report.IndexOf("### Schema public", StringComparison.Ordinal)];
        var rows = section.Split('\n').Where(static line => line.StartsWith("| ", StringComparison.Ordinal) && !line.StartsWith("| Type", StringComparison.Ordinal)).Select(static line => line.TrimEnd('\r')).ToArray();
        CollectionAssert.AreEqual(
            new[]
            {
                "| Index | `a\\|b` | `sql/Schema/app/Table/customers.sql` |",
                "| Table | `customers` | `sql/Schema/app/Table/customers.sql` |",
                "| Table | `orders` | `sql/Schema/app/Table/orders.sql` |",
                "| View | `v_customers` | `sql/Schema/app/View/v_customers.sql` |",
            },
            rows);
        StringAssert.Contains(report, "### Objects that belong to no schema");
    }

    [TestMethod]
    public void NotSupportedExcludedAndClusterWideAreSeparateWithTheirReasons()
    {
        var report = Build(Result());

        StringAssert.Contains(report, "| `app` | Foreign table | `remote_prices` | not supported |");
        StringAssert.Contains(report, "| — | Language | `plv8` | not supported |");
        StringAssert.Contains(report, "- roles and their settings");
        StringAssert.Contains(report, "| `audit` | excluded (--exclude-schemas) |");
        StringAssert.Contains(report, "The objects in these schemas are not listed.");
        StringAssert.Contains(report, "| `sql/Schema/app/Table/orders.sql` | `audit` | 2 |");
        StringAssert.Contains(report, "| --exclude-owners | 14 owners |");
        Assert.IsFalse(report.Contains("staging.", StringComparison.Ordinal), "No object of an excluded schema is listed.");
        StringAssert.Contains(report, "| Role | `owner_role` |");
        StringAssert.Contains(report, "| Tablespace | `fast` |");
        Assert.IsTrue(report.IndexOf("| Role |", StringComparison.Ordinal) < report.IndexOf("| Tablespace |", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SchemasNotNamedInSchemasAreExcludedForThatReason()
    {
        var report = Build(Result(), schemasOption: "--schemas");

        StringAssert.Contains(report, "| `audit` | not named in --schemas |");
    }

    [TestMethod]
    public void ASectionWithNothingInItIsLeftOutOfTheReportAndTheContents()
    {
        var result = new ScaffoldResult([], ["public"], [new ScaffoldedObject("public", "Table", "t", "Schema/public/Table/t.sql")], [], [], [], [], 0, 0, 0);

        var report = Build(result, schemasOption: null, excludeOwners: false);

        Assert.IsFalse(report.Contains("excluded", StringComparison.OrdinalIgnoreCase) && report.Contains("Not modeled: excluded", StringComparison.Ordinal));
        Assert.IsFalse(report.Contains("cluster-wide", StringComparison.Ordinal));
        Assert.IsFalse(report.Contains("(#not-modeled-excluded)", StringComparison.Ordinal));
        StringAssert.Contains(report, "## Not modeled: not supported");
    }

    [TestMethod]
    public void TheErdIsLinkedInTheSummaryAndTheContentsRelativeToTheReport()
    {
        var withErd = InitReport.Build(new InitReportInput(
            Started, Started.AddSeconds(1), [], "localhost", "5432", "16.15", "shop", Path.Combine(Path.GetTempPath(), "proj"), Path.Combine(Path.GetTempPath(), "proj", "sql"),
            null, Result(), [], false, false, false, ErdRelativePath: "../erd 2026-10-02-14-03-11.mmd",
            ErdPlacement: new ErdPlacement("Kept the existing file.", "Default (no --erd-existing): the existing file was not written by --erd, so it is kept.")));

        StringAssert.Contains(withErd, "| ERD | [erd 2026-10-02-14-03-11.mmd](../erd%202026-10-02-14-03-11.mmd) |");
        StringAssert.Contains(withErd, "| ERD action | Kept the existing file. |");
        StringAssert.Contains(withErd, "| ERD action reason | Default (no --erd-existing): the existing file was not written by --erd, so it is kept. |");
        StringAssert.Contains(withErd, "- [Entity relationship diagram](../erd%202026-10-02-14-03-11.mmd)");
        Assert.IsFalse(Build(Result()).Contains("Entity relationship diagram", StringComparison.Ordinal), "No --erd, no link.");
    }

    [TestMethod]
    public void HeadingAnchorsAreLowerCaseWithHyphensAndNoPunctuation()
    {
        Assert.AreEqual("not-modeled-not-supported", InitReport.MakeAnchor("Not modeled: not supported"));
        Assert.AreEqual("app-11-objects", InitReport.MakeAnchor("app (11 objects)"));
        Assert.AreEqual("schema-my_schema", InitReport.MakeAnchor("Schema My_Schema"));
    }
}
