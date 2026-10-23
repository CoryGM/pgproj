using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-277: a column renamed with <c>@renamed-from</c> takes its constraints and indexes with it, as <c>RENAME COLUMN</c> does in PostgreSQL. They, and the
/// foreign keys that reference the column, are compared with the old name read as the new one, so a change that is only the rename is no change to
/// them, while a real change still is.
/// </summary>
[TestClass]
public sealed class RenamedColumnReferencesTests
{
    private const string Live = """
        CREATE TABLE public.a (
            id int PRIMARY KEY,
            code text NOT NULL,
            span int4range,
            CONSTRAINT a_code_key UNIQUE (code) INCLUDE (id),
            CONSTRAINT a_code_check CHECK (code <> '' AND code <> 'code'),
            CONSTRAINT a_span_excl EXCLUDE USING gist (span WITH &&));
        CREATE UNIQUE INDEX a_code_lower ON public.a (lower(code));
        CREATE INDEX a_code_idx ON public.a (code) INCLUDE (id) WHERE code IS NOT NULL;
        CREATE TABLE public.b (id int PRIMARY KEY, a_code text REFERENCES public.a (code));
        """;

    private const string Renamed = """
        CREATE TABLE public.a (
            id int PRIMARY KEY,
            -- @renamed-from: code
            sku text NOT NULL,
            -- @renamed-from: span
            period int4range,
            CONSTRAINT a_code_key UNIQUE (sku) INCLUDE (id),
            CONSTRAINT a_code_check CHECK (sku <> '' AND sku <> 'code'),
            CONSTRAINT a_span_excl EXCLUDE USING gist (period WITH &&));
        CREATE UNIQUE INDEX a_code_lower ON public.a (lower(sku));
        CREATE INDEX a_code_idx ON public.a (sku) INCLUDE (id) WHERE sku IS NOT NULL;
        CREATE TABLE public.b (id int PRIMARY KEY, a_code text REFERENCES public.a (sku));
        """;

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);

    private static ChangeSet Diff(string live, string project) => new DiffEngine().Diff(Build(live), Build(project));

    [TestMethod]
    public void ARenameAloneChangesNoConstraintOrIndex()
    {
        var changes = Diff(Live, Renamed);

        Assert.IsEmpty(changes.Added);
        Assert.IsEmpty(changes.Removed);
        Assert.IsFalse(changes.Modified.Any(static change => change.NodeKind is ChangeNodeKind.Constraint or ChangeNodeKind.Index), Describe(changes));
        Assert.IsTrue(changes.Modified.Any(static change => change.NodeId == "public.a.sku"), Describe(changes));
    }

    [TestMethod]
    public void TheModelItselfIsNotChanged()
    {
        var live = Build(Live);
        var before = live.Constraints["public.a.a_code_key"].UniqueColumns.ToArray();

        _ = new DiffEngine().Diff(live, Build(Renamed));

        CollectionAssert.AreEqual(before, live.Constraints["public.a.a_code_key"].UniqueColumns.ToArray());
    }

    [TestMethod]
    [DataRow("CHECK (sku <> '' AND sku <> 'code')", "CHECK (sku <> '' AND sku <> 'sku')", DisplayName = "a string literal is not a column")]
    [DataRow("UNIQUE (sku) INCLUDE (id)", "UNIQUE (sku, id)", DisplayName = "a key's columns")]
    [DataRow("(lower(sku))", "(upper(sku))", DisplayName = "an index expression")]
    [DataRow("WHERE sku IS NOT NULL", "WHERE sku IS NULL", DisplayName = "an index predicate")]
    [DataRow("REFERENCES public.a (sku)", "REFERENCES public.a (id)", DisplayName = "a referencing foreign key")]
    public void ARealChangeAlongsideTheRenameIsStillAChange(string renamedText, string changedText)
    {
        var changes = Diff(Live, Renamed.Replace(renamedText, changedText, StringComparison.Ordinal));

        Assert.IsTrue(
            changes.Modified.Any(static change => change.NodeKind is ChangeNodeKind.Constraint or ChangeNodeKind.Index)
                || changes.Added.Any(static change => change.NodeKind is ChangeNodeKind.Constraint or ChangeNodeKind.Index),
            Describe(changes));
    }

    [TestMethod]
    public void AQuotedNewNameIsRenamedToo()
    {
        // The old name is plain: a quoted or capitalized annotation is read differently by the diff and by publish (G-287).
        var changes = Diff(
            """CREATE TABLE public.q (id int PRIMARY KEY, code text, CONSTRAINT q_key UNIQUE (code), CONSTRAINT q_check CHECK (code <> '')); CREATE INDEX q_idx ON public.q (lower(code));""",
            """
            CREATE TABLE public.q (id int PRIMARY KEY,
                -- @renamed-from: code
                "Sku Code" text, CONSTRAINT q_key UNIQUE ("Sku Code"), CONSTRAINT q_check CHECK ("Sku Code" <> ''));
            CREATE INDEX q_idx ON public.q (lower("Sku Code"));
            """);

        Assert.IsFalse(changes.Modified.Any(static change => change.NodeKind is ChangeNodeKind.Constraint or ChangeNodeKind.Index), Describe(changes));
    }

    [TestMethod]
    public void WithoutARenameNothingIsRewritten()
    {
        var changes = Diff(Live, Live.Replace("CHECK (code <> '' AND code <> 'code')", "CHECK (code <> '')", StringComparison.Ordinal));

        Assert.IsTrue(changes.Modified.Any(static change => change.NodeId == "public.a.a_code_check"), Describe(changes));
    }

    private static string Describe(ChangeSet changes)
        => string.Join("\n", changes.Modified.Select(static change => $"~ {change.NodeKind} {change.NodeId}: {string.Join("; ", change.Details.Select(static detail => $"{detail.PropertyName} {detail.OldValue} -> {detail.NewValue}"))}")
            .Concat(changes.Added.Select(static change => $"+ {change.NodeKind} {change.NodeId}"))
            .Concat(changes.Removed.Select(static change => $"- {change.NodeKind} {change.NodeId}")));
}
