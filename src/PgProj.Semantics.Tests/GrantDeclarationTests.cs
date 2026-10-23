using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-60: grants are read from the raw SQL text with regular expressions whose privilege list was a lazy <c>.+?</c> that could
/// cross statement boundaries. For the sample project the privileges of <c>GRANT ALL PRIVILEGES ON ALL TABLES ...</c> became
/// pieces of neighbouring statements, which the generator then spliced into the output (an invalid
/// <c>GRANT ALL PRIVILEGES, USAGE ON SCHEMA PUBLIC TO PUBLIC</c>, and <c>ALL PRIVILEGES</c> on tables reduced to <c>USAGE</c>).
/// Text in comments, strings, and routine bodies is not a project grant either.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class GrantDeclarationTests
{
    /// <summary>The sample project's public.sql (the former src/SimpleSqlExample), which is what exposed the problem.</summary>
    private const string SampleGrants = """
        CREATE SCHEMA IF NOT EXISTS public;

        ALTER SCHEMA public OWNER TO pg_database_owner;

        GRANT USAGE ON SCHEMA public TO PUBLIC;
        GRANT USAGE, CREATE ON SCHEMA public TO local_pg_admin;

        GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO local_pg_admin;
        GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public TO local_pg_admin;
        GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA public TO local_pg_admin;

        ALTER DEFAULT PRIVILEGES IN SCHEMA public
        	GRANT ALL ON TABLES TO local_pg_admin;

        ALTER DEFAULT PRIVILEGES IN SCHEMA public
        	GRANT ALL ON SEQUENCES TO local_pg_admin;

        ALTER DEFAULT PRIVILEGES IN SCHEMA public
        	GRANT EXECUTE ON FUNCTIONS TO local_pg_admin;
        """;

    private static readonly string[] SampleExpected =
    [
        "all functions in schema:public->local_pg_admin = ALL PRIVILEGES",
        "all sequences in schema:public->local_pg_admin = ALL PRIVILEGES",
        "all tables in schema:public->local_pg_admin = ALL PRIVILEGES",
        "default functions in schema:public->local_pg_admin = ALL PRIVILEGES",
        "default sequences in schema:public->local_pg_admin = ALL PRIVILEGES",
        "default tables in schema:public->local_pg_admin = ALL PRIVILEGES",
        "schema:public->PUBLIC = USAGE",
        "schema:public->local_pg_admin = ALL PRIVILEGES",
    ];

    [TestMethod]
    public void SampleProjectGrantsHaveExactlyTheirOwnPrivileges()
    {
        CollectionAssert.AreEqual(SampleExpected, Describe(SampleGrants));
    }

    /// <summary>The result must not depend on which statement comes first in the file.</summary>
    [TestMethod]
    public void GrantsDoNotDependOnStatementOrder()
    {
        var statements = SampleGrants.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static statement => !statement.StartsWith("CREATE SCHEMA", StringComparison.OrdinalIgnoreCase) && !statement.StartsWith("ALTER SCHEMA", StringComparison.OrdinalIgnoreCase))
            .Select(static statement => statement + ";")
            .ToArray();

        var reversed = string.Join("\n", statements.Reverse());
        var rotated = string.Join("\n", statements.Skip(3).Concat(statements.Take(3)));

        CollectionAssert.AreEqual(SampleExpected, Describe(reversed));
        CollectionAssert.AreEqual(SampleExpected, Describe(rotated));
    }

    [TestMethod]
    public void NoPrivilegeContainsStatementText()
    {
        var model = Build(SampleGrants);

        foreach (var privilege in model.Grants.Values.SelectMany(static grant => grant.Privileges))
        {
            Assert.IsFalse(privilege.Contains(';', StringComparison.Ordinal) || privilege.Contains("GRANT", StringComparison.OrdinalIgnoreCase) || privilege.Contains(" ON ", StringComparison.OrdinalIgnoreCase), $"Privilege '{privilege}' is statement text.");
        }
    }

    [TestMethod]
    public void GrantsAcrossSeveralStatementsOfDifferentKindsStayApart()
    {
        var actual = Describe("GRANT SELECT ON ALL TABLES IN SCHEMA s TO r1; GRANT USAGE ON SCHEMA s TO r2; GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA s TO r3;");

        CollectionAssert.AreEqual(
            new[]
            {
                "all functions in schema:s->r3 = ALL PRIVILEGES",
                "all tables in schema:s->r1 = SELECT",
                "schema:s->r2 = USAGE",
            },
            actual);
    }

    [TestMethod]
    [DataRow("GRANT ALL ON SCHEMA s TO r;", "ALL PRIVILEGES")]
    [DataRow("GRANT all privileges ON SCHEMA s TO r;", "ALL PRIVILEGES")]
    [DataRow("grant create, usage on schema s to r;", "ALL PRIVILEGES")]
    [DataRow("GRANT USAGE,CREATE ON SCHEMA s TO r;", "ALL PRIVILEGES")]
    [DataRow("GRANT CREATE ON SCHEMA s TO r;", "CREATE")]
    [DataRow("GRANT USAGE ON SCHEMA s TO r;", "USAGE")]
    public void PrivilegesAreNormalized(string sql, string expectedPrivileges)
    {
        Assert.AreEqual($"schema:s->r = {expectedPrivileges}", Describe(sql).Single());
    }

    /// <summary>Text that merely looks like a grant is not one.</summary>
    [TestMethod]
    [DataRow("-- GRANT ALL ON SCHEMA s TO r;\nCREATE TABLE s.t (id integer);")]
    [DataRow("/* GRANT ALL ON SCHEMA s TO r; */ CREATE TABLE s.t (id integer);")]
    [DataRow("CREATE TABLE s.t (note text DEFAULT 'GRANT ALL ON SCHEMA s TO r;');")]
    [DataRow("CREATE FUNCTION s.f() RETURNS void LANGUAGE plpgsql AS $$ BEGIN EXECUTE 'GRANT ALL ON ALL TABLES IN SCHEMA s TO r;'; END $$;")]
    [DataRow("CREATE FUNCTION s.f() RETURNS void LANGUAGE plpgsql AS $body$ BEGIN\n-- GRANT USAGE ON SCHEMA s TO r;\nEND $body$;")]
    public void GrantLookalikesInCommentsStringsAndBodiesAreIgnored(string sql)
    {
        Assert.IsEmpty(Build(sql).Grants, string.Join("; ", Build(sql).Grants.Keys));
    }

    [TestMethod]
    public void RealGrantNextToALookalikeIsStillDeclared()
    {
        var actual = Describe("-- GRANT ALL ON SCHEMA s TO nobody;\nGRANT USAGE ON SCHEMA s TO r; /* GRANT CREATE ON SCHEMA s TO other; */");

        CollectionAssert.AreEqual(new[] { "schema:s->r = USAGE" }, actual);
    }

    [TestMethod]
    public void QuotedRoleAndSchemaNamesAreKeptWithTheirCase()
    {
        var actual = Describe("GRANT USAGE ON SCHEMA \"My Schema\" TO \"App Role\";");

        CollectionAssert.AreEqual(new[] { "schema:My Schema->App Role = USAGE" }, actual);
    }

    /// <summary>G-87: excludeGrants/excludeRoleMemberships must drop the excluded statement with no model entry and no diagnostic at all.</summary>
    [TestMethod]
    public void ExcludeGrantsDropsObjectGrantsButKeepsRoleMembership()
    {
        var model = Build("GRANT SELECT ON s.t TO reader; GRANT admin TO app_user;", excludeGrants: true);

        CollectionAssert.AreEqual(new[] { "role:admin->app_user = MEMBER" }, Describe(model));
        Assert.IsEmpty(model.Diagnostics);
    }

    [TestMethod]
    public void ExcludeRoleMembershipsDropsRoleMembershipButKeepsObjectGrants()
    {
        var model = Build("GRANT SELECT ON s.t TO reader; GRANT admin TO app_user;", excludeRoleMemberships: true);

        CollectionAssert.AreEqual(new[] { "table:s.t->reader = SELECT" }, Describe(model));
        Assert.IsEmpty(model.Diagnostics);
    }

    [TestMethod]
    public void ExcludingBothLeavesNoGrantsAndNoDiagnostics()
    {
        var model = Build("GRANT SELECT ON s.t TO reader; GRANT admin TO app_user;", excludeGrants: true, excludeRoleMemberships: true);

        Assert.IsEmpty(model.Grants);
        Assert.IsEmpty(model.Diagnostics);
    }

    /// <summary>An excluded statement produces no diagnostic either — not even the ones a kept statement of the same shape would (REVOKE, an unmodeled form, an unsupported part).</summary>
    [TestMethod]
    public void ExcludedStatementsProduceNoDiagnosticsEvenWhenTheyWouldOtherwiseWarn()
    {
        var model = Build("REVOKE SELECT ON s.t FROM reader; GRANT SELECT ON DATABASE d TO reader;", excludeGrants: true);

        Assert.IsEmpty(model.Diagnostics);
    }

    private static string[] Describe(DatabaseModel model)
        => model.Grants.Values
            .Select(static grant => $"{grant.ObjectKey}->{grant.Grantee} = {string.Join(", ", grant.Privileges)}")
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string[] Describe(string sql)
        => Describe(Build(sql));

    private static DatabaseModel Build(string sql, bool excludeGrants = false, bool excludeRoleMemberships = false)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)], excludeGrants: excludeGrants, excludeRoleMemberships: excludeRoleMemberships);
}
