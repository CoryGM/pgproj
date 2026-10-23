using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-83: a reference pgproj cannot resolve anywhere in the project is a warning instead of an error when it is
/// schema-qualified to a schema the project's <c>schemas</c> list doesn't include, or when <c>schemas</c> is empty
/// (no explicit boundary to check against at all, so every otherwise-unresolvable reference is lenient by default).
/// </summary>
[TestClass]
public sealed class SchemaScopeSeverityTests
{
    [TestMethod]
    public void EmptySchemasIsAlwaysAWarningEvenForAnUnqualifiedReference()
    {
        var model = ModelWithSchemas();

        Assert.AreEqual(SemanticDiagnosticSeverity.Warning, SchemaScopeSeverity.ForReference(model, null));
        Assert.AreEqual(SemanticDiagnosticSeverity.Warning, SchemaScopeSeverity.ForReference(model, "anything"));
    }

    [TestMethod]
    public void PopulatedSchemasIsAnErrorForAnUnqualifiedReference()
    {
        var model = ModelWithSchemas("public");

        Assert.AreEqual(SemanticDiagnosticSeverity.Error, SchemaScopeSeverity.ForReference(model, null));
    }

    [TestMethod]
    public void PopulatedSchemasIsAnErrorForAReferenceIntoAListedSchema()
    {
        var model = ModelWithSchemas("public", "app");

        Assert.AreEqual(SemanticDiagnosticSeverity.Error, SchemaScopeSeverity.ForReference(model, "app"));
    }

    [TestMethod]
    public void PopulatedSchemasIsAWarningForAReferenceIntoAnUnlistedSchema()
    {
        var model = ModelWithSchemas("public");

        Assert.AreEqual(SemanticDiagnosticSeverity.Warning, SchemaScopeSeverity.ForReference(model, "other_team"));
    }

    [TestMethod]
    public void SchemaComparisonIsCaseInsensitiveAndTrimsWhitespace()
    {
        var model = ModelWithSchemas("Public");

        Assert.AreEqual(SemanticDiagnosticSeverity.Error, SchemaScopeSeverity.ForReference(model, " public "));
    }

    [TestMethod]
    public void SchemaOfObjectReferenceIsNullForABareName()
        => Assert.IsNull(SchemaScopeSeverity.SchemaOfObjectReference("customers"));

    [TestMethod]
    public void SchemaOfObjectReferenceIsTheFirstPartOfAQualifiedName()
        => Assert.AreEqual("other_team", SchemaScopeSeverity.SchemaOfObjectReference("other_team.customers"));

    [TestMethod]
    public void SchemaOfObjectReferenceHonorsQuotedIdentifiers()
        => Assert.AreEqual("Other Team", SchemaScopeSeverity.SchemaOfObjectReference("\"Other Team\".customers"));

    [TestMethod]
    public void SchemaOfColumnReferenceIsNullForABareColumn()
        => Assert.IsNull(SchemaScopeSeverity.SchemaOfColumnReference("amount"));

    [TestMethod]
    public void SchemaOfColumnReferenceIsNullForATableDotColumnReference()
        => Assert.IsNull(SchemaScopeSeverity.SchemaOfColumnReference("orders.amount"));

    [TestMethod]
    public void SchemaOfColumnReferenceIsTheSchemaOfASchemaQualifiedTripleReference()
        => Assert.AreEqual("other_team", SchemaScopeSeverity.SchemaOfColumnReference("other_team.orders.amount"));

    [TestMethod]
    public void LenientPrefersTheMoreLenientOfTwoSeverities()
    {
        Assert.AreEqual(SemanticDiagnosticSeverity.Warning, SchemaScopeSeverity.Lenient(SemanticDiagnosticSeverity.Warning, SemanticDiagnosticSeverity.Error));
        Assert.AreEqual(SemanticDiagnosticSeverity.Warning, SchemaScopeSeverity.Lenient(SemanticDiagnosticSeverity.Error, SemanticDiagnosticSeverity.Warning));
        Assert.AreEqual(SemanticDiagnosticSeverity.Error, SchemaScopeSeverity.Lenient(SemanticDiagnosticSeverity.Error, SemanticDiagnosticSeverity.Error));
    }

    // --- Full-pipeline regressions: the real binding sites, not just the policy in isolation. ---

    [TestMethod]
    public void AProjectWithNoSchemasDeclaredBuildsCleanDespiteAGenuinelyUnresolvableReference()
    {
        var model = Build(null, "CREATE TABLE public.orders (id integer PRIMARY KEY, customer_id integer REFERENCES public.customers(id));");

        Assert.IsFalse(model.HasErrors, Dump(model));
        Assert.IsTrue(model.Diagnostics.Any(static d => d.Severity == SemanticDiagnosticSeverity.Warning && d.Message.Contains("customers", StringComparison.Ordinal)), Dump(model));
    }

    [TestMethod]
    public void AReferenceIntoAnUndeclaredSchemaIsAWarningNotAnErrorWhenSchemasIsPopulated()
    {
        var model = Build(["public"], "CREATE TABLE public.orders (id integer PRIMARY KEY, customer_id integer REFERENCES other_team.customers(id));");

        Assert.IsFalse(model.HasErrors, Dump(model));
        Assert.IsTrue(model.Diagnostics.Any(static d => d.Message.Contains("other_team.customers", StringComparison.Ordinal)), Dump(model));
    }

    [TestMethod]
    public void AReferenceIntoADeclaredSchemaThatStillDoesNotResolveIsStillAnError()
    {
        var model = Build(["public"], "CREATE TABLE public.orders (id integer PRIMARY KEY, customer_id integer REFERENCES public.customers(id));");

        Assert.IsTrue(model.HasErrors, Dump(model));
    }

    [TestMethod]
    public void AnUnqualifiedTypoStaysAnErrorEvenWithSchemasPopulated()
    {
        var model = Build(["public"], "CREATE TABLE public.orders (id integer DEFAULT public.no_such_function());");

        Assert.IsTrue(model.HasErrors, Dump(model));
    }

    [TestMethod]
    public void AColumnTypeQualifiedToAnUndeclaredSchemaIsAWarning()
    {
        var model = Build(["public"], "CREATE TABLE public.orders (id integer PRIMARY KEY, status other_team.status_type);");

        Assert.IsFalse(model.HasErrors, Dump(model));
        Assert.IsTrue(model.Diagnostics.Any(static d => d.Message.Contains("other_team.status_type", StringComparison.Ordinal)), Dump(model));
    }

    private static DatabaseModel ModelWithSchemas(params string[] schemas)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse("CREATE TABLE public.t (id integer);")], schemas);

    private static DatabaseModel Build(IReadOnlyList<string>? schemas, params string[] files)
    {
        var parser = new PgQueryParser();
        return new SemanticPipeline().Execute(files.Select(parser.Parse).ToArray(), schemas);
    }

    private static string Dump(DatabaseModel model)
        => string.Join(Environment.NewLine, model.Diagnostics.Select(static d => $"{d.Severity}: {d.Message}"));
}
