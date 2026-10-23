namespace PgProj.Core.Tests;

/// <summary>
/// Regression coverage for G-82: <see cref="LiveDatabaseModelLoader.BuildScaffoldFiles"/> must group a live
/// database into one file per object, following the layout `samples/MultiSchema` (this repository's own hand-authored sample
/// project) already uses, and must never drop a grant even when its exact object cannot be matched.
/// </summary>
[TestClass]
public sealed class ScaffoldTests
{
    [TestMethod]
    public void TableGetsItsOwnFileWithConstraintsIndexesAndTriggersTogether()
    {
        var files = BuildScaffoldFiles(
            tables: [new LiveTableSnapshot("public", "accounts", [new LiveColumnSnapshot("id", "uuid", false, null, false, null, null)])],
            constraints: [new LiveConstraintSnapshot("public", "accounts", "accounts_pkey", "PRIMARY KEY (id)")],
            indexes: [new LiveIndexSnapshot("public", "accounts", "accounts_name_idx", "CREATE INDEX accounts_name_idx ON public.accounts USING btree (name)")],
            triggers: [new LiveTriggerSnapshot("public", "accounts", "accounts_audit_trg", "CREATE TRIGGER accounts_audit_trg BEFORE INSERT ON public.accounts FOR EACH ROW EXECUTE FUNCTION public.audit()", "public", "audit")]);

        var tableFile = files.Single(static file => file.RelativePath == "Schema/public/Table/accounts.sql");
        StringAssert.Contains(tableFile.Content, "CREATE TABLE");
        StringAssert.Contains(tableFile.Content, "ADD CONSTRAINT \"accounts_pkey\"");
        StringAssert.Contains(tableFile.Content, "CREATE INDEX accounts_name_idx");
        StringAssert.Contains(tableFile.Content, "CREATE TRIGGER accounts_audit_trg");
    }

    /// <summary>G-203: a trigger on a view was left out of the scaffold, so the scaffold's first publish dropped it. It goes in the view's file, after the view.</summary>
    [TestMethod]
    public void ATriggerOnAViewGoesInTheViewsFileAfterTheView()
    {
        var files = BuildScaffoldFiles(
            tables: [new LiveTableSnapshot("public", "t", [new LiveColumnSnapshot("id", "integer", true, null, false, null, null)])],
            views: [new LiveViewSnapshot("public", "v", "SELECT id FROM public.t")],
            triggers:
            [
                new LiveTriggerSnapshot("public", "v", "v_insert", "CREATE TRIGGER v_insert INSTEAD OF INSERT ON public.v FOR EACH ROW EXECUTE FUNCTION public.f()", "public", "f", "routes inserts"),
                new LiveTriggerSnapshot("public", "t", "t_audit", "CREATE TRIGGER t_audit AFTER INSERT ON public.t FOR EACH STATEMENT EXECUTE FUNCTION public.f()", "public", "f"),
            ]);

        var viewFile = files.Single(static file => file.RelativePath == "Schema/public/View/v.sql").Content;
        Assert.IsTrue(viewFile.IndexOf("CREATE OR REPLACE VIEW", StringComparison.Ordinal) < viewFile.IndexOf("CREATE TRIGGER v_insert", StringComparison.Ordinal), viewFile);
        StringAssert.Contains(viewFile, "COMMENT ON TRIGGER \"v_insert\" ON public.v IS 'routes inserts';");
        StringAssert.Contains(files.Single(static file => file.RelativePath == "Schema/public/Table/t.sql").Content, "CREATE TRIGGER t_audit");
        Assert.IsFalse(viewFile.Contains("t_audit", StringComparison.Ordinal));
    }

