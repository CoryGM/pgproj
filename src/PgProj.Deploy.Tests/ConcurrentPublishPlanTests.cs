using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-271: <c>publish --concurrently</c> splits the script. Work on a table the database already has leaves the transaction: a new index is built with
/// <c>CREATE INDEX CONCURRENTLY</c> after the commit, a changed one is built under a temporary name and swapped in, a new foreign key or <c>CHECK</c> is added
/// <c>NOT VALID</c> and validated after, and a new unique key is built as an index and added <c>USING INDEX</c>. Everything else stays in the transaction.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ConcurrentPublishPlanTests
{
    private const string Table = "CREATE TABLE public.t (id integer NOT NULL, email text, v integer);\n";

    [TestMethod]
    public void ANewIndexOnAnExistingTableIsBuiltConcurrentlyAfterTheCommit()
    {
        var plan = Plan(Table, Table + "CREATE INDEX t_v ON public.t (v);\nCOMMENT ON INDEX public.t_v IS 'by v';");

        Assert.IsFalse(TransactionalSql(plan).Contains("CREATE INDEX", StringComparison.Ordinal), TransactionalSql(plan));
        CollectionAssert.AreEqual(
            new[] { "CREATE INDEX CONCURRENTLY \"t_v\" ON \"public\".\"t\" USING btree (\"v\");", "COMMENT ON INDEX \"public\".\"t_v\" IS 'by v';" },
            plan.AfterCommit.Select(static step => step.Sql).ToArray());
        Assert.AreEqual("DROP INDEX CONCURRENTLY IF EXISTS \"public\".\"t_v\";", plan.AfterCommit[0].CleanupSql);
    }

    [TestMethod]
    public void AnUnnamedIndexIsBuiltWithoutAName()
    {
        var plan = Plan(Table, Table + "CREATE INDEX ON public.t (v);");

        Assert.AreEqual("CREATE INDEX CONCURRENTLY ON \"public\".\"t\" USING btree (\"v\");", plan.AfterCommit[0].Sql);
        Assert.AreEqual("DROP INDEX CONCURRENTLY IF EXISTS \"public\".\"t_v_idx\";", plan.AfterCommit[0].CleanupSql);
    }

    [TestMethod]
    public void AnIndexOnANewTableStaysInTheTransaction()
    {
        var plan = Plan(string.Empty, Table + "CREATE INDEX t_v ON public.t (v);");

        Assert.AreEqual(0, plan.AfterCommit.Count);
        StringAssert.Contains(TransactionalSql(plan), "CREATE INDEX \"t_v\"");
    }

    [TestMethod]
    public void AChangedIndexIsBuiltUnderATemporaryNameAndSwappedIn()
    {
        var plan = Plan(Table + "CREATE INDEX t_v ON public.t (v);", Table + "CREATE INDEX t_v ON public.t (v DESC);");

        Assert.IsFalse(TransactionalSql(plan).Contains("t_v", StringComparison.Ordinal), TransactionalSql(plan));
        CollectionAssert.AreEqual(
            new[]
            {
                "CREATE INDEX CONCURRENTLY \"t_v_pgproj_new\" ON \"public\".\"t\" USING btree (\"v\" DESC);",
                "DROP INDEX CONCURRENTLY \"public\".\"t_v\";",
                "ALTER INDEX \"public\".\"t_v_pgproj_new\" RENAME TO \"t_v\";",
            },
            plan.AfterCommit.Select(static step => step.Sql).ToArray());
    }

    [TestMethod]
    public void AChangedIndexOnAColumnWhoseTypeChangesStaysInTheTransaction()
    {
        var changedType = Table.Replace("v integer", "v bigint", StringComparison.Ordinal);
        var plan = Plan(Table + "CREATE INDEX t_v ON public.t (v);", changedType + "CREATE INDEX t_v ON public.t (v DESC);");

        Assert.AreEqual(0, plan.AfterCommit.Count);
    }

    [TestMethod]
    public void AChangedIndexOnAnotherColumnIsSwappedInWhenTheTableChangesToo()
    {
        var changedType = Table.Replace("email text", "email varchar(200)", StringComparison.Ordinal);
        var plan = Plan(Table + "CREATE INDEX t_v ON public.t (v);", changedType + "CREATE INDEX t_v ON public.t (v DESC);");

        Assert.AreEqual(3, plan.AfterCommit.Count, string.Join("\n", plan.AfterCommit.Select(static step => step.Sql)));
    }

    [TestMethod]
    public void AnIndexOnAPartitionedTableIsCreatedOnOnlyTheParentAndBuiltOnEachPartition()
    {
        const string Partitioned = """
            CREATE TABLE public.p (id integer NOT NULL, v integer) PARTITION BY RANGE (id);
            CREATE TABLE public.p1 PARTITION OF public.p FOR VALUES FROM (0) TO (100);
            CREATE TABLE public.p2 PARTITION OF public.p FOR VALUES FROM (100) TO (200);

            """;
        var plan = Plan(Partitioned, Partitioned + "CREATE TABLE public.p3 PARTITION OF public.p FOR VALUES FROM (200) TO (300);\nCREATE INDEX p_v ON public.p (v);", existing: [("public", "p1_v_idx")]);

        var transactional = TransactionalSql(plan);
        StringAssert.Contains(transactional, "CREATE INDEX \"p_v\" ON ONLY \"public\".\"p\" USING btree (\"v\");");
        // A partition this publish creates is empty: its index is made and attached in the transaction, after the partition.
        StringAssert.Contains(transactional, "CREATE INDEX IF NOT EXISTS \"p3_v_idx\" ON \"public\".\"p3\" USING btree (\"v\");");
        StringAssert.Contains(transactional, "ALTER INDEX \"public\".\"p_v\" ATTACH PARTITION \"public\".\"p3_v_idx\";");
        Assert.IsTrue(transactional.IndexOf("CREATE TABLE \"public\".\"p3\"", StringComparison.Ordinal) < transactional.IndexOf("\"p3_v_idx\"", StringComparison.Ordinal), transactional);

        // A name a relation already has is not taken; the next is, as PostgreSQL would choose it.
        CollectionAssert.AreEqual(
            new[]
            {
                "CREATE INDEX CONCURRENTLY \"p1_v_idx1\" ON \"public\".\"p1\" USING btree (\"v\");",
                "ALTER INDEX \"public\".\"p_v\" ATTACH PARTITION \"public\".\"p1_v_idx1\";",
                "CREATE INDEX CONCURRENTLY \"p2_v_idx\" ON \"public\".\"p2\" USING btree (\"v\");",
                "ALTER INDEX \"public\".\"p_v\" ATTACH PARTITION \"public\".\"p2_v_idx\";",
            },
            plan.AfterCommit.Select(static step => step.Sql).ToArray());
    }

    [TestMethod]
    public void AnExpressionKeyOnAPartitionedTableIsNamedAsPostgreSqlNamesIt()
    {
        const string Partitioned = """
            CREATE TABLE public.p (id integer NOT NULL, email text) PARTITION BY RANGE (id);
            CREATE TABLE public.p1 PARTITION OF public.p FOR VALUES FROM (0) TO (100);

            """;
        var plan = Plan(Partitioned, Partitioned + "CREATE INDEX p_lower ON public.p (lower(email));");

        StringAssert.StartsWith(plan.AfterCommit[0].Sql, "CREATE INDEX CONCURRENTLY \"p1_lower_idx\" ON \"public\".\"p1\"");
    }

    [TestMethod]
    public void AnIndexOnASubPartitionedTableStaysInTheTransaction()
    {
        const string Partitioned = """
            CREATE TABLE public.p (id integer NOT NULL, v integer) PARTITION BY RANGE (id);
            CREATE TABLE public.p1 PARTITION OF public.p FOR VALUES FROM (0) TO (100) PARTITION BY RANGE (v);

            """;
        var plan = Plan(Partitioned, Partitioned + "CREATE INDEX p_v ON public.p (v);");

        Assert.AreEqual(0, plan.AfterCommit.Count);
        StringAssert.Contains(TransactionalSql(plan), "CREATE INDEX \"p_v\" ON \"public\".\"p\"");
    }

    [TestMethod]
    public void ANewForeignKeyAndCheckAreAddedNotValidAndValidatedAfter()
    {
        const string Ref = "CREATE TABLE public.r (id integer PRIMARY KEY);\n";
        var plan = Plan(Ref + Table, Ref + Table + "ALTER TABLE public.t ADD CONSTRAINT t_v_fk FOREIGN KEY (v) REFERENCES public.r (id);\nALTER TABLE public.t ADD CONSTRAINT t_v_check CHECK (v > 0);");

        var transactional = TransactionalSql(plan);
        StringAssert.Contains(transactional, "REFERENCES \"public\".\"r\" (\"id\") NOT VALID;");
        StringAssert.Contains(transactional, "CHECK (v > 0) NOT VALID;");
        CollectionAssert.AreEquivalent(
            new[] { "ALTER TABLE \"public\".\"t\" VALIDATE CONSTRAINT \"t_v_fk\";", "ALTER TABLE \"public\".\"t\" VALIDATE CONSTRAINT \"t_v_check\";" },
            plan.AfterCommit.Select(static step => step.Sql).ToArray());
    }

    [TestMethod]
    public void AForeignKeyOnAPartitionedTableIsValidatedAfterOnlyFromPostgreSql18()
    {
        const string Before = """
            CREATE TABLE public.r (id integer PRIMARY KEY);
            CREATE TABLE public.p (id integer NOT NULL, v integer) PARTITION BY RANGE (id);
            CREATE TABLE public.p1 PARTITION OF public.p FOR VALUES FROM (0) TO (100);

            """;
        const string After = Before + "ALTER TABLE public.p ADD CONSTRAINT p_v_fk FOREIGN KEY (v) REFERENCES public.r (id);";

        Assert.AreEqual(0, Plan(Before, After, serverMajorVersion: 17).AfterCommit.Count);
        Assert.AreEqual(1, Plan(Before, After, serverMajorVersion: 18).AfterCommit.Count);
    }

    [TestMethod]
    public void ANewUniqueConstraintIsBuiltAsAnIndexAndAddedUsingIt()
    {
        var plan = Plan(Table, Table + "ALTER TABLE public.t ADD CONSTRAINT t_email_key UNIQUE (email) DEFERRABLE INITIALLY DEFERRED;\nCOMMENT ON CONSTRAINT t_email_key ON public.t IS 'one each';");

        Assert.IsFalse(TransactionalSql(plan).Contains("t_email_key", StringComparison.Ordinal), TransactionalSql(plan));
        CollectionAssert.AreEqual(
            new[]
            {
                "CREATE UNIQUE INDEX CONCURRENTLY \"t_email_key\" ON \"public\".\"t\" (\"email\");",
                "ALTER TABLE \"public\".\"t\" ADD CONSTRAINT \"t_email_key\" UNIQUE USING INDEX \"t_email_key\" DEFERRABLE INITIALLY DEFERRED;",
                "COMMENT ON CONSTRAINT \"t_email_key\" ON \"public\".\"t\" IS 'one each';",
            },
            plan.AfterCommit.Select(static step => step.Sql).ToArray());
    }

    [TestMethod]
    public void APrimaryKeyIsBuiltConcurrentlyOnlyOnColumnsAlreadyNotNull()
    {
        Assert.AreEqual(2, Plan(Table, Table + "ALTER TABLE public.t ADD CONSTRAINT t_pkey PRIMARY KEY (id);").AfterCommit.Count);

        // USING INDEX would set NOT NULL on v, scanning the table under ACCESS EXCLUSIVE.
        var nullable = Plan(Table, Table + "ALTER TABLE public.t ADD CONSTRAINT t_pkey PRIMARY KEY (v);");
        Assert.AreEqual(0, nullable.AfterCommit.Count);
        StringAssert.Contains(TransactionalSql(nullable), "PRIMARY KEY (\"v\")");
    }

    [TestMethod]
    public void AKeyANewForeignKeyReferencesStaysInTheTransaction()
    {
        const string Other = "CREATE TABLE public.o (t_id integer);\n";
        var plan = Plan(Table + Other, Table + Other + "ALTER TABLE public.t ADD CONSTRAINT t_pkey PRIMARY KEY (id);\nALTER TABLE public.o ADD CONSTRAINT o_fk FOREIGN KEY (t_id) REFERENCES public.t (id);");

        StringAssert.Contains(TransactionalSql(plan), "PRIMARY KEY (\"id\")");
        CollectionAssert.AreEqual(new[] { "ALTER TABLE \"public\".\"o\" VALIDATE CONSTRAINT \"o_fk\";" }, plan.AfterCommit.Select(static step => step.Sql).ToArray());
    }

    [TestMethod]
    public void AnExclusionConstraintStaysInTheTransaction()
    {
        var plan = Plan(Table, Table + "ALTER TABLE public.t ADD CONSTRAINT t_v_excl EXCLUDE USING btree (v WITH =);");

        Assert.AreEqual(0, plan.AfterCommit.Count);
    }

    [TestMethod]
    public void AnInvalidIndexIsRebuiltConcurrently()
    {
        var live = Build(Table + "CREATE INDEX t_v ON public.t (v);");
        live.Indexes = live.Indexes.ToDictionary(static pair => pair.Key, static pair => pair.Value with { IsValid = false }, SqlIdentifiers.NameComparer);
        var plan = Plan(live, Build(Table + "CREATE INDEX t_v ON public.t (v);"));

        Assert.AreEqual("CREATE INDEX CONCURRENTLY \"t_v_pgproj_new\" ON \"public\".\"t\" USING btree (\"v\");", plan.AfterCommit[0].Sql);
    }

    [TestMethod]
    public void WithNothingToMoveTheBatchIsUnchanged()
    {
        var batch = Generate(Build(Table), Build(Table.Replace("v integer", "v integer DEFAULT 1", StringComparison.Ordinal)));
        var plan = ConcurrentPublishPlan.Create(batch, Build(Table), 18, new HashSet<(string, string)>());

        Assert.AreSame(batch, plan.Transactional);
        Assert.AreEqual(string.Empty, plan.AfterCommitScript());
    }

    [TestMethod]
    public void TheTemporaryNameFitsSixtyThreeBytes()
    {
        var name = ConcurrentPublishPlan.TemporaryName(new string('a', 63));

        Assert.AreEqual(63, name.Length);
        StringAssert.EndsWith(name, "_pgproj_new");
    }

    private static ConcurrentPublishPlan Plan(string liveSql, string projectSql, int serverMajorVersion = 18, IEnumerable<(string, string)>? existing = null)
        => Plan(Build(liveSql), Build(projectSql), serverMajorVersion, existing);

    private static ConcurrentPublishPlan Plan(DatabaseModel live, DatabaseModel project, int serverMajorVersion = 18, IEnumerable<(string, string)>? existing = null)
        => ConcurrentPublishPlan.Create(Generate(live, project), live, serverMajorVersion, new HashSet<(string, string)>(existing ?? []));

    private static OperationBatch Generate(DatabaseModel live, DatabaseModel project)
        => new ScriptGenerator().Generate(new DiffEngine().Diff(live, project), new DependencyAnalyzer().Analyze(live, project));

    private static string TransactionalSql(ConcurrentPublishPlan plan)
        => string.Join("\n", plan.Transactional.Operations.Select(static operation => operation.GenerateSql().Replace("\r\n", "\n", StringComparison.Ordinal)));

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
