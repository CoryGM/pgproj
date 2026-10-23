using Npgsql;
using PgProj.Core;
using PgProj.Deploy;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-65 against a real server: grants on individual tables, views, sequences, functions and columns, several grantees,
/// <c>WITH GRANT OPTION</c>, <c>FOR ROLE</c>, default privileges on types, and role membership must deploy, read back as the same
/// grants (an empty re-diff and an empty second publish), and be told from what the project does not want, which is revoked.
/// Unit tests cannot see a live-catalog query that is wrong, so the convergence assertions here are the point.
/// </summary>
[TestClass]
public sealed class ObjectGrantEndToEndTests
{
    private readonly PgQueryParser _parser = new();
    private readonly SemanticBuilder _semanticBuilder = new();
    private readonly LiveDatabaseModelLoader _liveLoader;

    public ObjectGrantEndToEndTests()
    {
        _liveLoader = new LiveDatabaseModelLoader(_parser, _semanticBuilder);
    }

    [TestMethod]
    public async Task ObjectLevelGrants_DeployConvergeAndOnlyWhatTheProjectDoesNotWantIsRevoked()
    {
        var schema = $"test_g65_{Guid.NewGuid():N}";
        var suffix = Guid.NewGuid().ToString("N")[..10];
        string ra = $"g65a_{suffix}", rb = $"g65b_{suffix}", member = $"g65m_{suffix}", owner = $"g65o_{suffix}", extra = $"g65x_{suffix}", outsider = $"g65z_{suffix}";
        string[] roles = [ra, rb, member, owner, extra, outsider];

        await using var connection = await OpenConnectionAsync();
        try
        {
            await ExecuteAsync(connection, string.Join(" ", roles.Select(static role => $"CREATE ROLE {role} NOLOGIN;")));

            string Source(string tableGrant) => $"""
                CREATE SCHEMA {schema};
                CREATE TABLE {schema}.t (id integer PRIMARY KEY, name text);
                CREATE TABLE {schema}.u (id integer PRIMARY KEY);
                CREATE VIEW {schema}.v AS SELECT id FROM {schema}.t;
                CREATE SEQUENCE {schema}.q;
                CREATE FUNCTION {schema}.f(x integer, y text) RETURNS integer LANGUAGE sql AS $$ SELECT x $$;
                GRANT USAGE ON SCHEMA {schema} TO PUBLIC;
                {tableGrant}
                GRANT SELECT ON {schema}.v TO {ra} WITH GRANT OPTION;
                GRANT UPDATE (name) ON {schema}.t TO {ra};
                GRANT SELECT (id) ON {schema}.u TO {rb};
                GRANT USAGE ON SEQUENCE {schema}.q TO {ra};
                GRANT EXECUTE ON FUNCTION {schema}.f(integer, text) TO {ra};
                GRANT {member} TO {ra};
                ALTER DEFAULT PRIVILEGES FOR ROLE {owner} IN SCHEMA {schema} GRANT SELECT ON TABLES TO {ra};
                ALTER DEFAULT PRIVILEGES IN SCHEMA {schema} GRANT USAGE ON TYPES TO {ra};
                """;

            async Task<bool> Check(string sql)
            {
                await using var command = new NpgsqlCommand(sql, connection);
                return (bool)(await command.ExecuteScalarAsync())!;
            }

            async Task AssertConverged(DatabaseModel target, string step)
            {
                var live = await LoadAsync(connection, schema, roles);
                var reDiff = await EndToEndSupport.DiffAsPublishedAsync(connection, live, target);
                Assert.AreEqual(0, reDiff.Added.Count + reDiff.Removed.Count + reDiff.Modified.Count, step + ": " + Describe(reDiff));
                Assert.AreEqual(
                    0,
                    new ScriptGenerator().Generate(reDiff, new PgProj.Dependencies.DependencyAnalyzer().Analyze(live, target)).Operations.Count,
                    step + ": a second publish must emit no operations.");
            }

            // 1. Deploy from nothing: everything is granted, nothing is revoked, and it reads back as the same grants.
            var first = Build(Source($"GRANT SELECT, INSERT ON {schema}.t TO {ra}, {rb};"));
            Assert.IsFalse(first.Diagnostics.Any(static diagnostic => diagnostic.Severity == SemanticDiagnosticSeverity.Warning), string.Join("\n", first.Diagnostics.Select(static d => d.Message)));
            var script = await PublishAsync(connection, DatabaseModel.Empty, first);
            Assert.IsFalse(script.Contains("REVOKE", StringComparison.Ordinal), script);

            Assert.IsTrue(await Check($"SELECT has_table_privilege('{ra}', '{schema}.t', 'SELECT') AND has_table_privilege('{ra}', '{schema}.t', 'INSERT')"), script);
            Assert.IsTrue(await Check($"SELECT has_table_privilege('{rb}', '{schema}.t', 'SELECT') AND has_table_privilege('{rb}', '{schema}.t', 'INSERT')"), "Several grantees.");
            Assert.IsFalse(await Check($"SELECT has_table_privilege('{ra}', '{schema}.u', 'SELECT')"), "A grant on one table must not reach another.");
            Assert.IsTrue(await Check($"SELECT has_table_privilege('{ra}', '{schema}.v', 'SELECT WITH GRANT OPTION')"), "WITH GRANT OPTION on a view.");
            Assert.IsTrue(await Check($"SELECT has_column_privilege('{ra}', '{schema}.t', 'name', 'UPDATE')"), "A column privilege.");
            Assert.IsFalse(await Check($"SELECT has_table_privilege('{ra}', '{schema}.t', 'UPDATE')"), "A column privilege is not a table privilege.");
            Assert.IsTrue(await Check($"SELECT has_column_privilege('{rb}', '{schema}.u', 'id', 'SELECT')"));
            Assert.IsTrue(await Check($"SELECT has_sequence_privilege('{ra}', '{schema}.q', 'USAGE')"), "A sequence.");
            Assert.IsTrue(await Check($"SELECT has_function_privilege('{ra}', '{schema}.f(integer, text)', 'EXECUTE')"), "A function.");
            Assert.IsTrue(await Check($"SELECT pg_has_role('{ra}', '{member}', 'MEMBER')"), "Role membership.");
            Assert.IsTrue(
                await Check($"SELECT EXISTS (SELECT 1 FROM pg_default_acl d WHERE d.defaclnamespace = '{schema}'::regnamespace AND d.defaclrole = '{owner}'::regrole AND d.defaclobjtype = 'r')"),
                "Default privileges FOR ROLE.");
            await AssertConverged(first, "after the first publish");

            // 2. What the project does not want is revoked, and only that. Role membership and PUBLIC's EXECUTE are left alone.
            await ExecuteAsync(connection, $"GRANT DELETE ON {schema}.t TO {ra}; GRANT SELECT ON {schema}.u TO {extra}; GRANT {member} TO {extra};");
            var revokeScript = await PublishAsync(connection, await LoadAsync(connection, schema, roles), first);
            // Live holds DELETE, INSERT, SELECT on t for ra as one grant and the project wants two of them: the grant is revoked whole
            // and what the project wants is granted again, in that order, in the same transaction.
            var revokeTable = revokeScript.IndexOf($"REVOKE DELETE, INSERT, SELECT ON TABLE \"{schema}\".\"t\" FROM \"{ra}\"", StringComparison.Ordinal);
            // The project's own grant is re-granted with its privileges in the order written (G-112).
            var regrantTable = revokeScript.IndexOf($"GRANT SELECT, INSERT ON TABLE \"{schema}\".\"t\" TO \"{ra}\"", StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, revokeTable, revokeScript);
            Assert.IsGreaterThan(revokeTable, regrantTable, revokeScript);
            StringAssert.Contains(revokeScript, $"REVOKE SELECT ON TABLE \"{schema}\".\"u\" FROM \"{extra}\"", revokeScript);
            Assert.IsFalse(revokeScript.Contains($"REVOKE \"{member}\"", StringComparison.Ordinal), "Role membership must not be revoked.\n" + revokeScript);
            Assert.IsFalse(revokeScript.Contains("FROM PUBLIC", StringComparison.Ordinal), "PUBLIC's EXECUTE must not be revoked.\n" + revokeScript);
            Assert.IsFalse(await Check($"SELECT has_table_privilege('{ra}', '{schema}.t', 'DELETE')"), "The unlisted privilege must be gone.");
            Assert.IsTrue(await Check($"SELECT has_table_privilege('{ra}', '{schema}.t', 'SELECT')"), "What the project lists must remain.");
            Assert.IsTrue(await Check($"SELECT pg_has_role('{extra}', '{member}', 'MEMBER')"), "Membership that the project does not list stays.");
            Assert.IsTrue(await Check($"SELECT has_function_privilege('{outsider}', '{schema}.f(integer, text)', 'EXECUTE')"), "PUBLIC's default EXECUTE must remain.");
            await AssertConverged(first, "after revoking the unlisted grants");

            // 3. Narrowing a grant really narrows it: INSERT goes, SELECT stays.
            var narrowed = Build(Source($"GRANT SELECT ON {schema}.t TO {ra}, {rb};"));
            await PublishAsync(connection, await LoadAsync(connection, schema, roles), narrowed);
            Assert.IsFalse(await Check($"SELECT has_table_privilege('{ra}', '{schema}.t', 'INSERT')"), "INSERT must have been revoked.");
            Assert.IsTrue(await Check($"SELECT has_table_privilege('{ra}', '{schema}.t', 'SELECT')"), "SELECT must have been kept.");
            await AssertConverged(narrowed, "after narrowing");

            // 4. A grant option is part of the grant: dropping it from the project takes it away without taking the privilege.
            var withoutOption = Build(Source($"GRANT SELECT ON {schema}.t TO {ra}, {rb};").Replace("WITH GRANT OPTION", string.Empty, StringComparison.Ordinal));
            await PublishAsync(connection, await LoadAsync(connection, schema, roles), withoutOption);
            Assert.IsTrue(await Check($"SELECT has_table_privilege('{ra}', '{schema}.v', 'SELECT')"), "The privilege must remain.");
            Assert.IsFalse(await Check($"SELECT has_table_privilege('{ra}', '{schema}.v', 'SELECT WITH GRANT OPTION')"), "The grant option must be gone.");
            await AssertConverged(withoutOption, "after dropping the grant option");
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {schema} CASCADE;");
            await ExecuteAsync(connection, $"DROP OWNED BY {string.Join(", ", roles)}; DROP ROLE IF EXISTS {string.Join(", ", roles)};");
        }
    }

