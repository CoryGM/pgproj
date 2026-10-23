using PgProj.Core;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core.Tests;

/// <summary>
/// G-65: the live loader reports what PostgreSQL recorded as <c>GRANT</c> text, which then goes through the same parser as a
/// project's SQL. These are the statements the loader renders for each kind of privilege it reads; each must come back as the
/// grant it stands for. (Whether the catalog queries behind them are right is checked against a real server by the integration
/// tests.)
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class LiveGrantSnapshotTests
{
    [TestMethod]
    [DataRow("public", "table:public.t", "r", "GRANT SELECT, UPDATE ON TABLE public.t TO r", "table:public.t", "SELECT, UPDATE", false)]
    [DataRow("public", "table:public.t", "r", "GRANT SELECT ON TABLE public.t TO r WITH GRANT OPTION", "table:public.t", "SELECT", true)]
    [DataRow("Sales", "table:Sales.Order Lines", "App Role", "GRANT SELECT ON TABLE \"Sales\".\"Order Lines\" TO \"App Role\"", "table:Sales.Order Lines", "SELECT", false)]
    [DataRow("public", "sequence:public.q()", "r", "GRANT ALL PRIVILEGES ON SEQUENCE public.q TO r", "sequence:public.q", "ALL PRIVILEGES", false)]
    [DataRow("public", "function:public.f(integer, text[])", "r", "GRANT ALL PRIVILEGES ON FUNCTION public.f(integer, text[]) TO r", "function:public.f(int4,text[])", "ALL PRIVILEGES", false)]
    [DataRow("public", "function:public.f(character varying)", "r", "GRANT ALL PRIVILEGES ON FUNCTION public.f(character varying) TO PUBLIC", "function:public.f(varchar)", "ALL PRIVILEGES", false)]
    [DataRow("public", "procedure:public.p(integer)", "r", "GRANT ALL PRIVILEGES ON PROCEDURE public.p(integer) TO r", "procedure:public.p(int4)", "ALL PRIVILEGES", false)]
    [DataRow("public", "function:public.f()", "r", "GRANT ALL PRIVILEGES ON FUNCTION public.f() TO r", "function:public.f()", "ALL PRIVILEGES", false)]
    public void ObjectGrantRenderingReadsBackAsTheGrant(string schema, string snapshotKey, string grantee, string statement, string expectedObjectKey, string expectedPrivileges, bool expectedGrantOption)
    {
        var grant = Load(new LiveGrantSnapshot(schema, snapshotKey, grantee, statement)).Single();

        Assert.AreEqual(expectedObjectKey, grant.ObjectKey);
        Assert.AreEqual(expectedPrivileges, string.Join(", ", grant.Privileges));
        Assert.AreEqual(expectedGrantOption, grant.WithGrantOption);
    }

    [TestMethod]
    public void ColumnGrantRenderingReadsBackWithItsColumns()
    {
        var grant = Load(new LiveGrantSnapshot("public", "table:public.t", "r", "GRANT UPDATE (a, \"B c\") ON TABLE public.t TO r")).Single();

        Assert.AreEqual("table:public.t", grant.ObjectKey);
        // In the order the statement lists them (G-112: a publish keeps the written order; the grant's key sorts them).
        CollectionAssert.AreEqual(new[] { "a", "B c" }, grant.Columns.ToArray());
        CollectionAssert.AreEqual(new[] { "UPDATE" }, grant.Privileges.ToArray());
    }

    [TestMethod]
    public void RoleMembershipRenderingReadsBackWithItsAdminOption()
    {
        var plain = Load(new LiveGrantSnapshot(string.Empty, "role:reader", "alice", "GRANT reader TO alice")).Single();
        var admin = Load(new LiveGrantSnapshot(string.Empty, "role:\"Reader Role\"", "alice", "GRANT \"Reader Role\" TO alice WITH ADMIN OPTION")).Single();

        Assert.AreEqual("role:reader", plain.ObjectKey);
        Assert.IsFalse(plain.WithGrantOption);
        Assert.AreEqual("role:Reader Role", admin.ObjectKey);
        Assert.IsTrue(admin.WithGrantOption);
    }

    [TestMethod]
    [DataRow("ALTER DEFAULT PRIVILEGES FOR ROLE owner IN SCHEMA s GRANT SELECT ON TABLES TO r", "default tables in schema:s", "owner", "SELECT")]
    [DataRow("ALTER DEFAULT PRIVILEGES IN SCHEMA s GRANT ALL PRIVILEGES ON TYPES TO r", "default types in schema:s", null, "ALL PRIVILEGES")]
    [DataRow("ALTER DEFAULT PRIVILEGES GRANT USAGE ON SCHEMAS TO r", "default schemas", null, "USAGE")]
    public void DefaultPrivilegesRenderingReadsBackWithItsRole(string statement, string expectedObjectKey, string? expectedRole, string expectedPrivileges)
    {
        var grant = Load(new LiveGrantSnapshot(expectedObjectKey.Contains("schema:s", StringComparison.Ordinal) ? "s" : string.Empty, expectedObjectKey, "r", statement)).Single();

        Assert.AreEqual(expectedObjectKey, grant.ObjectKey);
        Assert.AreEqual(expectedRole, grant.ForRole);
        Assert.AreEqual(expectedPrivileges, string.Join(", ", grant.Privileges));
    }

    /// <summary>The snapshot script must not depend on the order the catalog returned rows in.</summary>
    [TestMethod]
    public void SnapshotScriptOrdersGrantsDeterministically()
    {
        LiveGrantSnapshot[] grants =
        [
            new("s", "table:s.t", "b", "GRANT SELECT ON TABLE s.t TO b"),
            new("s", "table:s.t", "a", "GRANT UPDATE ON TABLE s.t TO a"),
            new("s", "table:s.t", "a", "GRANT INSERT ON TABLE s.t TO a"),
        ];

        var forward = LiveDatabaseModelLoader.GenerateSnapshotSql([new LiveSchemaSnapshot("s")], [], [], [], grants: grants);
        var backward = LiveDatabaseModelLoader.GenerateSnapshotSql([new LiveSchemaSnapshot("s")], [], [], [], grants: grants.Reverse().ToArray());

        Assert.AreEqual(forward, backward);
    }

    private static IReadOnlyList<GrantDefinition> Load(LiveGrantSnapshot snapshot)
    {
        var loader = new LiveDatabaseModelLoader(new PgQueryParser(), new SemanticBuilder());
        var sql = LiveDatabaseModelLoader.GenerateSnapshotSql([new LiveSchemaSnapshot("public")], [], [], [], grants: [snapshot]);
        return loader.BuildModelFromSnapshotSql(sql).Grants.Values.ToArray();
    }
}