    /// <summary>
    /// G-212: two tables whose names differ only by case (G-206) get files of those names when the target file system tells them apart, and the second
    /// gets a <c>~2</c> suffix when it does not, so neither overwrites the other.
    /// </summary>
    [TestMethod]
    [DataRow(true, "Schema/public/Table/T.sql", "Schema/public/Table/t.sql")]
    [DataRow(false, "Schema/public/Table/T.sql", "Schema/public/Table/t~2.sql")]
    public void NamesDifferingOnlyByCaseGetFilesAsTheFileSystemAllows(bool caseSensitive, string first, string second)
    {
        var files = LiveDatabaseModelLoader.BuildScaffoldFiles(
            [], [new LiveTableSnapshot("public", "T", [new LiveColumnSnapshot("a", "integer", true, null, false, null, null)]), new LiveTableSnapshot("public", "t", [new LiveColumnSnapshot("b", "integer", true, null, false, null, null)])],
            [], [], [], [], [], [], [], [], [], caseSensitivePaths: caseSensitive);

        StringAssert.Contains(files.Single(file => file.RelativePath == first).Content, "CREATE TABLE public.\"T\"");
        StringAssert.Contains(files.Single(file => file.RelativePath == second).Content, "CREATE TABLE public.t ");
    }

    [TestMethod]
    public void AMaterializedViewGetsItsIndexesInItsOwnFileAfterTheView()
    {
        var files = BuildScaffoldFiles(
            tables: [new LiveTableSnapshot("reporting", "orders", [new LiveColumnSnapshot("id", "uuid", false, null, false, null, null)])],
            indexes:
            [
                new LiveIndexSnapshot("reporting", "orders", "orders_id_idx", "CREATE INDEX orders_id_idx ON reporting.orders USING btree (id)"),
                new LiveIndexSnapshot("reporting", "mv_daily", "mv_daily_day", "CREATE UNIQUE INDEX mv_daily_day ON reporting.mv_daily USING btree (day)", "the day"),
            ],
            views: [new LiveViewSnapshot("reporting", "mv_daily", "SELECT day FROM reporting.orders", IsMaterialized: true)]);

        var viewFile = files.Single(static file => file.RelativePath == "Schema/reporting/View/mv_daily.sql");
        StringAssert.Contains(viewFile.Content, "CREATE MATERIALIZED VIEW");
        StringAssert.Contains(viewFile.Content, "CREATE UNIQUE INDEX mv_daily_day ON reporting.mv_daily");
        StringAssert.Contains(viewFile.Content, "COMMENT ON INDEX reporting.mv_daily_day IS 'the day'");
        Assert.IsLessThan(viewFile.Content.IndexOf("CREATE UNIQUE INDEX", StringComparison.Ordinal), viewFile.Content.IndexOf("CREATE MATERIALIZED VIEW", StringComparison.Ordinal));
        Assert.DoesNotContain("mv_daily_day", files.Single(static file => file.RelativePath == "Schema/reporting/Table/orders.sql").Content, StringComparison.Ordinal);
        StringAssert.Contains(files.Single(static file => file.RelativePath == "Schema/reporting/Table/orders.sql").Content, "orders_id_idx");
    }

    [TestMethod]
    public void ViewFunctionSequenceAndTypeEachGetTheirOwnFile()
    {
        var files = BuildScaffoldFiles(
            views: [new LiveViewSnapshot("public", "account_names", "SELECT name FROM public.accounts")],
            functions: [new LiveFunctionSnapshot("public", "get_count", "CREATE OR REPLACE FUNCTION public.get_count() RETURNS integer LANGUAGE sql AS $$ SELECT 1; $$")],
            sequences: [new LiveSequenceSnapshot("public", "accounts_seq", "bigint", "1", "1", "9223372036854775807", "1", false, "1", null, null, null)],
            types: [new LiveTypeSnapshot("public", "account_status", "user-defined", "CREATE TYPE public.account_status AS ENUM ('active', 'inactive')")]);

        CollectionAssert.Contains(files.Select(static f => f.RelativePath).ToList(), "Schema/public/View/account_names.sql");
        CollectionAssert.Contains(files.Select(static f => f.RelativePath).ToList(), "Schema/public/Function/get_count.sql");
        CollectionAssert.Contains(files.Select(static f => f.RelativePath).ToList(), "Schema/public/Sequence/accounts_seq.sql");
        CollectionAssert.Contains(files.Select(static f => f.RelativePath).ToList(), "Schema/public/Type/account_status.sql");
    }

