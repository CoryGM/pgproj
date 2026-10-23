using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-134: a publication is a model object of its own, keyed by its name; the tables it names are dependencies; and its options, column lists, and row filters have a comparison
/// form, so PostgreSQL's defaults, the order of a column list, and the parentheses <c>pg_get_expr</c> adds do not make two publications different.
/// </summary>
[TestClass]
public sealed class PublicationSemanticsTests
{
    private const string Tables = "CREATE TABLE public.t (id int PRIMARY KEY, n int); CREATE TABLE public.u (id int PRIMARY KEY); ";

    [TestMethod]
    public void APublicationIsInTheModelKeyedByItsName()
    {
        var model = Build(Tables + "CREATE PUBLICATION p FOR TABLE public.t (id, n) WHERE (n > 0), public.u WITH (publish = 'insert');");

        var publication = model.Publications["publication:p"];
        Assert.AreEqual("p", publication.Name);
        Assert.IsNull(publication.SchemaName);
        Assert.AreEqual(2, publication.Tables.Count);
        CollectionAssert.AreEquivalent(new[] { "public.t", "public.u" }, publication.ReferencedTables.ToArray());
        Assert.AreEqual(0, model.Diagnostics.Count, string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
    }

    [TestMethod]
    public void ThePostgreSqlDefaultsAreLeftOutOfTheComparisonForm()
    {
        var stated = Build(Tables + "CREATE PUBLICATION p FOR TABLE public.t WITH (publish = 'update, truncate, insert, delete', publish_via_partition_root = false);").Publications.Values.Single();
        var omitted = Build(Tables + "CREATE PUBLICATION p FOR TABLE public.t;").Publications.Values.Single();
        var narrowed = Build(Tables + "CREATE PUBLICATION p FOR TABLE public.t WITH (publish = 'update,insert', publish_via_partition_root = on);").Publications.Values.Single();

        Assert.IsEmpty(stated.NormalizedOptions);
        Assert.IsEmpty(omitted.NormalizedOptions);
        Assert.AreEqual("insert,update", narrowed.NormalizedOptions["publish"]);
        Assert.AreEqual("true", narrowed.NormalizedOptions["publish_via_partition_root"]);
    }

    [TestMethod]
    public void AColumnListIsASetAndARowFilterIsInComparisonForm()
    {
        var first = Build(Tables + "CREATE PUBLICATION p FOR TABLE public.t (id, n) WHERE (n > 0);").Publications.Values.Single().Tables.Single();
        var second = Build(Tables + "CREATE PUBLICATION p FOR TABLE public.t (n, id) WHERE ((n) > 0);").Publications.Values.Single().Tables.Single();

        CollectionAssert.AreEqual(first.NormalizedColumns.ToArray(), second.NormalizedColumns.ToArray());
        Assert.IsNotNull(first.NormalizedFilter);
        Assert.AreEqual(first.NormalizedFilter, second.NormalizedFilter);
    }

    [TestMethod]
    public void TwoPublicationsOfTheSameNameAreAnError()
    {
        var model = Build(Tables + "CREATE PUBLICATION p FOR TABLE public.t; CREATE PUBLICATION p FOR ALL TABLES;");

        Assert.AreEqual(1, model.Diagnostics.Count(static d => d.Severity == SemanticDiagnosticSeverity.Error && d.Message.Contains("Duplicate Publication", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void APublicationIsAModelNodeSoTheGraphSeesIt()
    {
        var model = Build(Tables + "CREATE PUBLICATION p FOR ALL TABLES;");

        Assert.IsTrue(model.GetAllNodes().Any(static node => node is PublicationDefinition));
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
