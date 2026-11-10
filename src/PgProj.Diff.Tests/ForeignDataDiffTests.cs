using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-132: foreign data wrappers, servers, and user mappings compared: the same written another way is no change; a changed option is reported with a variable's value
/// and a password never shown; a mapping whose options the connected role cannot read is not compared by them; <c>FOR CURRENT_USER</c> is the connected role's mapping.
/// </summary>
[TestClass]
public sealed class ForeignDataDiffTests
{
    private const string Objects = """
        CREATE FOREIGN DATA WRAPPER w VALIDATOR app.v;
        CREATE SERVER s VERSION '18' FOREIGN DATA WRAPPER w OPTIONS (host 'db', port '5432');
        CREATE USER MAPPING FOR app SERVER s OPTIONS (user 'u', password :'pw');

        """;

    [TestMethod]
    public void TheSameWrittenAnotherWayIsNoChange()
    {
        Assert.IsEmpty(Changes(Objects, Objects));
        Assert.IsEmpty(Changes(Objects, Objects.Replace("host 'db', port '5432'", "port '5432', host 'db'", StringComparison.Ordinal)), "Option order means nothing.");
        Assert.IsEmpty(Changes(Objects, Objects.Replace("VERSION '18' ", string.Empty, StringComparison.Ordinal)), "A VERSION the project does not state is not compared.");
    }

    [TestMethod]
    public void AChangedOptionIsReportedWithoutItsSecret()
    {
        var change = new DiffEngine().Diff(Build(Objects), Build(Objects.Replace("user 'u'", "user 'other'", StringComparison.Ordinal))).Modified.Single();

        var detail = change.Details.Single();
        Assert.AreEqual("Options", detail.PropertyName);
        StringAssert.Contains(detail.NewValue, "user=other");
        StringAssert.Contains(detail.NewValue, "password=:'pw'");
    }

    [TestMethod]
    public void AMappingWhoseOptionsCannotBeReadIsNotComparedByThem()
    {
        var live = Build(Objects.Replace("user 'u', password :'pw'", "user 'x'", StringComparison.Ordinal));
        live.UserMappings = live.UserMappings.ToDictionary(static pair => pair.Key, static pair => pair.Value with { OptionsUnreadable = true });

        Assert.IsEmpty(new DiffEngine().Diff(live, Build(Objects)).Modified);
    }

    [TestMethod]
    public void ForCurrentUserIsTheConnectedRolesMapping()
    {
        var live = Build(Objects);
        live.ConnectedRole = "app";

        var changes = new DiffEngine().Diff(live, Build(Objects.Replace("FOR app", "FOR CURRENT_USER", StringComparison.Ordinal)));
        Assert.IsEmpty(changes.Added);
        Assert.IsEmpty(changes.Removed);
    }

    private static IReadOnlyList<ModifiedChange> Changes(string live, string project) => new DiffEngine().Diff(Build(live), Build(project)).Modified;

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
