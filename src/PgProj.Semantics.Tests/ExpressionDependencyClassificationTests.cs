using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Semantics.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ExpressionDependencyClassificationTests
{
    [TestMethod]
    public void SchemaQualifiedFunctionDefaultIsNotReportedAsMissingColumn()
    {
        const string sql = "create table public.accounts (id uuid default public.uuid_generate_v7());";

        var model = BuildModel(sql);

        Assert.IsFalse(model.Diagnostics.Any(diagnostic =>
            diagnostic.Message.Contains("references missing column 'public.uuid_generate_v7'", StringComparison.OrdinalIgnoreCase)));

        var column = model.Tables["public.accounts"].Columns.Single(column => column.Name == "id");
        CollectionAssert.Contains(column.ReferencedFunctions.ToList(), "public.uuid_generate_v7");
    }

    [TestMethod]
    public void QualifiedColumnInsideFunctionArgumentsRemainsAColumnDependency()
    {
        const string sql = "create table public.accounts (source uuid, id uuid default public.make_uuid(public.accounts.source));";

        var model = BuildModel(sql);

        Assert.IsFalse(model.Diagnostics.Any(diagnostic =>
            diagnostic.Message.Contains("missing column 'public.accounts.source'", StringComparison.OrdinalIgnoreCase)));

        var column = model.Tables["public.accounts"].Columns.Single(column => column.Name == "id");
        CollectionAssert.Contains(column.ReferencedColumns.ToList(), "public.accounts.source");
        CollectionAssert.Contains(column.ReferencedFunctions.ToList(), "public.make_uuid");
    }

    [TestMethod]
    public void IndexNodeIsDeclaredBeforeDeepIndexMapping()
    {
        const string sql = "create table public.accounts (account_name text); create index accounts_account_name on public.accounts (account_name);";
        // Parsed for real: an index's key columns are read from its structured key elements, which a hand-built IndexNode would not carry (G-90 part 3).
        var model = BuildModel(sql);

        Assert.IsTrue(model.Indexes.ContainsKey("public.accounts_account_name"));
        Assert.IsFalse(model.Diagnostics.Any(diagnostic =>
            diagnostic.Message.Contains("could not find semantic index 'public.accounts_account_name'", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(model.Diagnostics.Any(diagnostic =>
            diagnostic.Message.Contains("does not expose a raw predicate expression fragment yet", StringComparison.OrdinalIgnoreCase)));
        CollectionAssert.Contains(
            model.Indexes["public.accounts_account_name"].ReferencedColumns.ToList(),
            "public.accounts.account_name");
    }

    [TestMethod]
    public void SchemaQualifiedDomainIsResolvedFromSqlDeclaration()
    {
        const string sql = "create domain public.account_name_type as text; create table public.accounts (name public.account_name_type);";
        var table = new TableNode("accounts", "public", [CreateColumn("name", null, "public.account_name_type")]);
        // G-78: CREATE DOMAIN is read from pg_query's AST, not regexed out of this hand-built tree's Sql text, so
        // the domain the table's own column references must be declared here explicitly too.
        var domain = new DomainNode("account_name_type", "public", "text");
        var model = new SemanticPipeline().Execute([
            new SqlSyntaxTree(sql, null, [], [table, domain], [])
        ]);

        Assert.IsTrue(model.Types.ContainsKey("public.account_name_type"));
        Assert.IsFalse(model.Diagnostics.Any(diagnostic =>
            diagnostic.Message.Contains("references missing type 'public.account_name_type'", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void CitextExtensionIsResolvedFromExplicitExtensionDeclaration()
    {
        const string sql = "create extension if not exists citext; create table public.accounts (name citext);";
        var table = new TableNode("accounts", "public", [CreateColumn("name", null, "citext")]);
        // G-78: CREATE EXTENSION is read from pg_query's AST, not regexed out of this hand-built tree's Sql text.
        var extension = new ExtensionNode("citext", null);
        var model = new SemanticPipeline().Execute([
            new SqlSyntaxTree(sql, null, [], [table, extension], [])
        ]);

        Assert.IsTrue(model.Types.ContainsKey("extension:citext"));
        Assert.IsFalse(model.Diagnostics.Any(diagnostic =>
            diagnostic.Message.Contains("references missing type 'public.citext'", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void ArbitraryDeclaredTypesAndExtensionsAreResolved()
    {
        const string sql = "create type public.account_state as enum ('active'); create domain public.account_code as text; create extension if not exists hstore; create table public.accounts (state public.account_state, code public.account_code, metadata hstore);";
        var table = new TableNode("accounts", "public", [
            CreateColumn("state", null, "public.account_state"),
            CreateColumn("code", null, "public.account_code"),
            CreateColumn("metadata", null, "hstore")
        ]);
        // G-78: CREATE TYPE/DOMAIN/EXTENSION are read from pg_query's AST, not regexed out of this hand-built
        // tree's Sql text, so every declaration the table's own columns reference must be listed here explicitly.
        var type = new TypeNode("account_state", "public", "create type public.account_state as enum ('active')");
        var domain = new DomainNode("account_code", "public", "text");
        var extension = new ExtensionNode("hstore", null);
        var model = new SemanticPipeline().Execute([
            new SqlSyntaxTree(sql, null, [], [table, type, domain, extension], [])
        ]);

        Assert.IsTrue(model.Types.ContainsKey("public.account_state"));
        Assert.IsTrue(model.Types.ContainsKey("public.account_code"));
        Assert.IsTrue(model.Types.ContainsKey("extension:hstore"));
        Assert.IsFalse(model.Diagnostics.Any(diagnostic => diagnostic.Message.Contains("references missing type", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void UuidRemainsRecognizedAsBuiltInType()
    {
        const string sql = "create table public.accounts (id uuid);";
        var table = new TableNode("accounts", "public", [CreateColumn("id", null, "uuid")]);
        var model = new SemanticPipeline().Execute([
            new SqlSyntaxTree(sql, null, [], [table], [])
        ]);

        Assert.IsFalse(model.Diagnostics.Any(diagnostic =>
            diagnostic.Message.Contains("references missing type 'uuid'", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void SchemaQualifiedBuiltInTypesAreResolvedByTerminalName()
    {
        const string sql = "create table activity.person_projection (id activity.uuid, name activity.text, payload activity.jsonb);";
        var table = new TableNode("person_projection", "activity", [
            new ColumnNode("id", "person_projection", "activity", "activity.uuid", "activity.uuid", true, null, false, null, null, null, null),
            new ColumnNode("name", "person_projection", "activity", "activity.text", "activity.text", true, null, false, null, null, null, null),
            new ColumnNode("payload", "person_projection", "activity", "activity.jsonb", "activity.jsonb", true, null, false, null, null, null, null)
        ]);
        var model = new SemanticPipeline().Execute([
            new SqlSyntaxTree(sql, null, [], [table], [])
        ]);

        Assert.IsFalse(model.Diagnostics.Any(diagnostic =>
            diagnostic.Message.Contains("references missing type", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void UndeclaredTypeStillProducesMissingTypeDiagnostic()
    {
        const string sql = "create table public.accounts (value unknown_account_type);";
        var table = new TableNode("accounts", "public", [CreateColumn("value", null, "unknown_account_type")]);
        var model = new SemanticPipeline().Execute([
            new SqlSyntaxTree(sql, null, [], [table], [])
        ]);

        Assert.IsTrue(model.Diagnostics.Any(diagnostic =>
            diagnostic.Message.Contains("references missing type 'unknown_account_type'", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void BuiltInSystemRelationsDoNotProduceMissingTableDiagnostics()
    {
        const string sql = "create function audit.generic_create_audit() returns trigger language sql as $$ select * from INFORMATION_SCHEMA.COLUMNS; select * from pg_catalog.pg_class; $$; create index missing_idx on missing.audit_rows (id);";
        var function = new FunctionNode("generic_create_audit", "audit", "sql", "select * from INFORMATION_SCHEMA.COLUMNS; select * from pg_catalog.pg_class;");
        var missingIndex = new IndexNode("missing_idx", "missing", "audit_rows", null);
        var model = new SemanticPipeline().Execute([
            new SqlSyntaxTree(sql, null, [], [function, missingIndex], [])
        ]);

        Assert.IsFalse(model.Diagnostics.Any(diagnostic =>
            diagnostic.Message.Contains("missing table 'INFORMATION_SCHEMA.COLUMNS'", StringComparison.OrdinalIgnoreCase)
            || diagnostic.Message.Contains("missing table 'pg_catalog.pg_class'", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(
            model.Diagnostics.Any(diagnostic => diagnostic.Message.Contains("missing.audit_rows", StringComparison.OrdinalIgnoreCase)),
            string.Join(Environment.NewLine, model.Diagnostics.Select(diagnostic => diagnostic.Message)));
    }

    [TestMethod]
    public void BuiltInSystemRelationsDoNotProduceMissingColumnDiagnostics()
    {
        const string sql = "create function audit.generic_history_audit() returns trigger language sql as $$ select INFORMATION_SCHEMA.COLUMNS.column_name from pg_attribute base join pg_attribute on pg_attribute.attrelid = base.attrelid; $$;";
        var function = new FunctionNode(
            "generic_history_audit",
            "audit",
            "sql",
            "select INFORMATION_SCHEMA.COLUMNS.column_name from pg_attribute base join pg_attribute on pg_attribute.attrelid = base.attrelid;");

        var model = new SemanticPipeline().Execute([
            new SqlSyntaxTree(sql, null, [], [function], [])
        ]);

        Assert.IsFalse(model.Diagnostics.Any(diagnostic =>
            diagnostic.Message.Contains("missing table 'audit.pg_attribute'", StringComparison.OrdinalIgnoreCase)
            || diagnostic.Message.Contains("missing table 'pg_attribute'", StringComparison.OrdinalIgnoreCase)
            || diagnostic.Message.Contains("missing column 'INFORMATION_SCHEMA.COLUMNS'", StringComparison.OrdinalIgnoreCase)
            || diagnostic.Message.Contains("missing column 'base.attrelid'", StringComparison.OrdinalIgnoreCase)
            || diagnostic.Message.Contains("missing column 'pg_attribute.attrelid'", StringComparison.OrdinalIgnoreCase)),
            string.Join(Environment.NewLine, model.Diagnostics.Select(diagnostic => diagnostic.Message)));
    }

    [TestMethod]
    public void OwnedColumnReferencesDoNotCreateTableSelfCycles()
    {
        const string sql = "create table public.accounts (id uuid, name text);";
        var table = new TableNode("accounts", "public", [
            CreateColumn("id", null),
            CreateColumn("name", null, "text")
        ]);

        var model = new SemanticPipeline().Execute([
            new SqlSyntaxTree(sql, null, [], [table], [])
        ]);

        Assert.IsFalse(model.Diagnostics.Any(diagnostic =>
            diagnostic.Message.Contains("Semantic dependency cycle detected: public.accounts -> public.accounts", StringComparison.OrdinalIgnoreCase)),
            string.Join(Environment.NewLine, model.Diagnostics.Select(diagnostic => diagnostic.Message)));
    }

    // Parsed for real: a column's DEFAULT is bound from its pg_query tree, which a hand-built ColumnNode would not carry (G-90 part 2).
    private static DatabaseModel BuildModel(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);

    private static ColumnNode CreateColumn(string name, string? defaultExpression, string dataType = "uuid")
        => new(name, "accounts", "public", dataType, dataType, true, defaultExpression, false, null, null, null, null);
}
