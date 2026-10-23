using Npgsql;
using PgProj.Core;
using PgProj.Deploy;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-30: real end-to-end tests against a disposable PostgreSQL server (via Testcontainers).
/// These prove the full pipeline (parse -> semantics -> diff -> dependency graph -> script
/// generation -> execute) actually converges against a live server, that iterative changes
/// keep converging, and that a failing script rolls back atomically.
/// </summary>
[TestClass]
public sealed class PublishRollbackReDiffTests
{
    private readonly PgQueryParser _parser = new();
    private readonly SemanticBuilder _semanticBuilder = new();
    private readonly LiveDatabaseModelLoader _liveLoader;

    public PublishRollbackReDiffTests()
    {
        _liveLoader = new LiveDatabaseModelLoader(_parser, _semanticBuilder);
    }

    private static string NewSchemaName() => $"test_g30_{Guid.NewGuid():N}";

    private async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        var connection = new NpgsqlConnection(PostgresContainerFixture.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private DatabaseModel BuildModel(string sql)
    {
        var syntaxTree = _parser.Parse(sql);
        return _semanticBuilder.Build([syntaxTree]);
    }

    private static async Task ExecuteScriptAsync(NpgsqlConnection connection, string script)
    {
        var statements = SqlStatementSplitter.Split(script);
        await using var transaction = await connection.BeginTransactionAsync();
        foreach (var statement in statements)
        {
            await using var command = new NpgsqlCommand(statement, connection, transaction);
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    [TestMethod]
    public async Task PublishFromEmpty_ReDiffsToZero()
    {
        var schema = NewSchemaName();
        await using var connection = await OpenConnectionAsync();
        try
        {
            var sourceModel = BuildModel(SqlFixtures.InitialSchema(schema));

            var dependencyAnalyzer = new PgProj.Dependencies.DependencyAnalyzer();
            var diffEngine = new PgProj.Diff.DiffEngine();
            var scriptGenerator = new PgProj.Deploy.ScriptGenerator();

            var changes = diffEngine.Diff(DatabaseModel.Empty, sourceModel);
            var graph = dependencyAnalyzer.Analyze(DatabaseModel.Empty, sourceModel);
            var batch = scriptGenerator.Generate(changes, graph);
            var script = scriptGenerator.GenerateSqlScript(batch);

            await ExecuteScriptAsync(connection, script);

            var liveModel = await LoadSchemaModelAsync(connection, schema);
            var reDiff = await EndToEndSupport.DiffAsPublishedAsync(connection, liveModel, sourceModel);

            Assert.AreEqual(0, reDiff.Added.Count, "Expected no added objects after publish + reload.");
            Assert.AreEqual(0, reDiff.Removed.Count, "Expected no removed objects after publish + reload.");
            Assert.AreEqual(0, reDiff.Modified.Count, "Expected no modified objects after publish + reload.");
        }
        finally
        {
            await DropSchemaAsync(connection, schema);
        }
    }

    [TestMethod]
    public async Task IncrementalChange_ReDiffsToZero()
    {
        var schema = NewSchemaName();
        await using var connection = await OpenConnectionAsync();
        try
        {
            var dependencyAnalyzer = new PgProj.Dependencies.DependencyAnalyzer();
            var diffEngine = new PgProj.Diff.DiffEngine();
            var scriptGenerator = new PgProj.Deploy.ScriptGenerator();

            // First publish: initial schema.
            var initialModel = BuildModel(SqlFixtures.InitialSchema(schema));
            var initialChanges = diffEngine.Diff(DatabaseModel.Empty, initialModel);
            var initialGraph = dependencyAnalyzer.Analyze(DatabaseModel.Empty, initialModel);
            var initialBatch = scriptGenerator.Generate(initialChanges, initialGraph);
            await ExecuteScriptAsync(connection, scriptGenerator.GenerateSqlScript(initialBatch));

            var liveAfterFirstPublish = await LoadSchemaModelAsync(connection, schema);

            // Second publish: revised schema (adds a column + index).
            var revisedModel = BuildModel(SqlFixtures.RevisedSchema(schema));
            var incrementalChanges = await EndToEndSupport.DiffAsPublishedAsync(connection, liveAfterFirstPublish, revisedModel);
            var incrementalGraph = dependencyAnalyzer.Analyze(liveAfterFirstPublish, revisedModel);
            var incrementalBatch = scriptGenerator.Generate(incrementalChanges, incrementalGraph);
            await ExecuteScriptAsync(connection, scriptGenerator.GenerateSqlScript(incrementalBatch));

            var liveAfterSecondPublish = await LoadSchemaModelAsync(connection, schema);
            var reDiff = await EndToEndSupport.DiffAsPublishedAsync(connection, liveAfterSecondPublish, revisedModel);

            Assert.AreEqual(0, reDiff.Added.Count, "Expected no added objects after incremental publish + reload.");
            Assert.AreEqual(0, reDiff.Removed.Count, "Expected no removed objects after incremental publish + reload.");
            Assert.AreEqual(0, reDiff.Modified.Count, "Expected no modified objects after incremental publish + reload.");
        }
        finally
        {
            await DropSchemaAsync(connection, schema);
        }
    }

    /// <summary>
    /// G-36/G-37: PostgreSQL scopes trigger names to the table, so the same trigger name on two tables
    /// in one schema must both be modeled, generated, and created; and because the live loader reads
    /// triggers back, a re-diff after publish must be empty and a second publish must emit nothing.
    /// </summary>
    [TestMethod]
    public async Task PublishFromEmpty_CreatesSameNamedTriggersOnDifferentTables()
    {
        var schema = NewSchemaName();
        await using var connection = await OpenConnectionAsync();
        try
        {
            var sourceModel = BuildModel($"""
                CREATE SCHEMA {schema};
                CREATE TABLE {schema}.orders (id bigint PRIMARY KEY);
                CREATE TABLE {schema}.customers (id bigint PRIMARY KEY);
                CREATE FUNCTION {schema}.audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END; $$;
                CREATE TRIGGER audit_trg AFTER INSERT ON {schema}.orders FOR EACH ROW EXECUTE FUNCTION {schema}.audit();
                CREATE TRIGGER audit_trg AFTER INSERT ON {schema}.customers FOR EACH ROW EXECUTE FUNCTION {schema}.audit();
                """);

            Assert.IsFalse(sourceModel.HasErrors, string.Join(Environment.NewLine, sourceModel.Diagnostics.Select(static diagnostic => diagnostic.Message)));
            Assert.HasCount(2, sourceModel.Triggers);

            var changes = new PgProj.Diff.DiffEngine().Diff(DatabaseModel.Empty, sourceModel);
            var graph = new PgProj.Dependencies.DependencyAnalyzer().Analyze(DatabaseModel.Empty, sourceModel);
            var scriptGenerator = new PgProj.Deploy.ScriptGenerator();
            var script = scriptGenerator.GenerateSqlScript(scriptGenerator.Generate(changes, graph));

            await ExecuteScriptAsync(connection, script);

            await using var checkCommand = new NpgsqlCommand(
                """
                SELECT count(*)
                FROM pg_catalog.pg_trigger trg
                JOIN pg_catalog.pg_class cls ON cls.oid = trg.tgrelid
                JOIN pg_catalog.pg_namespace ns ON ns.oid = cls.relnamespace
                WHERE ns.nspname = @schema AND trg.tgname = 'audit_trg' AND NOT trg.tgisinternal;
                """,
                connection);
            checkCommand.Parameters.AddWithValue("schema", schema);
            var triggerCount = (long)(await checkCommand.ExecuteScalarAsync())!;

            Assert.AreEqual(2L, triggerCount, "Both same-named triggers (one per table) must exist after publish.");

            var liveModel = await LoadSchemaModelAsync(connection, schema);
            Assert.HasCount(2, liveModel.Triggers);

            var diffEngine = new PgProj.Diff.DiffEngine();
            var reDiff = await EndToEndSupport.DiffAsPublishedAsync(connection, liveModel, sourceModel);
            Assert.AreEqual(0, reDiff.Added.Count, "Added: " + string.Join(", ", reDiff.Added.Select(static change => change.Node.Key)));
            Assert.AreEqual(0, reDiff.Removed.Count, "Removed: " + string.Join(", ", reDiff.Removed.Select(static change => change.NodeId)));
            Assert.AreEqual(0, reDiff.Modified.Count, "Modified: " + string.Join("; ", reDiff.Modified.Select(static change => change.NodeId + " [" + string.Join(", ", change.Details.Select(static detail => detail.PropertyName + ": " + detail.OldValue + " -> " + detail.NewValue)) + "]")));

            var secondGraph = new PgProj.Dependencies.DependencyAnalyzer().Analyze(liveModel, sourceModel);
            var secondScript = scriptGenerator.GenerateSqlScript(scriptGenerator.Generate(reDiff, secondGraph));
            Assert.IsFalse(secondScript.Contains("CREATE TRIGGER", StringComparison.OrdinalIgnoreCase), "A second publish must not re-create existing triggers.");
        }
        finally
        {
            await DropSchemaAsync(connection, schema);
        }
    }

    /// <summary>
    /// G-38: PostgreSQL scopes constraint names to the table, so the same CHECK/FOREIGN KEY name on two
    /// tables in one schema must both be modeled, created, and read back; a re-diff must be empty.
    /// The referencing tables (a, b) sort before the referenced table (parent), which used to fail with
    /// 42830 until foreign keys depended on the referenced key (G-41).
    /// </summary>
    [TestMethod]
    public async Task PublishFromEmpty_KeepsSameNamedConstraintsOnDifferentTables()
    {
        var schema = NewSchemaName();
        await using var connection = await OpenConnectionAsync();
        try
        {
            var sourceModel = BuildModel($"""
                CREATE SCHEMA {schema};
                CREATE TABLE {schema}.parent (id bigint PRIMARY KEY);
                CREATE TABLE {schema}.a (id bigint PRIMARY KEY, parent_id bigint, v integer,
                    CONSTRAINT v_positive CHECK (v > 0),
                    CONSTRAINT fk_parent FOREIGN KEY (parent_id) REFERENCES {schema}.parent (id));
                CREATE TABLE {schema}.b (id bigint PRIMARY KEY, parent_id bigint, v integer,
                    CONSTRAINT v_positive CHECK (v > 0),
                    CONSTRAINT fk_parent FOREIGN KEY (parent_id) REFERENCES {schema}.parent (id));
                """);

            Assert.IsFalse(sourceModel.HasErrors, string.Join(Environment.NewLine, sourceModel.Diagnostics.Select(static diagnostic => diagnostic.Message)));

            var diffEngine = new PgProj.Diff.DiffEngine();
            var changes = diffEngine.Diff(DatabaseModel.Empty, sourceModel);
            var graph = new PgProj.Dependencies.DependencyAnalyzer().Analyze(DatabaseModel.Empty, sourceModel);
            var scriptGenerator = new PgProj.Deploy.ScriptGenerator();
            await ExecuteScriptAsync(connection, scriptGenerator.GenerateSqlScript(scriptGenerator.Generate(changes, graph)));

            await using var checkCommand = new NpgsqlCommand(
                """
                SELECT count(*)
                FROM pg_catalog.pg_constraint con
                JOIN pg_catalog.pg_class cls ON cls.oid = con.conrelid
                JOIN pg_catalog.pg_namespace ns ON ns.oid = cls.relnamespace
                WHERE ns.nspname = @schema AND con.conname IN ('v_positive', 'fk_parent');
                """,
                connection);
            checkCommand.Parameters.AddWithValue("schema", schema);
            Assert.AreEqual(4L, (long)(await checkCommand.ExecuteScalarAsync())!, "Both same-named CHECK and FK constraints must exist on both tables.");

            var liveModel = await LoadSchemaModelAsync(connection, schema);
            var reDiff = await EndToEndSupport.DiffAsPublishedAsync(connection, liveModel, sourceModel);
            Assert.AreEqual(0, reDiff.Added.Count, "Added: " + string.Join(", ", reDiff.Added.Select(static change => change.Node.Key)));
            Assert.AreEqual(0, reDiff.Removed.Count, "Removed: " + string.Join(", ", reDiff.Removed.Select(static change => change.NodeId)));
            Assert.AreEqual(0, reDiff.Modified.Count, "Modified: " + string.Join("; ", reDiff.Modified.Select(static change => change.NodeId + " [" + string.Join(", ", change.Details.Select(static detail => detail.PropertyName + ": " + detail.OldValue + " -> " + detail.NewValue)) + "]")));
        }
        finally
        {
            await DropSchemaAsync(connection, schema);
        }
    }
    /// <summary>
    /// G-42: unnamed constraints must receive exactly the names PostgreSQL gives them, because the live
    /// catalog reports those names. This runs a battery of unnamed constraints through a real server and
    /// requires the parser's generated names to equal the server's, including de-duplication suffixes and
    /// 63-byte truncation.
    /// </summary>
    [TestMethod]
    public async Task UnnamedConstraints_GetTheSameNamesPostgreSqlAssigns()
    {
        var schema = NewSchemaName();
        await using var connection = await OpenConnectionAsync();
        try
        {
            var sql = $"""
                CREATE SCHEMA {schema};
                CREATE TABLE {schema}.t1 (
                    id bigint PRIMARY KEY,
                    a integer CHECK (a > 0),
                    b integer CHECK (b > 0 AND b < 100),
                    c integer CHECK (c > 0),
                    d integer,
                    e integer,
                    x integer UNIQUE,
                    CHECK (d > e),
                    CHECK (true),
                    CHECK (a <> 5),
                    CHECK (c * c > 1),
                    UNIQUE (d, e),
                    UNIQUE (d, e)
                );
                CREATE TABLE {schema}.t2 (
                    id bigint PRIMARY KEY,
                    p bigint REFERENCES {schema}.t1 (id),
                    q1 integer,
                    q2 integer,
                    FOREIGN KEY (q1, q2) REFERENCES {schema}.t1 (d, e) DEFERRABLE INITIALLY DEFERRED
                );
                ALTER TABLE {schema}.t2 ADD CHECK (p > 0);
                ALTER TABLE {schema}.t2 ADD UNIQUE (q1);
                ALTER TABLE {schema}.t2 ADD FOREIGN KEY (q2) REFERENCES {schema}.t1 (id);
                CREATE TABLE {schema}.t3 (a integer, EXCLUDE USING btree (a WITH =));
                CREATE TABLE {schema}.t4 (
                    id bigint PRIMARY KEY,
                    u integer,
                    v integer,
                    UNIQUE (id),
                    CONSTRAINT my_u UNIQUE (u),
                    UNIQUE (u),
                    UNIQUE (v),
                    CONSTRAINT my_v UNIQUE (v)
                );
                CREATE TABLE {schema}.t5 (id bigint, UNIQUE (id), PRIMARY KEY (id));
                CREATE TABLE {schema}.t6 (a int, b text, c int, UNIQUE (a) INCLUDE (b, c), EXCLUDE USING btree (c WITH =) INCLUDE (a), EXCLUDE USING btree ((c + 1) WITH =), EXCLUDE USING btree (lower(b) WITH =));
                CREATE TABLE {schema}.a_rather_long_table_name_used_for_truncation_tests (
                    a_similarly_long_column_name_used_for_truncation integer CHECK (a_similarly_long_column_name_used_for_truncation > 0) UNIQUE
                );
                """;

            var namedTree = ConstraintNameAssigner.Assign([_parser.Parse(sql)])[0];
            var expected = namedTree.Constraints
                .Where(static constraint => constraint.ConstraintType is not null && constraint.Name is not null)
                .Select(static constraint => $"{constraint.TableName}.{constraint.Name}")
                .Order(StringComparer.Ordinal)
                .ToArray();

            // Only my_u and my_v are explicit (transferred onto the surviving constraint); all others are generated.
            Assert.IsTrue(namedTree.Constraints.All(static constraint => constraint.IsNameGenerated || constraint.Name is "my_u" or "my_v"));

            foreach (var statement in SqlStatementSplitter.Split(sql))
            {
                await using var command = new NpgsqlCommand(statement, connection);
                await command.ExecuteNonQueryAsync();
            }

            await using var query = new NpgsqlCommand(
                """
                SELECT cls.relname || '.' || con.conname
                FROM pg_catalog.pg_constraint con
                JOIN pg_catalog.pg_class cls ON cls.oid = con.conrelid
                JOIN pg_catalog.pg_namespace ns ON ns.oid = cls.relnamespace
                WHERE ns.nspname = @schema AND con.contype IN ('p', 'u', 'f', 'c', 'x');
                """,
                connection);
            query.Parameters.AddWithValue("schema", schema);

            var actual = new List<string>();
            await using (var reader = await query.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    actual.Add(reader.GetString(0));
                }
            }

            CollectionAssert.AreEqual(
                actual.Order(StringComparer.Ordinal).ToArray(),
                expected,
                "Generated names differ from PostgreSQL's.\nPostgreSQL: " + string.Join(", ", actual) + "\nGenerated : " + string.Join(", ", expected));
        }
        finally
        {
            await DropSchemaAsync(connection, schema);
        }
    }
    /// <summary>
    /// G-42 (cross-file): names generated across several files, in execution order, must equal the server's,
    /// including the rule that index-backed constraints avoid table/index names while CHECK constraints only
    /// avoid other constraint names.
    /// </summary>
    [TestMethod]
    public async Task UnnamedConstraintsAcrossFiles_GetTheSameNamesPostgreSqlAssigns()
    {
        var schema = NewSchemaName();
        await using var connection = await OpenConnectionAsync();
        try
        {
            // Each entry is one "file"; they run in this order, which is the order the names are generated in.
            string[] files =
            [
                $"CREATE SCHEMA {schema}; CREATE TABLE {schema}.t9 (a integer CHECK (a > 0));",
                $"ALTER TABLE {schema}.t9 ADD CHECK (a > 0);",
                $"CREATE TABLE {schema}.t10 (id bigint, a integer); CREATE INDEX t10_pkey ON {schema}.t10 (id);",
                $"ALTER TABLE {schema}.t10 ADD PRIMARY KEY (id);",
                $"CREATE TABLE {schema}.t11_a_key (x integer); CREATE TABLE {schema}.t11 (a integer);",
                $"ALTER TABLE {schema}.t11 ADD UNIQUE (a);",
                $"CREATE TABLE {schema}.t12_a_check (x integer); CREATE TABLE {schema}.t12 (a integer CHECK (a > 0));",
            ];

            var trees = ConstraintNameAssigner.Assign(files.Select(_parser.Parse).ToArray());
            var expected = trees
                .SelectMany(static tree => tree.Constraints)
                .Where(static constraint => constraint.ConstraintType is not null && constraint.Name is not null)
                .Select(static constraint => $"{constraint.TableName}.{constraint.Name}")
                .Order(StringComparer.Ordinal)
                .ToArray();

            foreach (var statement in files.SelectMany(static file => SqlStatementSplitter.Split(file)))
            {
                await using var command = new NpgsqlCommand(statement, connection);
                await command.ExecuteNonQueryAsync();
            }

            await using var query = new NpgsqlCommand(
                """
                SELECT cls.relname || '.' || con.conname
                FROM pg_catalog.pg_constraint con
                JOIN pg_catalog.pg_class cls ON cls.oid = con.conrelid
                JOIN pg_catalog.pg_namespace ns ON ns.oid = cls.relnamespace
                WHERE ns.nspname = @schema AND con.contype IN ('p', 'u', 'f', 'c', 'x');
                """,
                connection);
            query.Parameters.AddWithValue("schema", schema);

            var actual = new List<string>();
            await using (var reader = await query.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    actual.Add(reader.GetString(0));
                }
            }

            CollectionAssert.AreEqual(
                actual.Order(StringComparer.Ordinal).ToArray(),
                expected,
                "Generated names differ from PostgreSQL's.\nPostgreSQL: " + string.Join(", ", actual) + "\nGenerated : " + string.Join(", ", expected));
        }
        finally
        {
            await DropSchemaAsync(connection, schema);
        }
    }
    /// <summary>
    /// G-41: foreign keys must be created after the key they reference and dropped before it, whatever the
    /// object names sort like. Covers an implicit primary-key target (G-43), a UNIQUE target, a composite key
    /// matched in a different column order, a self-reference, and a mutual reference, re-diffs to zero
    /// (PostgreSQL reports the explicit column list), then removes it all.
    /// </summary>
    [TestMethod]
    public async Task ForeignKeys_DeployAndDropInDependencyOrder()
    {
        var schema = NewSchemaName();
        await using var connection = await OpenConnectionAsync();
        try
        {
            var sourceModel = BuildModel($"""
                CREATE SCHEMA {schema};
                CREATE TABLE {schema}.a (
                    id bigint PRIMARY KEY,
                    parent_id bigint REFERENCES {schema}.p,
                    parent_code text REFERENCES {schema}.p (code),
                    x integer,
                    y integer,
                    b_id bigint,
                    FOREIGN KEY (x, y) REFERENCES {schema}.p (x, y)
                );
                CREATE TABLE {schema}.p (
                    id bigint PRIMARY KEY,
                    code text UNIQUE,
                    x integer,
                    y integer,
                    self_id bigint REFERENCES {schema}.p (id),
                    UNIQUE (y, x)
                );
                CREATE TABLE {schema}.b (id bigint PRIMARY KEY, a_id bigint);
                ALTER TABLE {schema}.a ADD FOREIGN KEY (b_id) REFERENCES {schema}.b (id);
                ALTER TABLE {schema}.b ADD FOREIGN KEY (a_id) REFERENCES {schema}.a (id);
                """);

            Assert.IsFalse(sourceModel.HasErrors, string.Join(Environment.NewLine, sourceModel.Diagnostics.Select(static diagnostic => diagnostic.Message)));

            var diffEngine = new PgProj.Diff.DiffEngine();
            var scriptGenerator = new PgProj.Deploy.ScriptGenerator();

            var changes = diffEngine.Diff(DatabaseModel.Empty, sourceModel);
            var graph = new PgProj.Dependencies.DependencyAnalyzer().Analyze(DatabaseModel.Empty, sourceModel);
            await ExecuteScriptAsync(connection, scriptGenerator.GenerateSqlScript(scriptGenerator.Generate(changes, graph)));

            var liveModel = await LoadSchemaModelAsync(connection, schema);
            var reDiff = await EndToEndSupport.DiffAsPublishedAsync(connection, liveModel, sourceModel);
            Assert.AreEqual(0, reDiff.Added.Count, "Added: " + string.Join(", ", reDiff.Added.Select(static change => change.Node.Key)));
            Assert.AreEqual(0, reDiff.Removed.Count, "Removed: " + string.Join(", ", reDiff.Removed.Select(static change => change.NodeId)));
            Assert.AreEqual(0, reDiff.Modified.Count, "Modified: " + string.Join("; ", reDiff.Modified.Select(static change => change.NodeId + " [" + string.Join(", ", change.Details.Select(static detail => detail.PropertyName + ": " + detail.OldValue + " -> " + detail.NewValue)) + "]")));

            // Remove every key and foreign key; the drops must run foreign keys first.
            var strippedModel = BuildModel($"""
                CREATE SCHEMA {schema};
                CREATE TABLE {schema}.a (id bigint, parent_id bigint, parent_code text, x integer, y integer, b_id bigint);
                CREATE TABLE {schema}.p (id bigint, code text, x integer, y integer, self_id bigint);
                CREATE TABLE {schema}.b (id bigint, a_id bigint);
                """);

            var dropChanges = await EndToEndSupport.DiffAsPublishedAsync(connection, liveModel, strippedModel);
            var dropGraph = new PgProj.Dependencies.DependencyAnalyzer().Analyze(liveModel, strippedModel);
            var dropScript = scriptGenerator.GenerateSqlScript(scriptGenerator.Generate(dropChanges, dropGraph));
            await ExecuteScriptAsync(connection, dropScript);

            await using var countCommand = new NpgsqlCommand(
                """
                SELECT count(*)
                FROM pg_catalog.pg_constraint con
                JOIN pg_catalog.pg_class cls ON cls.oid = con.conrelid
                JOIN pg_catalog.pg_namespace ns ON ns.oid = cls.relnamespace
                WHERE ns.nspname = @schema AND con.contype IN ('p', 'u', 'f');
                """,
                connection);
            countCommand.Parameters.AddWithValue("schema", schema);
            Assert.AreEqual(0L, (long)(await countCommand.ExecuteScalarAsync())!, "All keys and foreign keys must be gone.");
        }
        finally
        {
            await DropSchemaAsync(connection, schema);
        }
    }
    private static string ColumnTypeTable(string schema, string varcharLength, string numericSpec, string charLength, string timestampPrecision, string arrayElement) => $"""
        CREATE TABLE {schema}.t (
            c_numeric numeric({numericSpec}),
            c_numeric_zero numeric(10,0),
            c_numeric_lone numeric(5),
            c_numeric_free numeric,
            c_varchar varchar({varcharLength}),
            c_varchar_free varchar,
            c_char char({charLength}),
            c_char_bare char,
            c_bit bit(8),
            c_varbit bit varying(12),
            c_ts timestamp({timestampPrecision}) with time zone,
            c_time time(2) without time zone,
            c_interval interval day to second(2),
            c_interval_precision interval(3),
            c_interval_range interval year to month,
            c_text_array text[],
            c_numeric_array {arrayElement}[],
            c_varchar_array varchar(20)[],
            c_int integer,
            c_plain text
        );
        """;

    /// <summary>
    /// G-45: a column's full type (precision, scale, length, interval fields, array-ness) must survive
    /// deployment. The same definitions are created natively in one schema and deployed through pgproj into
    /// another, and every column's <c>format_type</c> must match; then the modifiers are changed and deployed again.
    /// </summary>
    [TestMethod]
    public async Task ColumnTypesWithModifiersAndArrays_DeployWithFullFidelity()
    {
        var nativeSchema = NewSchemaName();
        var deployedSchema = NewSchemaName();
        var nativeChangedSchema = NewSchemaName();
        await using var connection = await OpenConnectionAsync();
        try
        {
            var diffEngine = new PgProj.Diff.DiffEngine();
            var analyzer = new PgProj.Dependencies.DependencyAnalyzer();
            var scriptGenerator = new PgProj.Deploy.ScriptGenerator();

            // The expected result: PostgreSQL's own interpretation of the definitions.
            await ExecuteRawAsync(connection, $"CREATE SCHEMA {nativeSchema}; " + ColumnTypeTable(nativeSchema, "20", "10,2", "3", "3", "numeric(10,2)"));

            // The same definitions, deployed by pgproj.
            var initialSql = $"CREATE SCHEMA {deployedSchema}; " + ColumnTypeTable(deployedSchema, "20", "10,2", "3", "3", "numeric(10,2)");
            var sourceModel = BuildModel(initialSql);
            Assert.IsFalse(sourceModel.HasErrors, string.Join(Environment.NewLine, sourceModel.Diagnostics.Select(static diagnostic => diagnostic.Message)));
            var createChanges = diffEngine.Diff(DatabaseModel.Empty, sourceModel);
            var createBatch = scriptGenerator.Generate(createChanges, analyzer.Analyze(DatabaseModel.Empty, sourceModel));
            await ExecuteScriptAsync(connection, scriptGenerator.GenerateSqlScript(createBatch));

            CollectionAssert.AreEqual(
                await ReadColumnTypesAsync(connection, nativeSchema),
                await ReadColumnTypesAsync(connection, deployedSchema),
                "Deployed column types differ from what PostgreSQL creates from the same definitions.");

            // The deployed schema must round-trip to no changes.
            var liveModel = await LoadSchemaModelAsync(connection, deployedSchema);
            var reDiff = await EndToEndSupport.DiffAsPublishedAsync(connection, liveModel, sourceModel);
            Assert.AreEqual(0, reDiff.Added.Count + reDiff.Removed.Count + reDiff.Modified.Count, DescribeChanges(reDiff));

            // Change the modifiers and deploy incrementally; the result must match PostgreSQL's interpretation again.
            await ExecuteRawAsync(connection, $"CREATE SCHEMA {nativeChangedSchema}; " + ColumnTypeTable(nativeChangedSchema, "40", "12,3", "5", "6", "numeric(12,2)"));
            var changedSql = $"CREATE SCHEMA {deployedSchema}; " + ColumnTypeTable(deployedSchema, "40", "12,3", "5", "6", "numeric(12,2)");
            var changedModel = BuildModel(changedSql);
            var alterChanges = await EndToEndSupport.DiffAsPublishedAsync(connection, liveModel, changedModel);
            var alterBatch = scriptGenerator.Generate(alterChanges, analyzer.Analyze(liveModel, changedModel));
            var alterScript = scriptGenerator.GenerateSqlScript(alterBatch);
            await ExecuteScriptAsync(connection, alterScript);

            CollectionAssert.AreEqual(
                await ReadColumnTypesAsync(connection, nativeChangedSchema),
                await ReadColumnTypesAsync(connection, deployedSchema),
                "Column types after an incremental change differ from PostgreSQL's interpretation.\n" + alterScript);

            var liveAfterAlter = await LoadSchemaModelAsync(connection, deployedSchema);
            var finalDiff = await EndToEndSupport.DiffAsPublishedAsync(connection, liveAfterAlter, changedModel);
            Assert.AreEqual(0, finalDiff.Added.Count + finalDiff.Removed.Count + finalDiff.Modified.Count, DescribeChanges(finalDiff));
        }
        finally
        {
            await DropSchemaAsync(connection, nativeSchema);
            await DropSchemaAsync(connection, deployedSchema);
            await DropSchemaAsync(connection, nativeChangedSchema);
        }
    }

    private static async Task ExecuteRawAsync(NpgsqlConnection connection, string sql)
    {
        foreach (var statement in SqlStatementSplitter.Split(sql))
        {
            await using var command = new NpgsqlCommand(statement, connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task<string[]> ReadColumnTypesAsync(NpgsqlConnection connection, string schema)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT att.attname || ' ' || pg_catalog.format_type(att.atttypid, att.atttypmod)
            FROM pg_catalog.pg_attribute att
            JOIN pg_catalog.pg_class cls ON cls.oid = att.attrelid
            JOIN pg_catalog.pg_namespace ns ON ns.oid = cls.relnamespace
            WHERE ns.nspname = @schema AND cls.relname = 't' AND att.attnum > 0 AND NOT att.attisdropped;
            """,
            connection);
        command.Parameters.AddWithValue("schema", schema);

        var types = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            types.Add(reader.GetString(0));
        }

        return types.Order(StringComparer.Ordinal).ToArray();
    }

    private static string DescribeChanges(PgProj.Diff.ChangeSet changes)
        => "Added: " + string.Join(", ", changes.Added.Select(static change => change.Node.Key))
            + " | Removed: " + string.Join(", ", changes.Removed.Select(static change => change.NodeId))
            + " | Modified: " + string.Join("; ", changes.Modified.Select(static change => change.NodeId + " [" + string.Join(", ", change.Details.Select(static detail => detail.PropertyName + ": " + detail.OldValue + " -> " + detail.NewValue)) + "]"));
    private static string ArrayRoutines(string schema, bool includeArrayOverloads) => $"""
        CREATE FUNCTION {schema}.f(a integer) RETURNS integer LANGUAGE sql AS $$ SELECT a $$;
        {(includeArrayOverloads ? $"CREATE FUNCTION {schema}.f(a integer[]) RETURNS integer LANGUAGE sql AS $$ SELECT a[1] $$;" : string.Empty)}
        CREATE FUNCTION {schema}.g(a text[], VARIADIC b numeric[]) RETURNS text[] LANGUAGE sql AS $$ SELECT a $$;
        CREATE FUNCTION {schema}.h() RETURNS SETOF integer[] LANGUAGE sql AS $$ SELECT ARRAY[1, 2] $$;
        {(includeArrayOverloads ? $"CREATE PROCEDURE {schema}.p(a integer[]) LANGUAGE sql AS $$ SELECT 1 $$;" : string.Empty)}
        """;

    /// <summary>
    /// G-49: a routine's array parameters and array return type must survive deployment, and a scalar and an array
    /// overload must coexist. The same routines are created natively in one schema and deployed through pgproj into
    /// another, and PostgreSQL's own description of each (identity arguments and result) must match; the deployment
    /// must round-trip to no changes, and dropping just the array overloads must work.
    /// </summary>
    [TestMethod]
    public async Task ArrayRoutines_DeployWithFullFidelity_AndDropCleanly()
    {
        var nativeSchema = NewSchemaName();
        var deployedSchema = NewSchemaName();
        var nativeReducedSchema = NewSchemaName();
        await using var connection = await OpenConnectionAsync();
        try
        {
            var diffEngine = new PgProj.Diff.DiffEngine();
            var analyzer = new PgProj.Dependencies.DependencyAnalyzer();
            var scriptGenerator = new PgProj.Deploy.ScriptGenerator();

            await ExecuteRawAsync(connection, $"CREATE SCHEMA {nativeSchema}; " + ArrayRoutines(nativeSchema, includeArrayOverloads: true));

            var sourceModel = BuildModel($"CREATE SCHEMA {deployedSchema}; " + ArrayRoutines(deployedSchema, includeArrayOverloads: true));
            Assert.IsFalse(sourceModel.HasErrors, string.Join(Environment.NewLine, sourceModel.Diagnostics.Select(static diagnostic => diagnostic.Message)));
            var createBatch = scriptGenerator.Generate(diffEngine.Diff(DatabaseModel.Empty, sourceModel), analyzer.Analyze(DatabaseModel.Empty, sourceModel));
            await ExecuteScriptAsync(connection, scriptGenerator.GenerateSqlScript(createBatch));

            CollectionAssert.AreEqual(
                await ReadRoutineSignaturesAsync(connection, nativeSchema),
                await ReadRoutineSignaturesAsync(connection, deployedSchema),
                "Deployed routine signatures differ from what PostgreSQL creates from the same definitions.");

            var liveModel = await LoadSchemaModelAsync(connection, deployedSchema);
            var reDiff = await EndToEndSupport.DiffAsPublishedAsync(connection, liveModel, sourceModel);
            Assert.AreEqual(0, reDiff.Added.Count + reDiff.Removed.Count + reDiff.Modified.Count, DescribeChanges(reDiff));

            // Remove the array overloads and the procedure; only the scalar f, g, and h must remain.
            await ExecuteRawAsync(connection, $"CREATE SCHEMA {nativeReducedSchema}; " + ArrayRoutines(nativeReducedSchema, includeArrayOverloads: false));
            var reducedModel = BuildModel($"CREATE SCHEMA {deployedSchema}; " + ArrayRoutines(deployedSchema, includeArrayOverloads: false));
            var dropBatch = scriptGenerator.Generate(await EndToEndSupport.DiffAsPublishedAsync(connection, liveModel, reducedModel), analyzer.Analyze(liveModel, reducedModel));
            var dropScript = scriptGenerator.GenerateSqlScript(dropBatch);
            await ExecuteScriptAsync(connection, dropScript);

            CollectionAssert.AreEqual(
                await ReadRoutineSignaturesAsync(connection, nativeReducedSchema),
                await ReadRoutineSignaturesAsync(connection, deployedSchema),
                "Routines after dropping the array overloads differ from PostgreSQL's interpretation.\n" + dropScript);

            var liveAfterDrop = await LoadSchemaModelAsync(connection, deployedSchema);
            var finalDiff = await EndToEndSupport.DiffAsPublishedAsync(connection, liveAfterDrop, reducedModel);
            Assert.AreEqual(0, finalDiff.Added.Count + finalDiff.Removed.Count + finalDiff.Modified.Count, DescribeChanges(finalDiff));
        }
        finally
        {
            await DropSchemaAsync(connection, nativeSchema);
            await DropSchemaAsync(connection, deployedSchema);
            await DropSchemaAsync(connection, nativeReducedSchema);
        }
    }

    private static async Task<string[]> ReadRoutineSignaturesAsync(NpgsqlConnection connection, string schema)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT proc.proname || '(' || pg_catalog.pg_get_function_identity_arguments(proc.oid) || ') ' || proc.prokind::text
                   || ' -> ' || coalesce(pg_catalog.pg_get_function_result(proc.oid), '')
            FROM pg_catalog.pg_proc proc
            JOIN pg_catalog.pg_namespace ns ON ns.oid = proc.pronamespace
            WHERE ns.nspname = @schema;
            """,
            connection);
        command.Parameters.AddWithValue("schema", schema);

        var signatures = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            signatures.Add(reader.GetString(0));
        }

        return signatures.Order(StringComparer.Ordinal).ToArray();
    }
    /// <summary>
    /// G-50: routines must be created after the types (and table row types) in their signatures and dropped before
    /// them. Every routine here is named so that it sorts before the type or table it uses, so an ordering that only
    /// follows key order fails on the real server (42704).
    /// </summary>
    [TestMethod]
    public async Task RoutinesOverUserDefinedTypes_DeployAndDropInDependencyOrder()
    {
        var schema = NewSchemaName();
        await using var connection = await OpenConnectionAsync();
        try
        {
            var sourceModel = BuildModel($"""
                CREATE SCHEMA {schema};
                CREATE TYPE {schema}.mood AS ENUM ('happy', 'sad');
                CREATE TYPE {schema}.pt AS (x integer, y integer);
                CREATE TABLE {schema}.orders (id bigint PRIMARY KEY);
                CREATE FUNCTION {schema}.a_takes_mood(m {schema}.mood) RETURNS integer LANGUAGE sql AS $$ SELECT 1 $$;
                CREATE FUNCTION {schema}.a_returns_mood() RETURNS {schema}.mood LANGUAGE sql AS $$ SELECT 'happy'::{schema}.mood $$;
                CREATE FUNCTION {schema}.a_takes_moods(m {schema}.mood[]) RETURNS integer LANGUAGE sql AS $$ SELECT 1 $$;
                CREATE FUNCTION {schema}.a_composite(p {schema}.pt) RETURNS {schema}.pt LANGUAGE sql AS $$ SELECT p $$;
                CREATE FUNCTION {schema}.a_rows() RETURNS SETOF {schema}.orders LANGUAGE sql AS $$ SELECT * FROM {schema}.orders $$;
                CREATE PROCEDURE {schema}.a_proc(m {schema}.mood) LANGUAGE sql AS $$ SELECT 1 $$;
                """);
            Assert.IsFalse(sourceModel.HasErrors, string.Join(Environment.NewLine, sourceModel.Diagnostics.Select(static diagnostic => diagnostic.Message)));

            var diffEngine = new PgProj.Diff.DiffEngine();
            var analyzer = new PgProj.Dependencies.DependencyAnalyzer();
            var scriptGenerator = new PgProj.Deploy.ScriptGenerator();

            var createBatch = scriptGenerator.Generate(diffEngine.Diff(DatabaseModel.Empty, sourceModel), analyzer.Analyze(DatabaseModel.Empty, sourceModel));
            await ExecuteScriptAsync(connection, scriptGenerator.GenerateSqlScript(createBatch));

            var liveModel = await LoadSchemaModelAsync(connection, schema);
            var reDiff = await EndToEndSupport.DiffAsPublishedAsync(connection, liveModel, sourceModel);
            Assert.AreEqual(0, reDiff.Added.Count + reDiff.Removed.Count + reDiff.Modified.Count, DescribeChanges(reDiff));

            // Remove everything but the schema; the routines must be dropped before the types and table they use.
            var emptiedModel = BuildModel($"CREATE SCHEMA {schema};");
            var dropBatch = scriptGenerator.Generate(await EndToEndSupport.DiffAsPublishedAsync(connection, liveModel, emptiedModel), analyzer.Analyze(liveModel, emptiedModel));
            await ExecuteScriptAsync(connection, scriptGenerator.GenerateSqlScript(dropBatch));

            Assert.AreEqual(0L, await CountAsync(connection, "SELECT count(*) FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = @schema;", schema), "Routines remain.");
            Assert.AreEqual(0L, await CountAsync(connection, "SELECT count(*) FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = @schema;", schema), "Tables or composite types remain.");
            Assert.AreEqual(0L, await CountAsync(connection, "SELECT count(*) FROM pg_catalog.pg_type t JOIN pg_catalog.pg_namespace n ON n.oid = t.typnamespace WHERE n.nspname = @schema AND t.typtype = 'e';", schema), "Enum types remain.");
        }
        finally
        {
            await DropSchemaAsync(connection, schema);
        }
    }

    private static async Task<long> CountAsync(NpgsqlConnection connection, string sql, string schema)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("schema", schema);
        return (long)(await command.ExecuteScalarAsync())!;
    }
    /// <summary>
    /// G-51: calls to built-in functions in a default, a CHECK, an index expression, and a view must build,
    /// deploy to a real server, and converge (re-diff empty, second publish emits nothing).
    /// </summary>
    [TestMethod]
    public async Task BuiltInFunctionCalls_DeployAndReDiffToZero()
    {
        var schema = NewSchemaName();
        await using var connection = await OpenConnectionAsync();
        try
        {
            var source = $"""
                CREATE SCHEMA {schema};
                CREATE TABLE {schema}.items (
                    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
                    name text NOT NULL,
                    qty integer NOT NULL DEFAULT 0,
                    created_at timestamptz NOT NULL DEFAULT now(),
                    CONSTRAINT items_name_lower CHECK (lower(name) = name)
                );
                CREATE INDEX items_name_lower_idx ON {schema}.items (lower(name));
                CREATE VIEW {schema}.item_totals AS
                    SELECT name, sum(qty) AS total, count(*) AS n, coalesce(max(qty), 0) AS top
                    FROM {schema}.items GROUP BY name;
                """;
            var sourceModel = BuildModel(source);
            Assert.AreEqual(
                0,
                sourceModel.Diagnostics.Count(static diagnostic => diagnostic.Message.Contains("missing function", StringComparison.OrdinalIgnoreCase)),
                string.Join(Environment.NewLine, sourceModel.Diagnostics.Select(static diagnostic => diagnostic.Message)));

            var dependencyAnalyzer = new PgProj.Dependencies.DependencyAnalyzer();
            var diffEngine = new PgProj.Diff.DiffEngine();
            var scriptGenerator = new PgProj.Deploy.ScriptGenerator();

            var changes = diffEngine.Diff(DatabaseModel.Empty, sourceModel);
            var graph = dependencyAnalyzer.Analyze(DatabaseModel.Empty, sourceModel);
            await ExecuteScriptAsync(connection, scriptGenerator.GenerateSqlScript(scriptGenerator.Generate(changes, graph)));

            var liveModel = await LoadSchemaModelAsync(connection, schema);
            var reDiff = await EndToEndSupport.DiffAsPublishedAsync(connection, liveModel, sourceModel);
            Assert.AreEqual(0, reDiff.Added.Count + reDiff.Removed.Count + reDiff.Modified.Count, DescribeChanges(reDiff));
            Assert.AreEqual(
                0,
                scriptGenerator.Generate(reDiff, dependencyAnalyzer.Analyze(liveModel, sourceModel)).Operations.Count,
                "A second publish must emit no operations.");
        }
        finally
        {
            await DropSchemaAsync(connection, schema);
        }
    }