    [TestMethod]
    public void ProcedureGoesUnderItsOwnFolderNotFunction()
    {
        var files = BuildScaffoldFiles(
            functions: [new LiveFunctionSnapshot("public", "do_thing", "CREATE OR REPLACE PROCEDURE public.do_thing() LANGUAGE sql AS $$ SELECT 1; $$", IsProcedure: true)]);

        CollectionAssert.Contains(files.Select(static f => f.RelativePath).ToList(), "Schema/public/Procedure/do_thing.sql");
    }

    [TestMethod]
    public void ExtensionGetsATopLevelFileNotASchemaFolder()
    {
        var files = BuildScaffoldFiles(types: [new LiveTypeSnapshot("public", "citext", "extension", "CREATE EXTENSION citext SCHEMA public")]);

        CollectionAssert.Contains(files.Select(static f => f.RelativePath).ToList(), "Extension/citext.sql");
    }

    [TestMethod]
    public void EverySchemaGetsAFileEvenWithNoObjects()
    {
        var files = BuildScaffoldFiles(schemas: [new LiveSchemaSnapshot("public"), new LiveSchemaSnapshot("app")]);

        var publicFile = files.Single(static f => f.RelativePath == "Schema/public/public.sql");
        StringAssert.Contains(publicFile.Content, "CREATE SCHEMA IF NOT EXISTS public;");
        var appFile = files.Single(static f => f.RelativePath == "Schema/app/app.sql");
        StringAssert.Contains(appFile.Content, "CREATE SCHEMA IF NOT EXISTS app;");
    }