    /// <summary>
    /// The same privileges written per table and over <c>ALL TABLES IN SCHEMA</c> are one grant: PostgreSQL cannot tell them apart,
    /// so neither may the comparison, in either direction.
    /// </summary>
    [TestMethod]
    public async Task GrantsPerObjectAndOverAllObjectsAreTheSameGrant()
    {
        var schema = $"test_g65_{Guid.NewGuid():N}";
        var role = $"g65c_{Guid.NewGuid():N}"[..20];
        await using var connection = await OpenConnectionAsync();
        try
        {
            await ExecuteAsync(connection, $"CREATE ROLE {role} NOLOGIN;");

            string Source(string grant) => $"""
                CREATE SCHEMA {schema};
                CREATE TABLE {schema}.a (id integer PRIMARY KEY);
                CREATE TABLE {schema}.b (id integer PRIMARY KEY);
                CREATE VIEW {schema}.v AS SELECT id FROM {schema}.a;
                CREATE SEQUENCE {schema}.q;
                {grant}
                """;

            async Task AssertNoChange(DatabaseModel target, string step)
            {
                var live = await LoadAsync(connection, schema, [role]);
                var changes = await EndToEndSupport.DiffAsPublishedAsync(connection, live, target);
                Assert.AreEqual(0, changes.Added.Count + changes.Removed.Count + changes.Modified.Count, step + ": " + Describe(changes));
            }

            var perObject = Build(Source($"GRANT SELECT ON {schema}.a, {schema}.b, {schema}.v TO {role}; GRANT USAGE, SELECT, UPDATE ON SEQUENCE {schema}.q TO {role};"));
            var overAll = Build(Source($"GRANT SELECT ON ALL TABLES IN SCHEMA {schema} TO {role}; GRANT ALL ON ALL SEQUENCES IN SCHEMA {schema} TO {role};"));

            await PublishAsync(connection, DatabaseModel.Empty, perObject);
            await AssertNoChange(perObject, "per object against itself");
            await AssertNoChange(overAll, "per object deployed, over all objects written");

            // And the other way round, from a clean schema.
            await ExecuteAsync(connection, $"DROP SCHEMA {schema} CASCADE;");
            await PublishAsync(connection, DatabaseModel.Empty, overAll);
            await AssertNoChange(overAll, "over all objects against itself");
            await AssertNoChange(perObject, "over all objects deployed, per object written");

            // Something that is not the same is still a difference: one table short of the schema.
            var oneShort = Build(Source($"GRANT SELECT ON {schema}.a, {schema}.b TO {role}; GRANT ALL ON ALL SEQUENCES IN SCHEMA {schema} TO {role};"));
            var live = await LoadAsync(connection, schema, [role]);
            var difference = await EndToEndSupport.DiffAsPublishedAsync(connection, live, oneShort);
            Assert.IsGreaterThan(0, difference.Removed.Count(static removed => removed.NodeKind == PgProj.Diff.ChangeNodeKind.Grant), Describe(difference));
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {schema} CASCADE;");
            await ExecuteAsync(connection, $"DROP OWNED BY {role}; DROP ROLE IF EXISTS {role};");
        }
    }