    private static string DefaultsTable(string schema, string a, string b, string c, string d) => $"""
        CREATE SCHEMA {schema};
        CREATE TABLE {schema}.t (
            id integer PRIMARY KEY,
            a integer {a},
            b text {b},
            c timestamptz {c},
            d integer {d}
        );
        """;

    /// <summary>Runs the plan from <paramref name="live"/> to <paramref name="target"/> and returns the SQL it executed.</summary>
    private async Task<string> PublishAsync(NpgsqlConnection connection, DatabaseModel live, DatabaseModel target)
    {
        var scriptGenerator = new PgProj.Deploy.ScriptGenerator();
        var batch = scriptGenerator.Generate(
            await EndToEndSupport.DiffAsPublishedAsync(connection, live, target),
            new PgProj.Dependencies.DependencyAnalyzer().Analyze(live, target));
        var script = scriptGenerator.GenerateSqlScript(batch);
        if (batch.Operations.Count > 0)
        {
            await ExecuteScriptAsync(connection, script);
        }

        return script;
    }

    private static async Task<Dictionary<string, string>> ReadLiveDefaultsAsync(NpgsqlConnection connection, string schema)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT att.attname, pg_catalog.pg_get_expr(def.adbin, def.adrelid)
            FROM pg_catalog.pg_attrdef def
            JOIN pg_catalog.pg_attribute att ON att.attrelid = def.adrelid AND att.attnum = def.adnum
            JOIN pg_catalog.pg_class cls ON cls.oid = def.adrelid
            JOIN pg_catalog.pg_namespace ns ON ns.oid = cls.relnamespace
            WHERE ns.nspname = @schema AND cls.relname = 't';
            """,
            connection);
        command.Parameters.AddWithValue("schema", schema);

        var defaults = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            defaults[reader.GetString(0)] = reader.GetString(1);
        }

        return defaults;
    }

    /// <summary>
    /// G-56/G-55: column defaults used to be dropped from the model, so they were never deployed. Every common shape
    /// must now deploy, read back from PostgreSQL in its own rendering (<c>'x'::text</c>, <c>'-1'::integer</c>,
    /// <c>nextval('s'::regclass)</c>), and still re-diff to zero, with a second publish emitting nothing.
    /// </summary>
    [TestMethod]
    public async Task ColumnDefaults_DeployAndReDiffToZero()
    {
        var schema = NewSchemaName();
        await using var connection = await OpenConnectionAsync();
        try
        {
            const string emptyBraces = "{}";
            var source = $"""
                CREATE SCHEMA {schema};
                CREATE SEQUENCE {schema}.ticket_seq;
                CREATE TABLE {schema}.t (
                    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
                    a integer NOT NULL DEFAULT 0,
                    b integer DEFAULT -1,
                    c text DEFAULT 'pending',
                    d varchar(20) DEFAULT 'x',
                    e boolean DEFAULT true,
                    f timestamptz DEFAULT now(),
                    g timestamptz DEFAULT CURRENT_TIMESTAMP,
                    h numeric(10,2) DEFAULT 1.50,
                    i text[] DEFAULT '{emptyBraces}',
                    j jsonb DEFAULT '{emptyBraces}',
                    k date DEFAULT '2020-01-01',
                    l interval DEFAULT '1 day',
                    m integer DEFAULT (1 + 2),
                    n bigint DEFAULT 9000000000,
                    o text DEFAULT coalesce(current_setting('app.none', true), 'none'),
                    p timestamptz DEFAULT date_trunc('day', now()),
                    q integer[] DEFAULT ARRAY[1,2],
                    r bigint DEFAULT nextval('{schema}.ticket_seq'),
                    s text DEFAULT NULL,
                    u text DEFAULT lower('ABC'),
                    v integer DEFAULT 1+1,
                    w text DEFAULT 'it''s, (fine)'
                );
                """;
            var sourceModel = BuildModel(source);
            Assert.AreEqual(0, sourceModel.Diagnostics.Count(static diagnostic => diagnostic.Severity == SemanticDiagnosticSeverity.Error),
                string.Join(Environment.NewLine, sourceModel.Diagnostics.Select(static diagnostic => diagnostic.Message)));

            await PublishAsync(connection, DatabaseModel.Empty, sourceModel);

            // The defaults are really on the table now (before this change they were dropped from CREATE TABLE).
            var liveDefaults = await ReadLiveDefaultsAsync(connection, schema);
            foreach (var column in new[] { "id", "a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l", "m", "n", "o", "p", "q", "r", "u", "v", "w" })
            {
                Assert.IsTrue(liveDefaults.ContainsKey(column), $"Column '{column}' has no default on the server. Defaults present: {string.Join(", ", liveDefaults.Keys.Order(StringComparer.Ordinal))}");
            }

            Assert.IsFalse(liveDefaults.ContainsKey("s"), "DEFAULT NULL is stored by PostgreSQL as no default.");
            Assert.AreEqual("0", liveDefaults["a"]);
            Assert.AreEqual("'pending'::text", liveDefaults["c"]);
            Assert.AreEqual("'it''s, (fine)'::text", liveDefaults["w"]);

            var liveModel = await LoadSchemaModelAsync(connection, schema);
            var reDiff = await EndToEndSupport.DiffAsPublishedAsync(connection, liveModel, sourceModel);
            Assert.AreEqual(0, reDiff.Added.Count + reDiff.Removed.Count + reDiff.Modified.Count, DescribeChanges(reDiff));

            var second = new PgProj.Deploy.ScriptGenerator().Generate(reDiff, new PgProj.Dependencies.DependencyAnalyzer().Analyze(liveModel, sourceModel));
            Assert.AreEqual(0, second.Operations.Count, "A second publish must emit no operations.");
        }
        finally
        {
            await DropSchemaAsync(connection, schema);
        }
    }

    /// <summary>
    /// G-56: an identity column (<c>GENERATED BY DEFAULT AS IDENTITY</c>) next to defaulted columns is not mistaken for a
    /// default expression, and converges. (<c>serial</c> is not covered: it does not deploy to a real server at all, G-58.)
    /// </summary>
    [TestMethod]
    public async Task IdentityColumnBesideDefaults_DeploysAndReDiffsToZero()
    {
        var schema = NewSchemaName();
        await using var connection = await OpenConnectionAsync();
        try
        {
            var sourceModel = BuildModel($"""
                CREATE SCHEMA {schema};
                CREATE TABLE {schema}.t (
                    id integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                    ident integer GENERATED ALWAYS AS IDENTITY,
                    name text NOT NULL DEFAULT 'x',
                    created timestamptz NOT NULL DEFAULT now()
                );
                """);

            await PublishAsync(connection, DatabaseModel.Empty, sourceModel);

            var liveModel = await LoadSchemaModelAsync(connection, schema);
            var reDiff = await EndToEndSupport.DiffAsPublishedAsync(connection, liveModel, sourceModel);

            Assert.AreEqual(0, reDiff.Added.Count + reDiff.Removed.Count + reDiff.Modified.Count, DescribeChanges(reDiff));
        }
        finally
        {
            await DropSchemaAsync(connection, schema);
        }
    }

    /// <summary>
    /// G-61: grants did not converge after publish: a second publish revoked and re-granted privileges, including PostgreSQL's
    /// implicit ones (the owner's, and <c>PUBLIC</c>'s default <c>EXECUTE</c> on functions). A project's grants must deploy, read
    /// back as the same grants (an empty re-diff and an empty second publish), an explicit grant the project does not list must
    /// still be revoked while the implicit ones are not, a grant over an empty schema must take effect once the schema has such an
    /// object, and narrowing a grant must really narrow it.
    /// </summary>
    [TestMethod]
    public async Task Grants_DeployConvergeAndOnlyExplicitUnlistedGrantsAreRevoked()
    {
        var schema = NewSchemaName();
        var role = $"g61_{Guid.NewGuid():N}"[..20];
        var other = role + "o";
        var outsider = role + "x";
        await using var connection = await OpenConnectionAsync();
        try
        {
            await ExecuteRawAsync(connection, $"CREATE ROLE {role} NOLOGIN; CREATE ROLE {other} NOLOGIN; CREATE ROLE {outsider} NOLOGIN;");

            string Source(string schemaGrant, string extraObjects) => $"""
                CREATE SCHEMA {schema};
                CREATE TABLE {schema}.t (id integer PRIMARY KEY);
                CREATE VIEW {schema}.v AS SELECT id FROM {schema}.t;
                CREATE FUNCTION {schema}.f() RETURNS integer LANGUAGE sql AS $$ SELECT 1 $$;
                {extraObjects}
                GRANT USAGE ON SCHEMA {schema} TO PUBLIC;
                {schemaGrant}
                GRANT SELECT ON ALL TABLES IN SCHEMA {schema} TO {role};
                GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA {schema} TO {role};
                GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA {schema} TO {role};
                GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA {schema} TO PUBLIC;
                ALTER DEFAULT PRIVILEGES IN SCHEMA {schema} GRANT ALL ON TABLES TO {role};
                """;

            async Task<bool> Check(string sql)
            {
                await using var command = new NpgsqlCommand(sql, connection);
                return (bool)(await command.ExecuteScalarAsync())!;
            }

            async Task AssertConverged(DatabaseModel target, string step)
            {
                var live = await LoadSchemaModelAsync(connection, schema);
                var reDiff = await EndToEndSupport.DiffAsPublishedAsync(connection, live, target);
                Assert.AreEqual(0, reDiff.Added.Count + reDiff.Removed.Count + reDiff.Modified.Count, step + ": " + DescribeChanges(reDiff));
                Assert.AreEqual(
                    0,
                    new PgProj.Deploy.ScriptGenerator().Generate(reDiff, new PgProj.Dependencies.DependencyAnalyzer().Analyze(live, target)).Operations.Count,
                    step + ": a second publish must emit no operations.");
            }

            // 1. Deploy: nothing is revoked, and what was asked for is granted (SELECT reaches the view too).
            var first = BuildModel(Source($"GRANT USAGE, CREATE ON SCHEMA {schema} TO {role};", string.Empty));
            var script = await PublishAsync(connection, DatabaseModel.Empty, first);
            Assert.IsFalse(script.Contains("REVOKE", StringComparison.Ordinal), script);
            Assert.IsTrue(await Check($"SELECT has_table_privilege('{role}', '{schema}.t', 'SELECT')"));
            Assert.IsTrue(await Check($"SELECT has_table_privilege('{role}', '{schema}.v', 'SELECT')"), "ALL TABLES includes the view.");
            await AssertConverged(first, "after the first publish");

            // 2. An explicit grant the project does not list is revoked; PUBLIC's default EXECUTE is not.
            await ExecuteRawAsync(connection, $"GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA {schema} TO {other};");
            var revokeScript = await PublishAsync(connection, await LoadSchemaModelAsync(connection, schema), first);
            StringAssert.Contains(revokeScript, $"REVOKE ALL PRIVILEGES ON ALL FUNCTIONS IN SCHEMA \"{schema}\" FROM \"{other}\"", revokeScript);
            Assert.IsFalse(revokeScript.Contains("FROM PUBLIC", StringComparison.Ordinal), "PUBLIC's EXECUTE must not be revoked.\n" + revokeScript);
            Assert.IsFalse(revokeScript.Contains($"FROM \"{schema}\"", StringComparison.Ordinal) || revokeScript.Contains("FROM \"postgres\"", StringComparison.Ordinal), "The owner's own privileges must not be revoked.\n" + revokeScript);
            Assert.IsFalse(await Check($"SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_proc p, LATERAL pg_catalog.aclexplode(p.proacl) a WHERE p.pronamespace = '{schema}'::regnamespace AND a.grantee = '{other}'::regrole)"), "The unlisted grant must be gone.");
            Assert.IsTrue(await Check($"SELECT has_function_privilege('{outsider}', '{schema}.f()', 'EXECUTE')"), "PUBLIC's default EXECUTE must remain.");
            await AssertConverged(first, "after revoking the unlisted grant");

            // 3. The grant over sequences had nothing to apply to; it takes effect once the schema has a sequence.
            var withSequence = BuildModel(Source($"GRANT USAGE, CREATE ON SCHEMA {schema} TO {role};", $"CREATE SEQUENCE {schema}.q;"));
            var sequenceScript = await PublishAsync(connection, await LoadSchemaModelAsync(connection, schema), withSequence);
            StringAssert.Contains(sequenceScript, "ON ALL SEQUENCES IN SCHEMA", sequenceScript);
            Assert.IsTrue(await Check($"SELECT has_sequence_privilege('{role}', '{schema}.q', 'USAGE')"), sequenceScript);
            await AssertConverged(withSequence, "after adding a sequence");

            // 4. Narrowing a grant really narrows it.
            var narrowed = BuildModel(Source($"GRANT USAGE ON SCHEMA {schema} TO {role};", $"CREATE SEQUENCE {schema}.q;"));
            await PublishAsync(connection, await LoadSchemaModelAsync(connection, schema), narrowed);
            Assert.IsTrue(await Check($"SELECT has_schema_privilege('{role}', '{schema}', 'USAGE')"));
            Assert.IsFalse(await Check($"SELECT has_schema_privilege('{role}', '{schema}', 'CREATE')"), "CREATE must have been revoked.");
            await AssertConverged(narrowed, "after narrowing the schema grant");
        }
        finally
        {
            await DropSchemaAsync(connection, schema);
            await ExecuteRawAsync(connection, $"DROP OWNED BY {role}, {other}, {outsider}; DROP ROLE IF EXISTS {role}, {other}, {outsider};");
        }
    }

    /// <summary>
    /// G-62: a column written with an explicit <c>NULL</c> re-diffed forever, because the live catalog reports a nullable column
    /// without <c>NOT NULL</c>. Explicit <c>NULL</c>, unspecified, and <c>NOT NULL</c> columns must deploy and converge, a real
    /// change in either direction must deploy, and nothing else must be emitted.
    /// </summary>
    [TestMethod]
    public async Task ExplicitNullColumns_DeployAndConvergeAndRealChangesStillDeploy()
    {
        var schema = NewSchemaName();
        await using var connection = await OpenConnectionAsync();
        try
        {
            string Source(string a, string b, string c) => $"""
                CREATE SCHEMA {schema};
                CREATE TABLE {schema}.t (
                    id integer PRIMARY KEY,
                    a text {a},
                    b text {b},
                    c text {c},
                    d timestamp without time zone NULL,
                    e jsonb NULL
                );
                """;

            var first = BuildModel(Source("NULL", "NOT NULL", string.Empty));
            await PublishAsync(connection, DatabaseModel.Empty, first);

            var live = await LoadSchemaModelAsync(connection, schema);
            var reDiff = await EndToEndSupport.DiffAsPublishedAsync(connection, live, first);
            Assert.AreEqual(0, reDiff.Added.Count + reDiff.Removed.Count + reDiff.Modified.Count, DescribeChanges(reDiff));
            Assert.AreEqual(
                0,
                new PgProj.Deploy.ScriptGenerator().Generate(reDiff, new PgProj.Dependencies.DependencyAnalyzer().Analyze(live, first)).Operations.Count,
                "A second publish must emit no operations.");

            // a: NULL -> not named (no change); b: NOT NULL -> NULL; c: not named -> NOT NULL (needs a value first).
            await ExecuteRawAsync(connection, $"INSERT INTO {schema}.t (id, a, b, c) VALUES (1, NULL, 'b', 'c');");
            var revised = BuildModel(Source(string.Empty, "NULL", "NOT NULL"));
            var script = await PublishAsync(connection, live, revised);

            StringAssert.Contains(script, "\"b\" DROP NOT NULL", script);
            StringAssert.Contains(script, "\"c\" SET NOT NULL", script);
            Assert.IsFalse(script.Contains("\"a\"", StringComparison.Ordinal), "The explicit NULL to unspecified change must emit nothing.\n" + script);

            var liveAfter = await LoadSchemaModelAsync(connection, schema);
            var reDiffAfter = await EndToEndSupport.DiffAsPublishedAsync(connection, liveAfter, revised);
            Assert.AreEqual(0, reDiffAfter.Added.Count + reDiffAfter.Removed.Count + reDiffAfter.Modified.Count, DescribeChanges(reDiffAfter));
        }
        finally
        {
            await DropSchemaAsync(connection, schema);
        }
    }

    /// <summary>
    /// G-63: a default that calls a function schema-qualified with <c>public</c> (or <c>pg_catalog</c>) re-diffed forever, because
    /// PostgreSQL reads it back without the schema it is on the search path of. Names in any other schema are printed qualified and
    /// already matched. The function here is a real one in <c>public</c>, so the server does the rendering.
    /// </summary>
    [TestMethod]
    public async Task SearchPathQualifiedFunctionDefaults_DeployAndConverge()
    {
        var schema = NewSchemaName();
        var function = $"g63_{Guid.NewGuid():N}"[..20];
        await using var connection = await OpenConnectionAsync();
        try
        {
            await ExecuteRawAsync(connection, $"CREATE FUNCTION public.{function}() RETURNS integer LANGUAGE sql IMMUTABLE AS $$ SELECT 7 $$;");

            var sourceModel = BuildModel($"""
                CREATE SCHEMA {schema};
                CREATE TABLE {schema}.t (
                    id integer PRIMARY KEY,
                    a integer NOT NULL DEFAULT public.{function}(),
                    b timestamptz NOT NULL DEFAULT pg_catalog.now(),
                    c integer NOT NULL DEFAULT {function}()
                );
                """);

            await PublishAsync(connection, DatabaseModel.Empty, sourceModel);
            await ExecuteRawAsync(connection, $"INSERT INTO {schema}.t (id) VALUES (1);");
            await using (var check = new NpgsqlCommand($"SELECT a + c FROM {schema}.t", connection))
            {
                Assert.AreEqual(14, (int)(await check.ExecuteScalarAsync())!, "The defaults must call the function.");
            }

            var live = await LoadSchemaModelAsync(connection, schema);
            var reDiff = await EndToEndSupport.DiffAsPublishedAsync(connection, live, sourceModel);
            Assert.AreEqual(0, reDiff.Added.Count + reDiff.Removed.Count + reDiff.Modified.Count, DescribeChanges(reDiff));
            Assert.AreEqual(
                0,
                new PgProj.Deploy.ScriptGenerator().Generate(reDiff, new PgProj.Dependencies.DependencyAnalyzer().Analyze(live, sourceModel)).Operations.Count,
                "A second publish must emit no operations.");
        }
        finally
        {
            await DropSchemaAsync(connection, schema);
            await ExecuteRawAsync(connection, $"DROP FUNCTION IF EXISTS public.{function}();");
        }
    }

    /// <summary>
    /// G-60: the sample project's grant forms used to produce an invalid statement (<c>GRANT ALL PRIVILEGES, USAGE ON SCHEMA
    /// ... TO PUBLIC</c>) and reduce <c>ALL PRIVILEGES</c> on tables to <c>USAGE</c>, because a privilege list was read across
    /// neighbouring statements. They must deploy, and PostgreSQL must record the privileges the project asked for.
    /// </summary>
    [TestMethod]
    public async Task SampleGrantForms_DeployAndGrantWhatTheProjectAsked()
    {
        var schema = NewSchemaName();
        var role = $"g60_{Guid.NewGuid():N}"[..20];
        var outsider = role + "x";
        await using var connection = await OpenConnectionAsync();
        try
        {
            await ExecuteRawAsync(connection, $"CREATE ROLE {role} NOLOGIN; CREATE ROLE {outsider} NOLOGIN;");

            var sourceModel = BuildModel($"""
                CREATE SCHEMA {schema};
                CREATE TABLE {schema}.t (id integer PRIMARY KEY);
                CREATE SEQUENCE {schema}.s;
                CREATE FUNCTION {schema}.f() RETURNS integer LANGUAGE sql AS $$ SELECT 1 $$;

