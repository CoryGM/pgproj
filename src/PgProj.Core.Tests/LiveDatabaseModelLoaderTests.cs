using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core.Tests;

[TestClass]
public sealed class LiveDatabaseModelLoaderTests
{
    [TestMethod]
    public void BuildModelFromSnapshotSql_MapsTypesGrantsTablesViewsFunctionsConstraintsAndIndexes()
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
                        new LiveColumnSnapshot("id", "uuid", false, null, false, null, null),
                        new LiveColumnSnapshot("name", "text", false, null, false, null, null)
                    ])
            ],
            [
                new LiveConstraintSnapshot("public", "accounts", "accounts_pkey", "PRIMARY KEY (id)"),
                new LiveConstraintSnapshot("public", "accounts", "accounts_name_key", "UNIQUE (name)")
            ],
            [
                new LiveIndexSnapshot("public", "accounts", "accounts_name_idx", "CREATE INDEX accounts_name_idx ON public.accounts USING btree (name)")
            ],
            [
                new LiveViewSnapshot("public", "account_names", "SELECT accounts.name FROM public.accounts")
            ],
            [
                new LiveFunctionSnapshot("public", "get_account_count", "CREATE OR REPLACE FUNCTION public.get_account_count() RETURNS integer LANGUAGE sql AS $$ SELECT count(*)::integer FROM public.accounts; $$")
            ],
            [
                new LiveTypeSnapshot("public", "citext", "extension", "CREATE EXTENSION citext SCHEMA public"),
                new LiveTypeSnapshot("public", "account_status", "user-defined", "CREATE TYPE public.account_status AS ENUM ('active', 'inactive')"),
                new LiveTypeSnapshot("public", "account_point", "user-defined", "CREATE TYPE public.account_point AS (x integer, y integer)"),
                new LiveTypeSnapshot("public", "account_period", "user-defined", "CREATE TYPE public.account_period AS RANGE (SUBTYPE = timestamp without time zone)")
            ],
            [
                new LiveGrantSnapshot("public", "schema:public", "PUBLIC", "GRANT USAGE ON SCHEMA public TO PUBLIC"),
                new LiveGrantSnapshot("public", "all tables in schema:public", "local_pg_admin", "GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO local_pg_admin"),
                new LiveGrantSnapshot("public", "default functions in schema:public", "local_pg_admin", "ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT EXECUTE ON FUNCTIONS TO local_pg_admin")
            ]);

        var model = CreateLoader().BuildModelFromSnapshotSql(snapshotSql);

        Assert.IsTrue(
            model.Tables.ContainsKey("public.accounts"),
            string.Join(Environment.NewLine, model.Tables.Keys.Concat(model.Diagnostics.Select(static diagnostic => diagnostic.Message))));
        Assert.HasCount(2, model.Tables["public.accounts"].Columns);
        CollectionAssert.Contains(model.Tables["public.accounts"].ConstraintKeys.ToList(), "public.accounts.accounts_pkey");
        CollectionAssert.Contains(model.Tables["public.accounts"].ConstraintKeys.ToList(), "public.accounts.accounts_name_key");
        Assert.IsTrue(model.Constraints.ContainsKey("public.accounts.accounts_pkey"));
        Assert.IsTrue(model.Constraints.ContainsKey("public.accounts.accounts_name_key"));
        Assert.IsTrue(model.Indexes.ContainsKey("public.accounts_name_idx"));
        Assert.IsTrue(model.Views.ContainsKey("public.account_names"));
        Assert.IsTrue(model.Functions.ContainsKey("public.get_account_count()"));
        Assert.IsTrue(model.Types.ContainsKey("extension:citext"));
        Assert.IsTrue(model.Types.ContainsKey("public.account_status"));
        Assert.IsTrue(model.Types.ContainsKey("public.account_point"));
        Assert.IsTrue(model.Types.ContainsKey("public.account_period"));
        Assert.HasCount(2, model.Types["public.account_point"].CompositeFields);
        Assert.AreEqual("x", model.Types["public.account_point"].CompositeFields[0].Name);
        Assert.AreEqual("integer", model.Types["public.account_point"].CompositeFields[0].DataType);
        Assert.IsNotNull(model.Types["public.account_period"].RangeOptions);
        Assert.AreEqual("timestamp without time zone", model.Types["public.account_period"].RangeOptions!.Subtype);
        Assert.IsTrue(model.Grants.Values.Any(static grant => string.Equals(grant.ObjectKey, "schema:public", StringComparison.OrdinalIgnoreCase) && string.Equals(grant.Grantee, "PUBLIC", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(model.Grants.Values.Any(static grant => string.Equals(grant.ObjectKey, "all tables in schema:public", StringComparison.OrdinalIgnoreCase) && string.Equals(grant.Grantee, "local_pg_admin", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(model.Grants.Values.Any(static grant => string.Equals(grant.ObjectKey, "default functions in schema:public", StringComparison.OrdinalIgnoreCase) && string.Equals(grant.Grantee, "local_pg_admin", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(model.HasErrors, string.Join(Environment.NewLine, model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    [TestMethod]
    public void GenerateSnapshotSql_QuotesIdentifiersAndOrdersStatements()
    {
        var snapshotSql = LiveDatabaseModelLoader.GenerateSnapshotSql(
            [
                new LiveSchemaSnapshot("Sales"),
                new LiveSchemaSnapshot("public")
            ],
            [
                new LiveTableSnapshot(
                    "Sales",
                    "Order",
                    [
                        new LiveColumnSnapshot("Id", "integer", false, null, false, null, null)
                    ])
            ],
            [
                new LiveConstraintSnapshot("Sales", "Order", "Order_pkey", "PRIMARY KEY (\"Id\")")
            ],
            [
                new LiveIndexSnapshot("Sales", "Order", "IX Order", "CREATE INDEX \"IX Order\" ON \"Sales\".\"Order\" USING btree (\"Id\")")
            ],
            [
                new LiveViewSnapshot("Sales", "Order Summary", "SELECT \"Order\".\"Id\" FROM \"Sales\".\"Order\"")
            ],
            [
                new LiveFunctionSnapshot("Sales", "GetOrderCount", "CREATE OR REPLACE FUNCTION \"Sales\".\"GetOrderCount\"() RETURNS integer LANGUAGE sql AS $$ SELECT count(*)::integer FROM \"Sales\".\"Order\"; $$")
            ],
            [
                new LiveTypeSnapshot("public", "uuid-ossp", "extension", "CREATE EXTENSION \"uuid-ossp\" SCHEMA public"),
                new LiveTypeSnapshot("public", "sales_point", "user-defined", "CREATE TYPE public.sales_point AS (x integer, y integer)"),
                new LiveTypeSnapshot("public", "sales_window", "user-defined", "CREATE TYPE public.sales_window AS RANGE (SUBTYPE = integer)")
            ],
            [
                new LiveGrantSnapshot("Sales", "schema:Sales", "PUBLIC", "GRANT USAGE ON SCHEMA \"Sales\" TO PUBLIC")
            ]);

        Assert.IsTrue(snapshotSql.Contains("CREATE SCHEMA IF NOT EXISTS \"Sales\";", StringComparison.Ordinal));
        Assert.IsTrue(snapshotSql.Contains("CREATE EXTENSION \"uuid-ossp\" SCHEMA public;", StringComparison.Ordinal));
        Assert.IsTrue(snapshotSql.Contains("CREATE TYPE public.sales_point AS (x integer, y integer);", StringComparison.Ordinal));
        Assert.IsTrue(snapshotSql.Contains("CREATE TYPE public.sales_window AS RANGE (SUBTYPE = integer);", StringComparison.Ordinal));
        Assert.IsTrue(snapshotSql.Contains("CREATE TABLE \"Sales\".\"Order\"", StringComparison.Ordinal));
        Assert.IsTrue(snapshotSql.Contains("\"Id\" integer NOT NULL", StringComparison.Ordinal));
        Assert.IsTrue(snapshotSql.Contains("ALTER TABLE \"Sales\".\"Order\" ADD CONSTRAINT \"Order_pkey\" PRIMARY KEY (\"Id\");", StringComparison.Ordinal));
        Assert.IsTrue(snapshotSql.Contains("CREATE INDEX \"IX Order\" ON \"Sales\".\"Order\" USING btree (\"Id\");", StringComparison.Ordinal));
        Assert.IsTrue(snapshotSql.Contains("CREATE OR REPLACE VIEW \"Sales\".\"Order Summary\" AS", StringComparison.Ordinal));
        Assert.IsTrue(snapshotSql.Contains("CREATE OR REPLACE FUNCTION \"Sales\".\"GetOrderCount\"() RETURNS integer LANGUAGE sql AS $$ SELECT count(*)::integer FROM \"Sales\".\"Order\"; $$;", StringComparison.Ordinal));
        Assert.IsTrue(snapshotSql.Contains("GRANT USAGE ON SCHEMA \"Sales\" TO PUBLIC;", StringComparison.Ordinal));

        var createSchemaIndex = snapshotSql.IndexOf("CREATE SCHEMA IF NOT EXISTS \"Sales\";", StringComparison.Ordinal);
        var createExtensionIndex = snapshotSql.IndexOf("CREATE EXTENSION", StringComparison.Ordinal);
        var createCompositeTypeIndex = snapshotSql.IndexOf("CREATE TYPE public.sales_point", StringComparison.Ordinal);
        var createRangeTypeIndex = snapshotSql.IndexOf("CREATE TYPE public.sales_window", StringComparison.Ordinal);
        var createTableIndex = snapshotSql.IndexOf("CREATE TABLE", StringComparison.Ordinal);
        var alterTableIndex = snapshotSql.IndexOf("ALTER TABLE", StringComparison.Ordinal);
        var createIndexIndex = snapshotSql.IndexOf("CREATE INDEX", StringComparison.Ordinal);
        var createViewIndex = snapshotSql.IndexOf("CREATE OR REPLACE VIEW", StringComparison.Ordinal);
        var createFunctionIndex = snapshotSql.IndexOf("CREATE OR REPLACE FUNCTION", StringComparison.Ordinal);
        var grantIndex = snapshotSql.IndexOf("GRANT USAGE ON SCHEMA", StringComparison.Ordinal);

        Assert.IsTrue(createSchemaIndex >= 0);
        Assert.IsTrue(createExtensionIndex >= 0);
        Assert.IsTrue(createCompositeTypeIndex >= 0);
        Assert.IsTrue(createRangeTypeIndex > createCompositeTypeIndex);
        Assert.IsTrue(createExtensionIndex > createSchemaIndex);
        // Extensions come before the other types, in the order they can be created (G-213).
        Assert.IsTrue(createExtensionIndex < createCompositeTypeIndex);
        Assert.IsTrue(createTableIndex > createExtensionIndex);
        Assert.IsTrue(alterTableIndex > createTableIndex);
        Assert.IsTrue(createIndexIndex > alterTableIndex);
        Assert.IsTrue(createViewIndex > createIndexIndex);
        Assert.IsTrue(createFunctionIndex > createViewIndex);
        Assert.IsTrue(grantIndex > createFunctionIndex);
    }

    [TestMethod]
    public void GenerateSnapshotSql_EmitsSchemasBeforeDependentObjects()
    {
        var snapshotSql = LiveDatabaseModelLoader.GenerateSnapshotSql(
            [
                new LiveSchemaSnapshot("analytics"),
                new LiveSchemaSnapshot("public")
            ],
            [
                new LiveTableSnapshot(
                    "analytics",
                    "events",
                    [
                        new LiveColumnSnapshot("id", "uuid", false, null, false, null, null)
                    ])
            ],
            [],
            [],
            [
                new LiveViewSnapshot("analytics", "event_ids", "SELECT events.id FROM analytics.events")
            ],
            [
                new LiveFunctionSnapshot("analytics", "get_event_count", "CREATE OR REPLACE FUNCTION analytics.get_event_count() RETURNS integer LANGUAGE sql AS $$ SELECT count(*)::integer FROM analytics.events; $$")
            ],
            [
                new LiveTypeSnapshot("analytics", "event_state", "user-defined", "CREATE TYPE analytics.event_state AS ENUM ('open', 'closed')")
            ],
            [
                new LiveGrantSnapshot("analytics", "schema:analytics", "PUBLIC", "GRANT USAGE ON SCHEMA analytics TO PUBLIC")
            ]);

        var createSchemaIndex = snapshotSql.IndexOf("CREATE SCHEMA IF NOT EXISTS analytics;", StringComparison.Ordinal);
        var createTypeIndex = snapshotSql.IndexOf("CREATE TYPE analytics.event_state", StringComparison.Ordinal);
        var createTableIndex = snapshotSql.IndexOf("CREATE TABLE analytics.events", StringComparison.Ordinal);
        var createViewIndex = snapshotSql.IndexOf("CREATE OR REPLACE VIEW analytics.event_ids", StringComparison.Ordinal);
        var createFunctionIndex = snapshotSql.IndexOf("CREATE OR REPLACE FUNCTION analytics.get_event_count", StringComparison.Ordinal);
        var grantIndex = snapshotSql.IndexOf("GRANT USAGE ON SCHEMA analytics TO PUBLIC;", StringComparison.Ordinal);

        Assert.IsTrue(createSchemaIndex >= 0);
        Assert.IsTrue(createTypeIndex > createSchemaIndex);
        Assert.IsTrue(createTableIndex > createTypeIndex);
        Assert.IsTrue(createViewIndex > createTableIndex);
        Assert.IsTrue(createFunctionIndex > createViewIndex);
        Assert.IsTrue(grantIndex > createFunctionIndex);
    }

    private static LiveDatabaseModelLoader CreateLoader()
        => new(new PgQueryParser(), new SemanticBuilder());

    [TestMethod]
    public void AKeyConstraintsIndexCommentIsWrittenAndReadBackOnTheConstraint()
    {
        var snapshotSql = LiveDatabaseModelLoader.GenerateSnapshotSql(
            [new LiveSchemaSnapshot("public")],
            [new LiveTableSnapshot("public", "accounts", [new LiveColumnSnapshot("id", "uuid", false, null, false, null, null)])],
            [new LiveConstraintSnapshot("public", "accounts", "accounts_pkey", "PRIMARY KEY (id)") { IndexComment = "the key's index" }],
            [],
            []);

        StringAssert.Contains(snapshotSql, "COMMENT ON INDEX public.accounts_pkey IS 'the key''s index';");
        var constraint = new LiveDatabaseModelLoader(new PgQueryParser(), new SemanticBuilder()).BuildModelFromSnapshotSql(snapshotSql).Constraints.Values.Single();
        Assert.AreEqual("the key's index", constraint.IndexComment);
        Assert.IsTrue(constraint.IndexCommentStated);
    }
}
