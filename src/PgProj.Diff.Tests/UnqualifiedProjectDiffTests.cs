using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-114: a project written without schema qualifiers describes the objects in <c>public</c>, and a live database reads them back
/// qualified. Against that live model the project must be no change, and a real change must still be one.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class UnqualifiedProjectDiffTests
{
    private const string Project = """
        CREATE TABLE parent (id int PRIMARY KEY);
        CREATE TABLE child (id int, parent_id int REFERENCES parent (id), note text);
        CREATE INDEX child_parent_idx ON child (parent_id);
        CREATE SEQUENCE counter;
        CREATE TYPE mood AS ENUM ('ok', 'sad');
        CREATE VIEW parent_ids AS SELECT id FROM parent;
        CREATE FUNCTION tf() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$;
        CREATE TRIGGER child_trg BEFORE INSERT ON child FOR EACH ROW EXECUTE FUNCTION tf();
        COMMENT ON TABLE child IS 'children';
        GRANT SELECT ON parent TO PUBLIC;
        """;

    // As the live loader reads it back: every object name qualified, a trigger's function included.
    private const string Live = """
        CREATE TABLE public.parent (id int PRIMARY KEY);
        CREATE TABLE public.child (id int, parent_id int REFERENCES public.parent (id), note text);
        CREATE INDEX child_parent_idx ON public.child USING btree (parent_id);
        CREATE SEQUENCE public.counter;
        CREATE TYPE public.mood AS ENUM ('ok', 'sad');
        CREATE VIEW public.parent_ids AS SELECT id FROM parent; -- pg_get_viewdef leaves a name on search_path unqualified
        CREATE FUNCTION public.tf() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$;
        CREATE TRIGGER child_trg BEFORE INSERT ON public.child FOR EACH ROW EXECUTE FUNCTION public.tf();
        COMMENT ON TABLE public.child IS 'children';
        GRANT SELECT ON public.parent TO PUBLIC;
        """;

    [TestMethod]
    public void AnUnqualifiedProjectMatchesItsQualifiedLiveDatabase()
    {
        var changes = new DiffEngine().Diff(Build(Live), Build(Project));

        Assert.IsEmpty(changes.Added, string.Join(", ", changes.Added.Select(static change => change.NodeId)));
        Assert.IsEmpty(changes.Removed, string.Join(", ", changes.Removed.Select(static change => change.NodeId)));
        Assert.IsEmpty(changes.Modified, string.Join("; ", changes.Modified.Select(static change => change.NodeId + ": " + string.Join(", ", change.Details.Select(static detail => $"{detail.PropertyName} '{detail.OldValue}' -> '{detail.NewValue}'")))));
    }

    [TestMethod]
    public void AnUnqualifiedTableKeysUnderPublic()
        => CollectionAssert.AreEquivalent(new[] { "public.parent", "public.child" }, Build(Project).Tables.Keys.ToArray());

    [TestMethod]
    public void ATriggerNowCallingAnotherFunctionIsStillAChange()
    {
        var project = Project.Replace("EXECUTE FUNCTION tf()", "EXECUTE FUNCTION other.tf()", StringComparison.Ordinal);

        var result = new DiffEngine().Execute(Build(Live), Build(project));

        Assert.HasCount(1, result.ModifiedTriggers);
    }

    [TestMethod]
    public void ARealChangeIsStillReported()
    {
        var project = Project.Replace("note text", "note varchar(10)", StringComparison.Ordinal);

        var result = new DiffEngine().Execute(Build(Live), Build(project));

        Assert.HasCount(1, result.ModifiedColumns);
        Assert.AreEqual("public.child.note", result.ModifiedColumns[0].NodeId);
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