    /// <summary>
    /// <c>ALTER DEFAULT PRIVILEGES FOR ROLE &lt;the connected role&gt;</c> (what <c>pg_dump</c> writes) and the same statement without
    /// <c>FOR ROLE</c> are one grant: a publish of either must leave a live database that reads back as equal. Found by a real
    /// server: the first version of G-65 revoked and granted the <c>FOR ROLE</c> form on every publish.
    /// </summary>
    [TestMethod]
    public async Task DefaultPrivilegesForTheConnectedRole_ConvergeWhetherOrNotTheRoleIsNamed()
    {
        var schema = $"test_g65_{Guid.NewGuid():N}";
        var role = $"g65r_{Guid.NewGuid():N}"[..18];
        await using var connection = await OpenConnectionAsync();
        try
        {
            await ExecuteAsync(connection, $"CREATE ROLE {role} NOLOGIN;");
            string connected;
            await using (var whoAmI = new NpgsqlCommand("select current_user", connection))
            {
                connected = (string)(await whoAmI.ExecuteScalarAsync())!;
            }

            var named = Build($"CREATE SCHEMA {schema}; ALTER DEFAULT PRIVILEGES FOR ROLE {connected} IN SCHEMA {schema} GRANT SELECT ON TABLES TO {role};");
            var unnamed = Build($"CREATE SCHEMA {schema}; ALTER DEFAULT PRIVILEGES IN SCHEMA {schema} GRANT SELECT ON TABLES TO {role};");

            await PublishAsync(connection, DatabaseModel.Empty, named);

            foreach (var (target, step) in new[] { (named, "named against itself"), (unnamed, "unnamed against a named deploy"), (named, "named again") })
            {
                var live = await LoadAsync(connection, schema, [role]);
                var changes = await EndToEndSupport.DiffAsPublishedAsync(connection, live, target);
                Assert.AreEqual(0, changes.Added.Count + changes.Removed.Count + changes.Modified.Count, step + ": " + Describe(changes));
            }

            // The default really is in effect for the connected role: a table created now is readable by the grantee.
            await ExecuteAsync(connection, $"CREATE TABLE {schema}.made_later (id integer);");
            await using var check = new NpgsqlCommand($"SELECT has_table_privilege('{role}', '{schema}.made_later', 'SELECT')", connection);
            Assert.IsTrue((bool)(await check.ExecuteScalarAsync())!, "The default privilege must apply to tables created afterwards.");
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {schema} CASCADE;");
            await ExecuteAsync(connection, $"DROP OWNED BY {role}; DROP ROLE IF EXISTS {role};");
        }
    }

