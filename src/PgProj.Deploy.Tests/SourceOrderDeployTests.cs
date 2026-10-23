using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-112, the user's rule: "the original order of properties, columns, etc. from the source should be retained on a publish. For
/// comparison/diff purposes they can appear in any order but the publish should always maintain order." Each case pins what a publish
/// emits to the order written, and that an order-only difference is not a change.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SourceOrderDeployTests
{
    private const string Table = "CREATE TABLE public.t (z int, b int, a int)";

    [TestMethod]
    public void UniqueColumnsAreEmittedInTheOrderWritten()
    {
        var statements = Statements(DatabaseModel.Empty, Build(Table + "; ALTER TABLE public.t ADD CONSTRAINT t_uq UNIQUE (z, b);"));

        Assert.IsTrue(statements.Any(static statement => statement.Contains("UNIQUE (\"z\", \"b\")", StringComparison.Ordinal)), Dump(statements));
    }

    [TestMethod]
    public void ATablesIndexesAndConstraintsAreEmittedInTheOrderWritten()
    {
        var statements = Statements(DatabaseModel.Empty, Build(Table + """
            ;
            CREATE INDEX t_z_idx ON public.t (z);
            CREATE INDEX t_a_idx ON public.t (a);
            ALTER TABLE public.t ADD CONSTRAINT t_zz_check CHECK (z > 0);
            ALTER TABLE public.t ADD CONSTRAINT t_aa_check CHECK (a > 0);
            """));

        var joined = Dump(statements);
        Assert.IsTrue(joined.IndexOf("\"t_z_idx\"", StringComparison.Ordinal) < joined.IndexOf("\"t_a_idx\"", StringComparison.Ordinal), joined);
        Assert.IsTrue(joined.IndexOf("\"t_zz_check\"", StringComparison.Ordinal) < joined.IndexOf("\"t_aa_check\"", StringComparison.Ordinal), joined);
    }

    [TestMethod]
    public void ConstraintsDeclaredInALaterFileComeAfterThoseInAnEarlierOne()
    {
        var model = new SemanticPipeline().Execute(
        [
            new PgQueryParser().Parse(Table + "; ALTER TABLE public.t ADD CONSTRAINT t_zz_check CHECK (z > 0);"),
            new PgQueryParser().Parse("ALTER TABLE public.t ADD CONSTRAINT t_aa_check CHECK (a > 0);"),
        ]);

        var joined = Dump(Statements(DatabaseModel.Empty, model));

        Assert.IsTrue(joined.IndexOf("\"t_zz_check\"", StringComparison.Ordinal) < joined.IndexOf("\"t_aa_check\"", StringComparison.Ordinal), joined);
    }

    [TestMethod]
    public void AForeignKeyStillWaitsForTheKeyItReferencesWhateverTheOrderWritten()
    {
        // Source order only decides among objects with no dependency between them; dependency order still wins (G-41).
        var statements = Statements(DatabaseModel.Empty, Build("""
            CREATE TABLE public.parent (id int NOT NULL);
            CREATE TABLE public.child (id int, parent_id int);
            ALTER TABLE public.child ADD CONSTRAINT a_child_parent_fk FOREIGN KEY (parent_id) REFERENCES public.parent (id);
            ALTER TABLE public.parent ADD CONSTRAINT z_parent_pk PRIMARY KEY (id);
            """));

        var joined = Dump(statements);
        Assert.IsTrue(joined.IndexOf("\"z_parent_pk\"", StringComparison.Ordinal) < joined.IndexOf("\"a_child_parent_fk\"", StringComparison.Ordinal), joined);
    }

    [TestMethod]
    public void GrantPrivilegesAndColumnsAreEmittedInTheOrderWritten()
    {
        var statements = Statements(Build(Table + ";"), Build(Table + "; GRANT UPDATE, SELECT, INSERT ON public.t TO PUBLIC; GRANT UPDATE (z, a) ON public.t TO PUBLIC;"));

        CollectionAssert.Contains(statements, "GRANT UPDATE, SELECT, INSERT ON TABLE \"public\".\"t\" TO PUBLIC;", Dump(statements));
        CollectionAssert.Contains(statements, "GRANT UPDATE (\"z\", \"a\") ON TABLE \"public\".\"t\" TO PUBLIC;", Dump(statements));
    }

    [TestMethod]
    public void StorageOptionsAreSetAndResetInTheOrderWritten()
    {
        var statements = Statements(
            Build(Table + " WITH (toast_tuple_target = 256, autovacuum_enabled = true, fillfactor = 90);"),
            Build(Table + " WITH (fillfactor = 70, autovacuum_enabled = false);"));

        var joined = Dump(statements);
        StringAssert.Contains(joined, "SET (fillfactor = 70, autovacuum_enabled = false);");
        StringAssert.Contains(joined, "RESET (toast_tuple_target);");
    }

    [TestMethod]
    [DataRow("ALTER TABLE public.t ADD CONSTRAINT t_uq UNIQUE (z, b);", "ALTER TABLE public.t ADD CONSTRAINT t_uq UNIQUE (b, z);")]
    [DataRow("GRANT UPDATE, SELECT ON public.t TO PUBLIC;", "GRANT SELECT, UPDATE ON public.t TO PUBLIC;")]
    [DataRow("GRANT UPDATE (z, a) ON public.t TO PUBLIC;", "GRANT UPDATE (a, z) ON public.t TO PUBLIC;")]
    [DataRow("CREATE INDEX i1 ON public.t (z); CREATE INDEX i2 ON public.t (a);", "CREATE INDEX i2 ON public.t (a); CREATE INDEX i1 ON public.t (z);")]
    public void AnOrderOnlyDifferenceIsNoChange(string one, string other)
    {
        var diff = new DiffEngine().Diff(Build(Table + "; " + one), Build(Table + "; " + other));

        Assert.AreEqual(0, diff.Added.Count + diff.Removed.Count + diff.Modified.Count, Dump(diff.Modified.Select(static change => change.NodeId)));
    }

    private static string Dump(IEnumerable<string> lines) => string.Join("\n---\n", lines);

    private static List<string> Statements(DatabaseModel oldModel, DatabaseModel newModel)
    {
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));
        return batch.Operations
            .Select(static operation => operation.GenerateSql().Trim().Replace("\r\n", "\n", StringComparison.Ordinal))
            .ToList();
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