    [TestMethod]
    public void TableGrantLandsInTheTableFileNotTheSchemaFile()
    {
        var files = BuildScaffoldFiles(
            schemas: [new LiveSchemaSnapshot("public")],
            tables: [new LiveTableSnapshot("public", "accounts", [new LiveColumnSnapshot("id", "uuid", false, null, false, null, null)])],
            grants: [new LiveGrantSnapshot("public", "table:public.accounts()", "app_user", "GRANT SELECT ON TABLE public.accounts TO app_user")]);

        var tableFile = files.Single(static f => f.RelativePath == "Schema/public/Table/accounts.sql");
        StringAssert.Contains(tableFile.Content, "GRANT SELECT ON TABLE public.accounts TO app_user");
        var schemaFile = files.Single(static f => f.RelativePath == "Schema/public/public.sql");
        Assert.IsFalse(schemaFile.Content.Contains("GRANT SELECT", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SchemaScopedGrantLandsInTheSchemaFile()
    {
        var files = BuildScaffoldFiles(
            schemas: [new LiveSchemaSnapshot("public")],
            grants: [new LiveGrantSnapshot("public", "all tables in schema:public", "local_pg_admin", "GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO local_pg_admin")]);

        var schemaFile = files.Single(static f => f.RelativePath == "Schema/public/public.sql");
        StringAssert.Contains(schemaFile.Content, "GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO local_pg_admin");
    }

    [TestMethod]
    public void RoleMembershipGrantLandsInATopLevelFile()
    {
        var files = BuildScaffoldFiles(grants: [new LiveGrantSnapshot("", "role:app_role", "app_user", "GRANT app_role TO app_user")]);

        var roleFile = files.Single(static f => f.RelativePath == "Role/role-memberships.sql");
        StringAssert.Contains(roleFile.Content, "GRANT app_role TO app_user");
    }

    [TestMethod]
    public void OverloadedFunctionsGetDistinctFiles()
    {
        var files = BuildScaffoldFiles(functions:
        [
            new LiveFunctionSnapshot("public", "total", "CREATE OR REPLACE FUNCTION public.total(a integer) RETURNS integer LANGUAGE sql AS $$ SELECT a; $$", IdentityArguments: "integer"),
            new LiveFunctionSnapshot("public", "total", "CREATE OR REPLACE FUNCTION public.total(a text) RETURNS integer LANGUAGE sql AS $$ SELECT 1; $$", IdentityArguments: "text"),
        ]);

        var paths = files.Select(static f => f.RelativePath).Where(static path => path.Contains("Function/total", StringComparison.Ordinal)).ToArray();
        Assert.HasCount(2, paths);
        CollectionAssert.Contains(paths, "Schema/public/Function/total.sql");
        CollectionAssert.Contains(paths, "Schema/public/Function/total~2.sql");
    }

    [TestMethod]
    public void EmptyDatabaseProducesNoFiles()
    {
        Assert.IsEmpty(BuildScaffoldFiles());
    }

    /// <summary>G-87: pgproj init --exclude-grants/--exclude-role-memberships filters what LoadForScaffoldAsync scaffolds, independently for each category.</summary>
    [TestMethod]
    public void FilterGrantsForScaffoldExcludesEachCategoryIndependently()
    {
        IReadOnlyList<LiveGrantSnapshot> grants =
        [
            new LiveGrantSnapshot("public", "table:public.accounts()", "app_user", "GRANT SELECT ON TABLE public.accounts TO app_user"),
            new LiveGrantSnapshot("", "role:app_role", "app_user", "GRANT app_role TO app_user"),
        ];

        Assert.HasCount(2, LiveDatabaseModelLoader.FilterGrantsForScaffold(grants, excludeGrants: false, excludeRoleMemberships: false));

        var grantsOnly = LiveDatabaseModelLoader.FilterGrantsForScaffold(grants, excludeGrants: false, excludeRoleMemberships: true);
        Assert.HasCount(1, grantsOnly);
        Assert.AreEqual("table:public.accounts()", grantsOnly[0].ObjectKey);

        var roleMembershipOnly = LiveDatabaseModelLoader.FilterGrantsForScaffold(grants, excludeGrants: true, excludeRoleMemberships: false);
        Assert.HasCount(1, roleMembershipOnly);
        Assert.AreEqual("role:app_role", roleMembershipOnly[0].ObjectKey);

        Assert.IsEmpty(LiveDatabaseModelLoader.FilterGrantsForScaffold(grants, excludeGrants: true, excludeRoleMemberships: true));
    }

    private static IReadOnlyList<ScaffoldedFile> BuildScaffoldFiles(
        IReadOnlyList<LiveSchemaSnapshot>? schemas = null,
        IReadOnlyList<LiveTableSnapshot>? tables = null,
        IReadOnlyList<LiveConstraintSnapshot>? constraints = null,
        IReadOnlyList<LiveIndexSnapshot>? indexes = null,
        IReadOnlyList<LiveViewSnapshot>? views = null,
        IReadOnlyList<LiveFunctionSnapshot>? functions = null,
        IReadOnlyList<LiveTypeSnapshot>? types = null,
        IReadOnlyList<LiveGrantSnapshot>? grants = null,
        IReadOnlyList<LiveSequenceSnapshot>? sequences = null,
        IReadOnlyList<LiveTriggerSnapshot>? triggers = null,
        IReadOnlyList<LivePolicySnapshot>? policies = null)
        => LiveDatabaseModelLoader.BuildScaffoldFiles(
            schemas ?? [],
            tables ?? [],
            constraints ?? [],
            indexes ?? [],
            views ?? [],
            functions ?? [],
            types ?? [],
            grants ?? [],
            sequences ?? [],
            triggers ?? [],
            policies ?? []);
}
