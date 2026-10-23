using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-104: <c>CREATE OR REPLACE VIEW</c> can only add output columns at the end. A view whose columns are removed, reordered, renamed, or
/// retyped is dropped and created instead (with the views built on it), which PostgreSQL's own column lists for the live view and the
/// rendered project view tell apart. Adding a column at the end, or not knowing the lists, keeps the replace.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ViewOutputColumnChangeTests
{
    private const string Table = "CREATE SCHEMA s; CREATE TABLE s.c (id int PRIMARY KEY, name text NOT NULL, score int);";
    private const string OnTop = " CREATE VIEW s.w AS SELECT name FROM s.v;";

    [TestMethod]
    [DataRow("SELECT c.name FROM s.c c", new[] { "name text" }, DisplayName = "a column removed")]
    [DataRow("SELECT c.name, c.id FROM s.c c", new[] { "name text", "id integer" }, DisplayName = "columns reordered")]
    [DataRow("SELECT c.name AS id, c.id AS name FROM s.c c", new[] { "id text", "name integer" }, DisplayName = "two columns trade names")]
    [DataRow("SELECT c.id::bigint AS id, c.name FROM s.c c", new[] { "id bigint", "name text" }, DisplayName = "a column retyped")]
    public void AViewReshapedIsDroppedAndCreatedWithTheViewsOnIt(string query, string[] newColumns)
    {
        var statements = Statements(query, newColumns);

        var dropW = statements.IndexOf("DROP VIEW \"s\".\"w\";");
        var dropV = statements.IndexOf("DROP VIEW \"s\".\"v\";");
        var createV = statements.FindIndex(static statement => statement.StartsWith("CREATE OR REPLACE VIEW \"s\".\"v\"", StringComparison.Ordinal));
        var createW = statements.FindIndex(static statement => statement.StartsWith("CREATE OR REPLACE VIEW \"s\".\"w\"", StringComparison.Ordinal));
        Assert.IsTrue(dropW >= 0 && dropW < dropV && dropV < createV && createV < createW, string.Join("\n", statements));
    }

    /// <summary>
    /// Replaces G-104's "a column renamed" case, which expected a drop and a create: by the user's approval of G-276 (2026-10-13) a view whose columns are
    /// only renamed is renamed with <c>ALTER VIEW ... RENAME COLUMN</c> and replaced in place, so the views built on it stay.
    /// </summary>
    [TestMethod]
    public void AColumnRenamedIsRenamedAndReplacedInPlace()
    {
        var statements = Statements("SELECT c.id AS key, c.name FROM s.c c", ["key integer", "name text"]);

        Assert.IsFalse(statements.Any(static statement => statement.StartsWith("DROP", StringComparison.Ordinal)), string.Join("\n", statements));
        var view = statements.Single(static statement => statement.Contains("CREATE OR REPLACE VIEW \"s\".\"v\"", StringComparison.Ordinal));
        StringAssert.StartsWith(view, "ALTER VIEW \"s\".\"v\" RENAME COLUMN id TO key;\nCREATE OR REPLACE VIEW \"s\".\"v\"");
    }

    [TestMethod]
    public void AColumnAddedAtTheEndIsStillAReplace()
    {
        var statements = Statements("SELECT c.id, c.name, c.score FROM s.c c", ["id integer", "name text", "score integer"]);

        Assert.IsFalse(statements.Any(static statement => statement.StartsWith("DROP", StringComparison.Ordinal)), string.Join("\n", statements));
        Assert.IsTrue(statements.Any(static statement => statement.StartsWith("CREATE OR REPLACE VIEW \"s\".\"v\"", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void UnknownColumnsKeepTheReplace()
    {
        // The project view could not be rendered on the server (no TEMPORARY privilege), so its columns are not known.
        var statements = Statements("SELECT c.name FROM s.c c", []);

        Assert.IsFalse(statements.Any(static statement => statement.StartsWith("DROP", StringComparison.Ordinal)), string.Join("\n", statements));
    }

    private static List<string> Statements(string newQuery, string[] newColumns)
    {
        var live = Build(Table + " CREATE VIEW s.v AS SELECT c.id, c.name FROM s.c c;" + OnTop);
        live.Views = WithColumns(live.Views, ["id integer", "name text"]);
        live.LiveDependencies = [new LiveDependency("view", "s.w", "s.v", "name")];
        var project = Build(Table + $" CREATE VIEW s.v AS {newQuery};" + OnTop);
        project.Views = WithColumns(project.Views, newColumns);

        return new ScriptGenerator().Generate(new DiffEngine().Diff(live, project), new DependencyAnalyzer().Analyze(live, project)).Operations
            .Select(static operation => operation.GenerateSql().Trim().Replace("\r\n", "\n", StringComparison.Ordinal))
            .ToList();
    }

    private static Dictionary<string, ViewDefinition> WithColumns(IReadOnlyDictionary<string, ViewDefinition> views, IReadOnlyList<string> columns)
        => views.ToDictionary(static pair => pair.Key, pair => pair.Key == "s.v" ? pair.Value with { ServerOutputColumns = columns } : pair.Value, StringComparer.OrdinalIgnoreCase);

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
