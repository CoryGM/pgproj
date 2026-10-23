using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-278: an index key's operator class written with its schema (<c>embedding public.vector_cosine_ops</c>, as <c>pg_dump</c> writes pgvector's) never
/// matched the database, which prints a class in <c>pg_catalog</c> or <c>public</c> without its schema, so every publish rebuilt the index. Such a class is
/// now compared without that schema; one in another schema keeps it.
/// </summary>
[TestClass]
public sealed class OperatorClassSchemaTests
{
    private static ChangeSet Diff(string live, string project)
        => new DiffEngine().Diff(new SemanticPipeline().Execute([new PgQueryParser().Parse(live)]), new SemanticPipeline().Execute([new PgQueryParser().Parse(project)]));

    private const string Table = "CREATE TABLE public.t (id int PRIMARY KEY, s text); ";

    [TestMethod]
    [DataRow("text_pattern_ops", "public.text_pattern_ops", DisplayName = "public")]
    [DataRow("text_pattern_ops", "pg_catalog.text_pattern_ops", DisplayName = "pg_catalog")]
    [DataRow("text_pattern_ops", "\"public\".text_pattern_ops", DisplayName = "a quoted public")]
    [DataRow("ext.my_ops", "ext.my_ops", DisplayName = "the same other schema")]
    public void TheSameClassIsNoChange(string live, string project)
    {
        var changes = Diff(Table + $"CREATE INDEX i ON public.t (s {live});", Table + $"CREATE INDEX i ON public.t (s {project});");

        Assert.IsEmpty(changes.Modified, string.Join("\n", changes.Modified.SelectMany(static change => change.Details).Select(static detail => $"{detail.PropertyName}: {detail.OldValue} -> {detail.NewValue}")));
    }

    [TestMethod]
    [DataRow("ext.my_ops", "other.my_ops", DisplayName = "two other schemas")]
    [DataRow("ext.my_ops", "my_ops", DisplayName = "another schema against none")]
    [DataRow("text_pattern_ops", "public.varchar_pattern_ops", DisplayName = "another class")]
    [DataRow("text_pattern_ops", "public.\"Text_Pattern_Ops\"", DisplayName = "the case of a quoted name")]
    public void AnotherClassIsAChange(string live, string project)
    {
        var changes = Diff(Table + $"CREATE INDEX i ON public.t (s {live});", Table + $"CREATE INDEX i ON public.t (s {project});");

        Assert.IsTrue(changes.Modified.Any(static change => change.NodeKind == ChangeNodeKind.Index));
    }

    [TestMethod]
    public void APartitionKeysClassIsComparedTheSameWay()
    {
        Assert.IsTrue(DiffEngine.SamePartitionKey(
            new SemanticPipeline().Execute([new PgQueryParser().Parse("CREATE TABLE public.p (s text) PARTITION BY LIST (s text_pattern_ops);")]).Tables["public.p"].PartitionKeyElements,
            new SemanticPipeline().Execute([new PgQueryParser().Parse("CREATE TABLE public.p (s text) PARTITION BY LIST (s public.text_pattern_ops);")]).Tables["public.p"].PartitionKeyElements));
        Assert.IsFalse(DiffEngine.SamePartitionKey(
            new SemanticPipeline().Execute([new PgQueryParser().Parse("CREATE TABLE public.p (s text) PARTITION BY LIST (s ext.my_ops);")]).Tables["public.p"].PartitionKeyElements,
            new SemanticPipeline().Execute([new PgQueryParser().Parse("CREATE TABLE public.p (s text) PARTITION BY LIST (s other.my_ops);")]).Tables["public.p"].PartitionKeyElements));
    }

    [TestMethod]
    [DataRow(null, "")]
    [DataRow("public.vector_cosine_ops", "vector_cosine_ops")]
    [DataRow("pg_catalog.\"Mixed\"", "\"Mixed\"")]
    [DataRow("ext.vector_cosine_ops", "ext.vector_cosine_ops")]
    [DataRow("publicx.ops", "publicx.ops")]
    public void TheComparisonFormLeavesOutOnlyPgCatalogAndPublic(string? name, string expected)
        => Assert.AreEqual(expected, OperatorClassNames.Comparison(name));
}