                GRANT USAGE ON SCHEMA {schema} TO PUBLIC;
                GRANT USAGE, CREATE ON SCHEMA {schema} TO {role};
                GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA {schema} TO {role};
                GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA {schema} TO {role};
                GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA {schema} TO {role};
                ALTER DEFAULT PRIVILEGES IN SCHEMA {schema}
                    GRANT ALL ON TABLES TO {role};
                ALTER DEFAULT PRIVILEGES IN SCHEMA {schema}
                    GRANT ALL ON SEQUENCES TO {role};
                ALTER DEFAULT PRIVILEGES IN SCHEMA {schema}
                    GRANT EXECUTE ON FUNCTIONS TO {role};
                """);
            Assert.AreEqual(8, sourceModel.Grants.Count, string.Join("\n", sourceModel.Grants.Keys));

            var script = await PublishAsync(connection, DatabaseModel.Empty, sourceModel);
            Assert.IsFalse(script.Contains("PRIVILEGES, ", StringComparison.Ordinal), script);

            async Task<bool> Check(string sql)
            {
                await using var command = new NpgsqlCommand(sql, connection);
                return (bool)(await command.ExecuteScalarAsync())!;
            }

            // ALL PRIVILEGES on the tables, not just USAGE.
            foreach (var privilege in new[] { "SELECT", "INSERT", "UPDATE", "DELETE", "TRUNCATE", "REFERENCES", "TRIGGER" })
            {
                Assert.IsTrue(await Check($"SELECT has_table_privilege('{role}', '{schema}.t', '{privilege}')"), $"The role must have {privilege} on the table.");
            }

            Assert.IsTrue(await Check($"SELECT has_sequence_privilege('{role}', '{schema}.s', 'USAGE')"));
            Assert.IsTrue(await Check($"SELECT has_function_privilege('{role}', '{schema}.f()', 'EXECUTE')"));
            Assert.IsTrue(await Check($"SELECT has_schema_privilege('{role}', '{schema}', 'CREATE')"));

            // PUBLIC was granted USAGE and nothing more: a role with no grants of its own can use the schema but not create in it.
            Assert.IsTrue(await Check($"SELECT has_schema_privilege('{outsider}', '{schema}', 'USAGE')"));
            Assert.IsFalse(await Check($"SELECT has_schema_privilege('{outsider}', '{schema}', 'CREATE')"), "PUBLIC must not be able to create in the schema.");

            // The default privileges apply to a table created afterwards.
            await ExecuteRawAsync(connection, $"CREATE TABLE {schema}.later (id integer);");
            Assert.IsTrue(await Check($"SELECT has_table_privilege('{role}', '{schema}.later', 'DELETE')"), "Default privileges must reach a later table.");
        }
        finally
        {
            await DropSchemaAsync(connection, schema);
            await ExecuteRawAsync(connection, $"DROP OWNED BY {role}; DROP ROLE IF EXISTS {role}; DROP ROLE IF EXISTS {outsider};");
        }
    }

    /// <summary>
    /// G-52: objects removed while a surviving object stops using them (enum and composite types, two functions, a serial
    /// sequence) were dropped before the alter that ends the dependency, so PostgreSQL refused with <c>2BP01</c>. They must be
    /// dropped after the surviving table, view, and constraint have changed, existing data must survive the retyping, and the
    /// result must converge.
    /// </summary>
    [TestMethod]
    public async Task RemovedObjectsThatSurvivorsStopUsing_AreDroppedAfterTheAlter()
    {
        var schema = NewSchemaName();
        await using var connection = await OpenConnectionAsync();
        try
        {
            var oldModel = BuildModel($"""
                CREATE SCHEMA {schema};
                CREATE TYPE {schema}.mood AS ENUM ('happy', 'sad');
                CREATE TYPE {schema}.pt AS (x integer, m {schema}.mood);
                CREATE FUNCTION {schema}.z_bump(x integer) RETURNS integer LANGUAGE sql IMMUTABLE AS $$ SELECT x + 1 $$;
                CREATE FUNCTION {schema}.z_ok(x integer) RETURNS boolean LANGUAGE sql IMMUTABLE AS $$ SELECT x > 0 $$;
                CREATE TABLE {schema}.a_t (
                    id serial PRIMARY KEY,
                    m {schema}.mood,
                    p {schema}.pt,
                    n integer DEFAULT {schema}.z_bump(1),
                    CONSTRAINT n_ok CHECK ({schema}.z_ok(n))
                );
                CREATE VIEW {schema}.a_v AS SELECT {schema}.z_bump(n) AS b FROM {schema}.a_t;
                """);
            await PublishAsync(connection, DatabaseModel.Empty, oldModel);
            await ExecuteRawAsync(connection, $"INSERT INTO {schema}.a_t (m, n) VALUES ('happy', 5);");

            var newModel = BuildModel($"""
                CREATE SCHEMA {schema};
                CREATE TABLE {schema}.a_t (
                    id integer PRIMARY KEY,
                    m text,
                    p text,
                    n integer,
                    CONSTRAINT n_ok CHECK (n > 0)
                );
                CREATE VIEW {schema}.a_v AS SELECT n AS b FROM {schema}.a_t;
                """);

            var live = await LoadSchemaModelAsync(connection, schema);
            var script = await PublishAsync(connection, live, newModel);

            // Everything that was in use is dropped only after the table has been altered.
            var alter = script.IndexOf("ALTER TABLE", StringComparison.Ordinal);
            foreach (var dropped in new[] { "DROP TYPE", "DROP FUNCTION", "DROP SEQUENCE" })
            {
                var drop = script.IndexOf(dropped, StringComparison.Ordinal);
                Assert.IsGreaterThan(-1, drop, $"Expected a {dropped} in the script.\n{script}");
                Assert.IsGreaterThan(alter, drop, $"{dropped} must come after the ALTER TABLE.\n{script}");
            }

            Assert.AreEqual(1L, await CountAsync(connection, $"SELECT count(*) FROM {schema}.a_t WHERE m = 'happy' AND n = 5", schema), "The data must survive the retyping.");
            Assert.AreEqual(0L, await CountAsync(connection, "SELECT count(*) FROM pg_catalog.pg_type t JOIN pg_catalog.pg_namespace n ON n.oid = t.typnamespace WHERE n.nspname = @schema AND t.typname IN ('mood', 'pt')", schema));
            Assert.AreEqual(0L, await CountAsync(connection, "SELECT count(*) FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = @schema", schema));

            var liveAfter = await LoadSchemaModelAsync(connection, schema);
            var reDiff = await EndToEndSupport.DiffAsPublishedAsync(connection, liveAfter, newModel);
            Assert.AreEqual(0, reDiff.Added.Count + reDiff.Removed.Count + reDiff.Modified.Count, DescribeChanges(reDiff));
        }
        finally
        {
            await DropSchemaAsync(connection, schema);
        }
    }

    /// <summary>
    /// G-52: the same for an extension: a column retyped from an extension's type (<c>citext</c>) while the extension is
    /// removed. Extensions are dropped last, after the alter.
    /// </summary>
    [TestMethod]
    public async Task RemovedExtensionThatAColumnStopsUsing_IsDroppedAfterTheAlter()
    {
        var schema = NewSchemaName();
        await using var connection = await OpenConnectionAsync();
        try
        {
            var oldModel = BuildModel($"""
                CREATE SCHEMA {schema};
                CREATE EXTENSION citext SCHEMA {schema};
                CREATE TABLE {schema}.t (id integer PRIMARY KEY, c {schema}.citext);
                """);
            await PublishAsync(connection, DatabaseModel.Empty, oldModel);
            await ExecuteRawAsync(connection, $"INSERT INTO {schema}.t (id, c) VALUES (1, 'MixedCase');");

            var newModel = BuildModel($"""
                CREATE SCHEMA {schema};
                CREATE TABLE {schema}.t (id integer PRIMARY KEY, c text);
                """);

            var live = await LoadSchemaModelAsync(connection, schema);
            var script = await PublishAsync(connection, live, newModel);

            Assert.IsGreaterThan(script.IndexOf("ALTER TABLE", StringComparison.Ordinal), script.IndexOf("DROP EXTENSION", StringComparison.Ordinal), script);
            Assert.AreEqual(1L, await CountAsync(connection, $"SELECT count(*) FROM {schema}.t WHERE c = 'MixedCase'", schema), "The data must survive the retyping.");
            Assert.AreEqual(0L, await CountAsync(connection, "SELECT count(*) FROM pg_catalog.pg_extension WHERE extname = 'citext' AND extnamespace = (SELECT oid FROM pg_catalog.pg_namespace WHERE nspname = @schema)", schema));
        }
        finally
        {
            await DropSchemaAsync(connection, schema);
        }
    }

    /// <summary>
    /// G-54: a schema-qualified call to a project function had no dependency edge, so a table, index, or view could be created
    /// before the function it calls (the names sort so key order alone puts the caller first). It must deploy, converge, and
    /// be removable again.
    /// </summary>
    [TestMethod]
    public async Task QualifiedProjectFunctionCalls_DeployInDependencyOrderAndDropCleanly()
    {
        var schema = NewSchemaName();
        await using var connection = await OpenConnectionAsync();
        try
        {
            var sourceModel = BuildModel($"""
                CREATE SCHEMA {schema};
                CREATE FUNCTION {schema}.z_bump(x integer) RETURNS integer LANGUAGE sql IMMUTABLE AS $$ SELECT x + 1 $$;
                CREATE TABLE {schema}.a_t (
                    n integer NOT NULL DEFAULT {schema}.z_bump(1),
                    m integer NOT NULL DEFAULT 0,
                    CONSTRAINT a_t_m_ok CHECK ({schema}.z_bump(m) > 0)
                );
                CREATE INDEX a_idx ON {schema}.a_t ({schema}.z_bump(m));
                CREATE VIEW {schema}.a_v AS SELECT {schema}.z_bump(m) AS bumped FROM {schema}.a_t;
                """);
            Assert.AreEqual(0, sourceModel.Diagnostics.Count(static diagnostic => diagnostic.Message.Contains("missing function", StringComparison.OrdinalIgnoreCase)),
                string.Join(Environment.NewLine, sourceModel.Diagnostics.Select(static diagnostic => diagnostic.Message)));

            await PublishAsync(connection, DatabaseModel.Empty, sourceModel);

            await ExecuteRawAsync(connection, $"INSERT INTO {schema}.a_t (m) VALUES (5);");
            Assert.AreEqual(2L, await CountAsync(connection, $"SELECT n::bigint FROM {schema}.a_t", schema), "The default must call the function.");
            Assert.AreEqual(6L, await CountAsync(connection, $"SELECT bumped::bigint FROM {schema}.a_v", schema));

            var liveModel = await LoadSchemaModelAsync(connection, schema);
            var reDiff = await EndToEndSupport.DiffAsPublishedAsync(connection, liveModel, sourceModel);
            Assert.AreEqual(0, reDiff.Added.Count + reDiff.Removed.Count + reDiff.Modified.Count, DescribeChanges(reDiff));
            Assert.AreEqual(
                0,
                new PgProj.Deploy.ScriptGenerator().Generate(reDiff, new PgProj.Dependencies.DependencyAnalyzer().Analyze(liveModel, sourceModel)).Operations.Count,
                "A second publish must emit no operations.");

            // Removing everything must drop the callers before the function they use.
            await PublishAsync(connection, liveModel, BuildModel($"CREATE SCHEMA {schema};"));
            Assert.AreEqual(0L, await CountAsync(connection, "SELECT count(*) FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = @schema", schema));
        }
        finally
        {
            await DropSchemaAsync(connection, schema);
        }
    }

    private static string SequenceMatrix(string schema) => $"""
        CREATE SCHEMA {schema};
        CREATE SEQUENCE {schema}.plain_seq;
        CREATE SEQUENCE {schema}.int_seq AS integer;
        CREATE SEQUENCE {schema}.small_seq AS smallint;
        CREATE SEQUENCE {schema}.down_seq INCREMENT BY -1;
        CREATE SEQUENCE {schema}.down_int_seq AS integer INCREMENT BY -5;
        CREATE SEQUENCE {schema}.min_seq MINVALUE 10;
        CREATE SEQUENCE {schema}.zero_seq MINVALUE 0 START WITH 0;
        CREATE SEQUENCE {schema}.cyc_seq MAXVALUE 100 CACHE 20 CYCLE;
        CREATE TABLE {schema}.t (
            a integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            b smallint GENERATED ALWAYS AS IDENTITY,
            c bigint GENERATED BY DEFAULT AS IDENTITY
        );
        """;

    /// <summary>
    /// G-57: sequences that leave options implicit (a plain <c>CREATE SEQUENCE</c>, an <c>integer</c> or <c>smallint</c>
    /// identity column, descending and zero-based sequences) used to re-diff forever, because the live catalog reports every
    /// option. They must deploy, read back, and converge with an empty second publish.
    /// </summary>
    [TestMethod]
    public async Task SequencesWithImplicitOptions_DeployAndReDiffToZero()
    {
        var schema = NewSchemaName();
        await using var connection = await OpenConnectionAsync();
        try
        {
            var sourceModel = BuildModel(SequenceMatrix(schema));
            Assert.AreEqual(0, sourceModel.Diagnostics.Count(static diagnostic => diagnostic.Severity == SemanticDiagnosticSeverity.Error),
                string.Join(Environment.NewLine, sourceModel.Diagnostics.Select(static diagnostic => diagnostic.Message)));

            await PublishAsync(connection, DatabaseModel.Empty, sourceModel);

            var liveModel = await LoadSchemaModelAsync(connection, schema);
            var reDiff = await EndToEndSupport.DiffAsPublishedAsync(connection, liveModel, sourceModel);
            Assert.AreEqual(0, reDiff.Added.Count + reDiff.Removed.Count + reDiff.Modified.Count, DescribeChanges(reDiff));

            var second = new PgProj.Deploy.ScriptGenerator().Generate(reDiff, new PgProj.Dependencies.DependencyAnalyzer().Analyze(liveModel, sourceModel));
            Assert.AreEqual(0, second.Operations.Count, "A second publish must emit no operations.");
        }
        finally
        {
            await DropSchemaAsync(connection, schema);
        }
    }

    /// <summary>
    /// G-57: a changed option must still be reported and deployed, including setting an option back to its default when the
    /// project stops naming it, and the result must converge on a real server.
    /// </summary>
    [TestMethod]
    public async Task ChangedSequenceOptions_AreDeployedAndConverge()
    {
        var schema = NewSchemaName();
        await using var connection = await OpenConnectionAsync();
        try
        {
            string Source(string a, string b, string c, string d) => $"""
                CREATE SCHEMA {schema};
                CREATE SEQUENCE {schema}.a_seq {a};
                CREATE SEQUENCE {schema}.b_seq {b};
                CREATE SEQUENCE {schema}.c_seq {c};
                CREATE SEQUENCE {schema}.d_seq {d};
                """;

            var first = BuildModel(Source("MAXVALUE 100", "INCREMENT BY 5 CACHE 20", "AS integer", "CYCLE"));
            await PublishAsync(connection, DatabaseModel.Empty, first);

            // a: 100 -> 200; b: options no longer named (back to defaults); c: integer -> smallint; d: CYCLE -> not named.
            var revised = BuildModel(Source("MAXVALUE 200", string.Empty, "AS smallint", string.Empty));
            var live = await LoadSchemaModelAsync(connection, schema);
            var script = await PublishAsync(connection, live, revised);

            StringAssert.Contains(script, "\"a_seq\" MAXVALUE 200", script);
            StringAssert.Contains(script, "\"b_seq\" INCREMENT BY 1", script);

            await using var check = new NpgsqlCommand(
                $"SELECT sequencename, data_type::text, increment_by::text, max_value::text, cache_size::text, cycle FROM pg_catalog.pg_sequences WHERE schemaname = '{schema}' ORDER BY sequencename",
                connection);
            var rows = new Dictionary<string, string>();
            await using (var reader = await check.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    rows[reader.GetString(0)] = $"{reader.GetString(1)}|{reader.GetString(2)}|{reader.GetString(3)}|{reader.GetString(4)}|{reader.GetBoolean(5)}";
                }
            }

            Assert.AreEqual("bigint|1|200|1|False", rows["a_seq"]);
            Assert.AreEqual("bigint|1|9223372036854775807|1|False", rows["b_seq"]);
            Assert.AreEqual("smallint|1|32767|1|False", rows["c_seq"]);
            Assert.AreEqual("bigint|1|9223372036854775807|1|False", rows["d_seq"]);

            var liveAfter = await LoadSchemaModelAsync(connection, schema);
            var reDiff = await EndToEndSupport.DiffAsPublishedAsync(connection, liveAfter, revised);
            Assert.AreEqual(0, reDiff.Added.Count + reDiff.Removed.Count + reDiff.Modified.Count, DescribeChanges(reDiff));
        }
        finally
        {
            await DropSchemaAsync(connection, schema);
        }
    }

    /// <summary>
    /// G-58: a <c>serial</c> column used to fail to deploy at all (the sequence was created <c>OWNED BY</c> a table that did
    /// not exist yet, <c>42P01</c>). It must deploy in a schema that is not on the search path, really auto-increment, be
    /// owned by its column, converge, and be removable again.
    /// </summary>
    [TestMethod]
    public async Task SerialColumns_DeployIncrementConvergeAndDropCleanly()
    {
        var schema = NewSchemaName();
        await using var connection = await OpenConnectionAsync();
        try
        {
            var sourceModel = BuildModel($"""
                CREATE SCHEMA {schema};
                CREATE TABLE {schema}.t (
                    id serial PRIMARY KEY,
                    big bigserial,
                    small smallserial,
                    name text NOT NULL DEFAULT 'x'
                );
                """);

            await PublishAsync(connection, DatabaseModel.Empty, sourceModel);

            // The default is wired to a sequence and the sequence is owned by the column (so it drops with the table).
            await ExecuteRawAsync(connection, $"INSERT INTO {schema}.t (name) VALUES ('a'); INSERT INTO {schema}.t (name) VALUES ('b');");
            Assert.AreEqual(2L, await CountAsync(connection, $"SELECT max(id)::bigint FROM {schema}.t", schema));
            Assert.AreEqual(3L, await CountAsync(
                connection,
                "SELECT count(*) FROM pg_catalog.pg_depend d JOIN pg_catalog.pg_class s ON s.oid = d.objid AND s.relkind = 'S' JOIN pg_catalog.pg_namespace n ON n.oid = s.relnamespace WHERE n.nspname = @schema AND d.deptype = 'a'",
                schema));

            var liveModel = await LoadSchemaModelAsync(connection, schema);
            var reDiff = await EndToEndSupport.DiffAsPublishedAsync(connection, liveModel, sourceModel);
            Assert.AreEqual(0, reDiff.Added.Count + reDiff.Removed.Count + reDiff.Modified.Count, DescribeChanges(reDiff));
            Assert.AreEqual(
                0,
                new PgProj.Deploy.ScriptGenerator().Generate(reDiff, new PgProj.Dependencies.DependencyAnalyzer().Analyze(liveModel, sourceModel)).Operations.Count,
                "A second publish must emit no operations.");

            // Removing the table and its sequences must not fail on the sequences PostgreSQL already dropped with it.
            var empty = BuildModel($"CREATE SCHEMA {schema};");
            await PublishAsync(connection, liveModel, empty);
            Assert.AreEqual(0L, await CountAsync(connection, "SELECT count(*) FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = @schema", schema));
        }
        finally
        {
            await DropSchemaAsync(connection, schema);
        }
    }

    /// <summary>
    /// G-56: a changed, added, or removed default must be reported and deployed (a changed default used to be
    /// invisible to the diff), then converge.
    /// </summary>
    [TestMethod]
    public async Task ChangedAddedAndRemovedDefaults_AreDeployedAndConverge()
    {
        var schema = NewSchemaName();
        await using var connection = await OpenConnectionAsync();
        try
        {
            var first = BuildModel(DefaultsTable(schema, "DEFAULT 0", "DEFAULT 'old'", "DEFAULT now()", string.Empty));
            await PublishAsync(connection, DatabaseModel.Empty, first);

            // a: 0 -> 5 (changed literal); b: 'old' -> removed; c: now() -> CURRENT_TIMESTAMP (different expression);
            // d: none -> 7 (added).
            var revised = BuildModel(DefaultsTable(schema, "DEFAULT 5", string.Empty, "DEFAULT CURRENT_TIMESTAMP", "DEFAULT 7"));

            var live = await LoadSchemaModelAsync(connection, schema);
            var script = await PublishAsync(connection, live, revised);

            StringAssert.Contains(script, "\"a\" SET DEFAULT 5", script);
            StringAssert.Contains(script, "\"b\" DROP DEFAULT", script);
            StringAssert.Contains(script, "\"c\" SET DEFAULT CURRENT_TIMESTAMP", script);
            StringAssert.Contains(script, "\"d\" SET DEFAULT 7", script);

            var liveDefaults = await ReadLiveDefaultsAsync(connection, schema);
            Assert.AreEqual("5", liveDefaults["a"]);
            Assert.IsFalse(liveDefaults.ContainsKey("b"));
            Assert.AreEqual("CURRENT_TIMESTAMP", liveDefaults["c"]);
            Assert.AreEqual("7", liveDefaults["d"]);

            var liveAfter = await LoadSchemaModelAsync(connection, schema);
            var reDiff = await EndToEndSupport.DiffAsPublishedAsync(connection, liveAfter, revised);
            Assert.AreEqual(0, reDiff.Added.Count + reDiff.Removed.Count + reDiff.Modified.Count, DescribeChanges(reDiff));
        }
        finally
        {
            await DropSchemaAsync(connection, schema);
        }
    }

    [TestMethod]
    public async Task FailingScript_RollsBackAtomically()
    {
        var schema = NewSchemaName();
        await using var connection = await OpenConnectionAsync();
        try
        {
            await using (var createSchemaCommand = new NpgsqlCommand($"CREATE SCHEMA {schema};", connection))
            {
                await createSchemaCommand.ExecuteNonQueryAsync();
            }

            var script = $"""
                CREATE TABLE {schema}.widgets (id bigint PRIMARY KEY);
                THIS IS NOT VALID SQL;
                """;

            await Assert.ThrowsExactlyAsync<PostgresException>(() => ExecuteScriptAsync(connection, script));

            await using var checkCommand = new NpgsqlCommand(
                "SELECT count(*) FROM information_schema.tables WHERE table_schema = @schema AND table_name = 'widgets';",
                connection);
            checkCommand.Parameters.AddWithValue("schema", schema);
            var tableCount = (long)(await checkCommand.ExecuteScalarAsync())!;

            Assert.AreEqual(0L, tableCount, "The valid statement's effect must not persist after the transaction rolled back.");
        }
        finally
        {
            await DropSchemaAsync(connection, schema);
        }
    }

    private async Task<DatabaseModel> LoadSchemaModelAsync(NpgsqlConnection connection, string schema)
    {
        var fullModel = await _liveLoader.LoadAsync(connection, CancellationToken.None);
        return FilterToSchema(fullModel, schema);
    }

    private static DatabaseModel FilterToSchema(DatabaseModel model, string schema)
    {
        return model with
        {
            Tables = Filter(model.Tables, schema),
            Views = Filter(model.Views, schema),
            Functions = Filter(model.Functions, schema),
            Triggers = Filter(model.Triggers, schema),
            Procedures = Filter(model.Procedures, schema),
            Indexes = Filter(model.Indexes, schema),
            Constraints = Filter(model.Constraints, schema),
            Sequences = Filter(model.Sequences, schema),
            // An extension's key is 'extension:name' (G-213), so the schema on it is what places it.
            Types = Filter(model.Types, schema)
                .Concat(model.Types.Where(kvp => kvp.Value.TypeKind == "extension" && string.Equals(kvp.Value.SchemaName, schema, StringComparison.Ordinal)))
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            Grants = FilterGrants(model.Grants, schema),
        };
    }

    private static Dictionary<string, GrantDefinition> FilterGrants(IReadOnlyDictionary<string, GrantDefinition> source, string schema)
    {
        // A grant's object key is 'schema:s', 'all tables in schema:s', or 'default tables in schema:s', never 's.something', so the
        // schema on the grant is what identifies it. (Filtering on a key prefix dropped every grant.)
        return source
            .Where(kvp => string.Equals(kvp.Value.SchemaName, schema, StringComparison.Ordinal))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    private static Dictionary<string, T> Filter<T>(IReadOnlyDictionary<string, T> source, string schema)
        where T : class
    {
        var schemaPrefix = schema + ".";
        return source
            .Where(kvp => kvp.Key.StartsWith(schemaPrefix, StringComparison.Ordinal))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    private static async Task DropSchemaAsync(NpgsqlConnection connection, string schema)
    {
        await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE;", connection);
        await command.ExecuteNonQueryAsync();
    }
}
