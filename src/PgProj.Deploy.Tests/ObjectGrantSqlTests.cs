using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-65: the SQL for grants on named objects, column privileges, grant options, <c>FOR ROLE</c>, and role membership, and where
/// those statements sit in a script. The forms that existed before keep the SQL they had (see <see cref="GrantOperationTests"/>).
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ObjectGrantSqlTests
{
    private const string Objects =
        "create schema s; create table s.t (id integer, name text); create sequence s.q; "
        + "create function s.f(x integer, y text[]) returns integer language sql as $$ select x $$; ";

    [TestMethod]
    [DataRow("GRANT SELECT ON s.t TO r;", "GRANT SELECT ON TABLE \"s\".\"t\" TO \"r\";")]
    [DataRow("GRANT SELECT, INSERT ON TABLE s.t TO r1, r2;", "GRANT SELECT, INSERT ON TABLE \"s\".\"t\" TO \"r1\";", "GRANT SELECT, INSERT ON TABLE \"s\".\"t\" TO \"r2\";")]
    [DataRow("GRANT ALL ON s.t TO r;", "GRANT ALL PRIVILEGES ON TABLE \"s\".\"t\" TO \"r\";")]
    [DataRow("GRANT SELECT ON s.t TO PUBLIC;", "GRANT SELECT ON TABLE \"s\".\"t\" TO PUBLIC;")]
    [DataRow("GRANT SELECT ON s.t TO r WITH GRANT OPTION;", "GRANT SELECT ON TABLE \"s\".\"t\" TO \"r\" WITH GRANT OPTION;")]
    [DataRow("GRANT SELECT (id, name) ON s.t TO r;", "GRANT SELECT (\"id\", \"name\") ON TABLE \"s\".\"t\" TO \"r\";")]
    [DataRow("GRANT USAGE ON SEQUENCE s.q TO r;", "GRANT USAGE ON SEQUENCE \"s\".\"q\" TO \"r\";")]
    [DataRow("GRANT ALL ON SEQUENCE s.q TO r;", "GRANT ALL PRIVILEGES ON SEQUENCE \"s\".\"q\" TO \"r\";")]
    [DataRow("GRANT EXECUTE ON FUNCTION s.f(integer, text[]) TO r;", "GRANT ALL PRIVILEGES ON FUNCTION \"s\".\"f\"(pg_catalog.int4, text[]) TO \"r\";")]
    [DataRow("GRANT reader TO alice;", "GRANT \"reader\" TO \"alice\";")]
    [DataRow("GRANT reader TO alice WITH ADMIN OPTION;", "GRANT \"reader\" TO \"alice\" WITH ADMIN OPTION;")]
    [DataRow("ALTER DEFAULT PRIVILEGES FOR ROLE o IN SCHEMA s GRANT SELECT ON TABLES TO r;", "ALTER DEFAULT PRIVILEGES FOR ROLE \"o\" IN SCHEMA \"s\" GRANT SELECT ON TABLES TO \"r\";")]
    [DataRow("ALTER DEFAULT PRIVILEGES IN SCHEMA s GRANT SELECT ON TABLES TO r WITH GRANT OPTION;", "ALTER DEFAULT PRIVILEGES IN SCHEMA \"s\" GRANT SELECT ON TABLES TO \"r\" WITH GRANT OPTION;")]
    [DataRow("ALTER DEFAULT PRIVILEGES GRANT USAGE ON TYPES TO r;", "ALTER DEFAULT PRIVILEGES GRANT ALL PRIVILEGES ON TYPES TO \"r\";")]
    public void GrantIsEmittedWithItsOwnSql(string projectGrant, params string[] expected)
    {
        var statements = Statements(Build(Objects), Build(Objects + projectGrant));

        CollectionAssert.AreEqual(expected, statements.OrderBy(static statement => statement, StringComparer.Ordinal).ToArray(), string.Join("\n", statements));
    }

    [TestMethod]
    public void RevokeIsSpelledFromTheSamePartsAsTheGrant()
    {
        var statements = Statements(
            Build(Objects + "GRANT SELECT (id) ON s.t TO r; GRANT USAGE ON SEQUENCE s.q TO r; GRANT EXECUTE ON FUNCTION s.f(integer, text[]) TO r; GRANT reader TO alice;"
                + "ALTER DEFAULT PRIVILEGES FOR ROLE o IN SCHEMA s GRANT SELECT ON TABLES TO r;"),
            Build(Objects));

        CollectionAssert.AreEquivalent(
            new[]
            {
                "REVOKE SELECT (\"id\") ON TABLE \"s\".\"t\" FROM \"r\";",
                "REVOKE USAGE ON SEQUENCE \"s\".\"q\" FROM \"r\";",
                "REVOKE ALL PRIVILEGES ON FUNCTION \"s\".\"f\"(pg_catalog.int4, text[]) FROM \"r\";",
                "ALTER DEFAULT PRIVILEGES FOR ROLE \"o\" IN SCHEMA \"s\" REVOKE SELECT ON TABLES FROM \"r\";",
            },
            statements.ToArray(),
            string.Join("\n", statements));
    }

    /// <summary>Role membership the project no longer lists is left alone: removing it is a destructive change that is not made.</summary>
    [TestMethod]
    public void RoleMembershipIsNeverRevoked()
    {
        Assert.IsEmpty(Statements(Build(Objects + "GRANT reader TO alice;"), Build(Objects)).ToArray());
    }

    /// <summary>A privilege that changes is a revoke of what live has and a grant of what the project wants, in that order.</summary>
    [TestMethod]
    public void ChangedPrivilegeIsRevokedThenGranted()
    {
        var statements = Statements(Build(Objects + "GRANT SELECT, INSERT ON s.t TO r;"), Build(Objects + "GRANT SELECT ON s.t TO r;"));

        CollectionAssert.AreEqual(
            new[] { "REVOKE SELECT, INSERT ON TABLE \"s\".\"t\" FROM \"r\";", "GRANT SELECT ON TABLE \"s\".\"t\" TO \"r\";" }, // written order (G-112)
            statements.ToArray());
    }

    [TestMethod]
    public void QuotesInNamesAreEscaped()
    {
        var statements = Statements(Build("create schema s; create table s.\"we\"\"ird\" (id integer);"), Build("create schema s; create table s.\"we\"\"ird\" (id integer); GRANT SELECT ON s.\"we\"\"ird\" TO \"a\"\"b\";"));

        CollectionAssert.AreEqual(new[] { "GRANT SELECT ON TABLE \"s\".\"we\"\"ird\" TO \"a\"\"b\";" }, statements.ToArray());
    }

    // ---- ordering ----

    [TestMethod]
    [DataRow("create table s.t (id integer);", "GRANT SELECT ON s.t TO r;", "CREATE TABLE \"s\".\"t\"", "GRANT SELECT ON TABLE")]
    [DataRow("create table s.t (id integer, name text);", "GRANT SELECT (name) ON s.t TO r;", "CREATE TABLE \"s\".\"t\"", "GRANT SELECT (\"name\") ON TABLE")]
    [DataRow("create sequence s.q;", "GRANT USAGE ON SEQUENCE s.q TO r;", "CREATE SEQUENCE \"s\".\"q\"", "GRANT USAGE ON SEQUENCE")]
    [DataRow("create table s.t (id integer); create view s.v as select id from s.t;", "GRANT SELECT ON s.v TO r;", "CREATE OR REPLACE VIEW \"s\".\"v\"", "GRANT SELECT ON TABLE \"s\".\"v\"")]
    [DataRow("create function s.f() returns integer language sql as $$ select 1 $$;", "GRANT EXECUTE ON FUNCTION s.f() TO r;", "CREATE OR REPLACE FUNCTION \"s\".\"f\"", "GRANT ALL PRIVILEGES ON FUNCTION")]
    public void GrantOnANamedObjectIsEmittedAfterTheObjectIsCreated(string objects, string grant, string createPrefix, string grantPrefix)
    {
        var statements = Statements(DatabaseModel.Empty, Build("create schema s; " + objects + " " + grant));

        AssertBefore(statements, createPrefix, grantPrefix);
    }

    [TestMethod]
    [DataRow("create table s.t (id integer);", "GRANT SELECT ON s.t TO r;", "DROP TABLE", "REVOKE SELECT ON TABLE")]
    [DataRow("create sequence s.q;", "GRANT USAGE ON SEQUENCE s.q TO r;", "DROP SEQUENCE", "REVOKE USAGE ON SEQUENCE")]
    public void GrantOnANamedObjectIsRevokedBeforeTheObjectIsDropped(string objects, string grant, string dropPrefix, string revokePrefix)
    {
        var statements = Statements(Build("create schema s; " + objects + " " + grant), Build("create schema s;"));

        AssertBefore(statements, revokePrefix, dropPrefix);
    }

    // ---- what is not modeled must not become SQL ----

    [TestMethod]
    public void ScriptForAProjectWithOnlyUnmodeledGrantsHasNoGrantStatements()
    {
        var statements = Statements(Build(Objects), Build(Objects + "GRANT CONNECT ON DATABASE d TO r; REVOKE SELECT ON s.t FROM r;"));

        Assert.IsEmpty(statements.ToArray());
    }

    [TestMethod]
    public void NoGeneratedGrantOperationIsAPlaceholder()
    {
        var newModel = Build(Objects
            + "GRANT SELECT ON s.t TO r1, r2 WITH GRANT OPTION; GRANT SELECT (id) ON s.t TO r; GRANT USAGE ON SEQUENCE s.q TO r;"
            + "GRANT EXECUTE ON FUNCTION s.f(integer, text[]) TO r; GRANT reader TO alice; ALTER DEFAULT PRIVILEGES FOR ROLE o GRANT USAGE ON SCHEMAS TO r;");

        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(Build(Objects), newModel), new DependencyAnalyzer().Analyze(Build(Objects), newModel));

        Assert.IsEmpty(batch.GetUnsupportedOperations().ToArray());
    }

    private static void AssertBefore(IReadOnlyList<string> statements, string firstPrefix, string secondPrefix)
    {
        var first = statements.Select((statement, index) => (statement, index)).FirstOrDefault(entry => entry.statement.StartsWith(firstPrefix, StringComparison.Ordinal));
        var second = statements.Select((statement, index) => (statement, index)).FirstOrDefault(entry => entry.statement.StartsWith(secondPrefix, StringComparison.Ordinal));

        Assert.IsNotNull(first.statement, $"No statement starts with '{firstPrefix}'.\n{string.Join("\n", statements)}");
        Assert.IsNotNull(second.statement, $"No statement starts with '{secondPrefix}'.\n{string.Join("\n", statements)}");
        Assert.IsLessThan(second.index, first.index, $"'{firstPrefix}' must come before '{secondPrefix}'.\n{string.Join("\n", statements)}");
    }

    private static IReadOnlyList<string> Statements(DatabaseModel oldModel, DatabaseModel newModel)
    {
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));
        return batch.Operations
            .Where(static operation => operation is not SchemaCreateOperation)
            .Select(static operation => operation.GenerateSql().Trim())
            .ToArray();
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
