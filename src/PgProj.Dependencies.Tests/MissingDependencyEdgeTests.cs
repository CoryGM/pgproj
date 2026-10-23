using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Dependencies.Tests;

/// <summary>
/// G-231: an object is created after what PostgreSQL resolves when it is created: the types a view's body names, the primary key a view groups by, a sequence a view
/// reads or a domain's default names, a function a domain's default calls, the type a PL/pgSQL variable is declared with, and the parent's key a foreign key to a partition
/// needs. Each is named so that, by name alone, it would come first.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class MissingDependencyEdgeTests
{
    [TestMethod]
    public void AViewIsCreatedAfterATypeItCastsTo()
        => AssertBefore("CREATE VIEW public.a_v AS SELECT max(l.mode::z_modes) FROM public.z_locks l; CREATE TYPE public.z_modes AS ENUM ('a'); CREATE TABLE public.z_locks (mode text);", "public.z_modes", "public.a_v");

    [TestMethod]
    public void AViewIsCreatedAfterADomainItsJsonTableColumnNames()
        => AssertBefore("CREATE VIEW public.a_v AS SELECT * FROM JSON_TABLE('1'::jsonb, '$' COLUMNS (c public.z_dom PATH '$')); CREATE DOMAIN public.z_dom AS jsonb;", "public.z_dom", "public.a_v");

    [TestMethod]
    public void AViewThatGroupsByIsCreatedAfterThePrimaryKeyOfATableItReads()
        => AssertBefore("CREATE VIEW public.a_v AS SELECT id, data FROM public.z_base GROUP BY id; CREATE TABLE public.z_base (id int, data text, CONSTRAINT z_base_pk PRIMARY KEY (id));", "z_base_pk", "public.a_v");

    [TestMethod]
    public void AViewWithNoGroupByHasNoEdgeToThePrimaryKey()
    {
        var model = Build("CREATE VIEW public.a_v AS SELECT id FROM public.z_base; CREATE TABLE public.z_base (id int, CONSTRAINT z_base_pk PRIMARY KEY (id));");

        var dependencies = new DependencyAnalyzer().Analyze(model).GetDependencies("public.a_v").Select(static node => node.NodeId).ToList();

        Assert.IsFalse(dependencies.Any(static id => id.EndsWith("z_base_pk", StringComparison.Ordinal)), string.Join(", ", dependencies));
    }

    [TestMethod]
    public void AViewIsCreatedAfterASequenceItReadsAsATable()
        => AssertBefore("CREATE VIEW public.a_v AS SELECT last_value FROM public.z_seq; CREATE SEQUENCE public.z_seq;", "public.z_seq", "public.a_v");

    [TestMethod]
    public void ReadingASequenceAsATableIsNotAMissingTable()
    {
        var model = Build("CREATE VIEW public.a_v AS SELECT last_value FROM public.z_seq; CREATE SEQUENCE public.z_seq;");

        Assert.IsFalse(model.Diagnostics.Any(static d => d.Message.Contains("missing table", StringComparison.Ordinal)), string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
    }

    [TestMethod]
    public void ADomainIsCreatedAfterTheSequenceItsDefaultNames()
        => AssertBefore("CREATE DOMAIN public.a_dom AS int DEFAULT nextval('z_seq'); CREATE SEQUENCE public.z_seq;", "public.z_seq", "public.a_dom");

    [TestMethod]
    public void ADomainIsCreatedAfterTheFunctionItsDefaultCalls()
        => AssertBefore("CREATE DOMAIN public.a_dom AS int DEFAULT z_foo(4); CREATE FUNCTION public.z_foo(int) RETURNS int LANGUAGE sql IMMUTABLE AS 'SELECT $1 + 1';", "public.z_foo(int4)", "public.a_dom");

    [TestMethod]
    public void APlpgsqlFunctionIsCreatedAfterATypeItDeclaresAVariableOf()
        => AssertBefore("CREATE FUNCTION public.a_f() RETURNS void LANGUAGE plpgsql AS $$ DECLARE v public.z_t; BEGIN NULL; END $$; CREATE TYPE public.z_t AS ENUM ('x');", "public.z_t", "public.a_f()");

    [TestMethod]
    [DataRow("r z_rel%ROWTYPE;")]
    [DataRow("c z_rel.id%TYPE;")]
    [DataRow("n CONSTANT z_t := 'x';")]
    public void APlpgsqlFunctionIsCreatedAfterWhatItsDeclarationsName(string declaration)
        => AssertBefore(
            $"CREATE FUNCTION public.a_f() RETURNS void LANGUAGE plpgsql AS $$ DECLARE {declaration} BEGIN NULL; END $$; CREATE TABLE public.z_rel (id int); CREATE TYPE public.z_t AS ENUM ('x');",
            declaration.Contains("z_t", StringComparison.Ordinal) ? "public.z_t" : "public.z_rel",
            "public.a_f()");

    [TestMethod]
    public void ABuiltInTypeOrACursorDeclaredInPlpgsqlAddsNothing()
    {
        var model = Build("CREATE FUNCTION public.a_f() RETURNS void LANGUAGE plpgsql AS $$ DECLARE a int; b text := 'x'; curs CURSOR FOR SELECT 1; BEGIN NULL; END $$;");

        Assert.IsEmpty(new DependencyAnalyzer().Analyze(model).GetDependencies("public.a_f()"));
    }

    [TestMethod]
    public void AForeignKeyToAPartitionIsAddedAfterTheKeyItInheritsFromItsParent()
        => AssertBefore(
            "CREATE TABLE public.a_ref (x int, CONSTRAINT a_ref_fk FOREIGN KEY (x) REFERENCES public.z_pk1 (a)); CREATE TABLE public.z_pk (a int, CONSTRAINT z_pk_pkey PRIMARY KEY (a)) PARTITION BY RANGE (a); CREATE TABLE public.z_pk1 PARTITION OF public.z_pk FOR VALUES FROM (1) TO (10);",
            "z_pk_pkey",
            "a_ref_fk");

    [TestMethod]
    public void ANotValidCheckOnAParentIsCreatedAfterTheSameNamedCheckOfItsChild()
        => AssertBefore(
            "CREATE TABLE public.a_parent (f1 int); CREATE TABLE public.b_child () INHERITS (public.a_parent); ALTER TABLE public.a_parent ADD CONSTRAINT chk CHECK (f1 > 0) NOT VALID; ALTER TABLE public.b_child ADD CONSTRAINT chk CHECK (f1 > 0);",
            "public.b_child.chk",
            "public.a_parent.chk");

    [TestMethod]
    public void AValidCheckOnAParentIsCreatedAfterTheSameNamedCheckOfItsChildToo()
    {
        // Widened by G-250 from the NOT VALID case of G-246: a parent's check is cloned onto its children under its own name whenever it is valid or not, so a child's own check of that
        // name has to exist first.
        var model = Build("CREATE TABLE public.a_parent (f1 int); CREATE TABLE public.b_child () INHERITS (public.a_parent); ALTER TABLE public.a_parent ADD CONSTRAINT chk CHECK (f1 > 0); ALTER TABLE public.b_child ADD CONSTRAINT chk CHECK (f1 > 0);");

        var dependencies = new DependencyAnalyzer().Analyze(model).GetDependencies("public.a_parent.chk").Select(static node => node.NodeId).ToList();

        Assert.IsTrue(dependencies.Contains("public.b_child.chk"), string.Join(", ", dependencies));
    }

    [TestMethod]
    public void APartitionedTableIsCreatedAfterAFunctionItsKeyCalls()
        => AssertBefore(
            "CREATE TABLE public.a_part (a int, b int) PARTITION BY RANGE (a, z_plus(b)); CREATE FUNCTION public.z_plus(int) RETURNS int LANGUAGE sql IMMUTABLE AS 'SELECT $1 + 1';",
            "public.z_plus(int4)",
            "public.a_part");

    [TestMethod]
    public void AParentsIndexIsCreatedAfterTheIndexesOfItsPartitions()
        => AssertBefore(
            "CREATE TABLE public.a_pt (a int, b int) PARTITION BY RANGE (a); CREATE TABLE public.b_pt_1 PARTITION OF public.a_pt FOR VALUES FROM (0) TO (10); CREATE INDEX a_idx ON public.a_pt (b); CREATE INDEX z_own ON public.b_pt_1 USING hash (b);",
            "public.z_own",
            "public.a_idx");

    [TestMethod]
    public void AParentsCheckIsCreatedAfterTheSameNamedCheckOfItsPartition()
        => AssertBefore(
            "CREATE TABLE public.a_pt (a int) PARTITION BY RANGE (a); CREATE TABLE public.b_pt_1 PARTITION OF public.a_pt FOR VALUES FROM (0) TO (10); ALTER TABLE public.a_pt ADD CONSTRAINT chk CHECK (a > 0); ALTER TABLE public.b_pt_1 ADD CONSTRAINT chk CHECK (a > 0);",
            "public.b_pt_1.chk",
            "public.a_pt.chk");

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);

    private static void AssertBefore(string sql, string first, string then)
    {
        var order = new DependencyAnalyzer().Analyze(Build(sql)).TopologicalSort().Select(static node => node.NodeId).ToList();

        var firstIndex = order.FindIndex(id => id.EndsWith(first, StringComparison.Ordinal));
        var thenIndex = order.FindIndex(id => id.EndsWith(then, StringComparison.Ordinal));
        Assert.IsGreaterThanOrEqualTo(0, firstIndex, string.Join(", ", order));
        Assert.IsGreaterThanOrEqualTo(0, thenIndex, string.Join(", ", order));
        Assert.IsLessThan(thenIndex, firstIndex, string.Join(", ", order));
    }
}
