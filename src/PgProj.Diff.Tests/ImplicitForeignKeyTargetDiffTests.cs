using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-43: an implicit foreign-key target must compare equal to the deployed constraint (where PostgreSQL
/// reports the explicit column list), and a real change must still be reported.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ImplicitForeignKeyTargetDiffTests
{
    private const string LiveShaped = """
        create table public.p (id bigint not null, code text);
        alter table public.p add constraint p_pkey primary key (id);
        create table public.a (parent_id bigint);
        alter table public.a add constraint a_parent_id_fkey foreign key (parent_id) references public.p (id);
        """;

    private const string ImplicitProject = """
        create table public.p (id bigint primary key, code text);
        create table public.a (parent_id bigint references public.p);
        """;

    [TestMethod]
    public void ImplicitTargetDoesNotDiffAgainstTheDeployedExplicitForm()
    {
        var result = new DiffEngine().Execute(BuildModel(LiveShaped), BuildModel(ImplicitProject));

        AssertNoConstraintChanges(result);
    }

    [TestMethod]
    public void SelfDiffIsEmpty()
    {
        var model = BuildModel(ImplicitProject);

        AssertNoConstraintChanges(new DiffEngine().Execute(model, model));
    }

    [TestMethod]
    public void ExplicitAndImplicitProjectFormsAreEquivalent()
    {
        var explicitProject = BuildModel("""
            create table public.p (id bigint primary key, code text);
            create table public.a (parent_id bigint references public.p (id));
            """);

        AssertNoConstraintChanges(new DiffEngine().Execute(explicitProject, BuildModel(ImplicitProject)));
    }

    [TestMethod]
    public void ChangingThePrimaryKeyColumnMakesTheImplicitForeignKeyModified()
    {
        var project = BuildModel("""
            create table public.p (id bigint, code text primary key);
            create table public.a (parent_id text references public.p);
            """);

        var result = new DiffEngine().Execute(BuildModel(LiveShaped), project);

        Assert.IsTrue(result.ModifiedConstraints.Any(static change => change.NodeId == "public.a.a_parent_id_fkey"));
    }

    private static void AssertNoConstraintChanges(DiffResult result)
    {
        Assert.IsEmpty(result.AddedConstraints, string.Join(", ", result.AddedConstraints.Select(static constraint => constraint.Key)));
        Assert.IsEmpty(result.RemovedConstraints, string.Join(", ", result.RemovedConstraints.Select(static constraint => constraint.Key)));
        Assert.IsEmpty(result.ModifiedConstraints, string.Join(", ", result.ModifiedConstraints.Select(static change => change.NodeId)));
    }

    private static DatabaseModel BuildModel(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