    private DatabaseModel Build(string sql) => _semanticBuilder.Build([_parser.Parse(sql)]);

    /// <summary>The live model reduced to one schema and to the role memberships of the roles a test created.</summary>
    private async Task<DatabaseModel> LoadAsync(NpgsqlConnection connection, string schema, IReadOnlyCollection<string> roles)
    {
        var model = await _liveLoader.LoadAsync(connection, CancellationToken.None);
        var prefix = schema + ".";

        Dictionary<string, T> InSchema<T>(IReadOnlyDictionary<string, T> source) where T : class
            => source.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal)).ToDictionary(pair => pair.Key, pair => pair.Value);

        return model with
        {
            Tables = InSchema(model.Tables),
            Views = InSchema(model.Views),
            Functions = InSchema(model.Functions),
            Triggers = InSchema(model.Triggers),
            Procedures = InSchema(model.Procedures),
            Indexes = InSchema(model.Indexes),
            Constraints = InSchema(model.Constraints),
            Sequences = InSchema(model.Sequences),
            Types = InSchema(model.Types),
            Grants = model.Grants
                .Where(pair => string.Equals(pair.Value.SchemaName, schema, StringComparison.Ordinal)
                    || (pair.Value.ObjectKey?.StartsWith("role:", StringComparison.Ordinal) == true
                        && (roles.Contains(pair.Value.Grantee ?? string.Empty) || roles.Any(role => string.Equals(pair.Value.ObjectKey, $"role:{role}", StringComparison.Ordinal)))))
                .ToDictionary(pair => pair.Key, pair => pair.Value),
        };
    }

    private async Task<string> PublishAsync(NpgsqlConnection connection, DatabaseModel live, DatabaseModel target)
    {
        var scriptGenerator = new ScriptGenerator();
        var batch = scriptGenerator.Generate(
            await EndToEndSupport.DiffAsPublishedAsync(connection, live, target),
            new PgProj.Dependencies.DependencyAnalyzer().Analyze(live, target));
        var script = scriptGenerator.GenerateSqlScript(batch);
        if (batch.Operations.Count > 0)
        {
            await using var transaction = await connection.BeginTransactionAsync();
            foreach (var statement in SqlStatementSplitter.Split(script))
            {
                await using var command = new NpgsqlCommand(statement, connection, transaction);
                await command.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }

        return script;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        foreach (var statement in SqlStatementSplitter.Split(sql))
        {
            await using var command = new NpgsqlCommand(statement, connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        var connection = new NpgsqlConnection(PostgresContainerFixture.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static string Describe(PgProj.Diff.ChangeSet changes)
        => "Added: " + string.Join(", ", changes.Added.Select(static change => change.Node.Key))
            + " | Removed: " + string.Join(", ", changes.Removed.Select(static change => change.NodeId))
            + " | Modified: " + string.Join(", ", changes.Modified.Select(static change => change.NodeId));
}
