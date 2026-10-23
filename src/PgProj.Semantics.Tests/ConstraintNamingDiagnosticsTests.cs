using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-42 (cross-file): ambiguity in generated constraint names surfaces as a model warning that does not
/// fail the build.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ConstraintNamingDiagnosticsTests
{
    [TestMethod]
    public void OrderDependentGeneratedNameProducesAWarningNotAnError()
    {
        var parser = new PgQueryParser();
        var model = new SemanticPipeline().Execute(
        [
            parser.Parse("create table public.t (a integer check (a > 0));"),
            parser.Parse("alter table public.t add check (a > 0);"),
        ]);

        Assert.IsFalse(model.HasErrors, string.Join(Environment.NewLine, model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        var warning = model.Diagnostics.Single(static diagnostic => diagnostic.Source == ConstraintNameAssigner.DiagnosticSource);
        Assert.AreEqual(SemanticDiagnosticSeverity.Warning, warning.Severity);
        StringAssert.Contains(warning.Message, "t_a_check1");
        CollectionAssert.AreEquivalent(new[] { "public.t.t_a_check", "public.t.t_a_check1" }, model.Constraints.Keys.ToArray());
    }

    [TestMethod]
    public void UnambiguousNamesProduceNoNamingDiagnostics()
    {
        var model = new SemanticPipeline().Execute([new PgQueryParser().Parse("create table public.t (a integer check (a > 0), check (a < 100));")]);

        Assert.IsFalse(model.Diagnostics.Any(static diagnostic => diagnostic.Source == ConstraintNameAssigner.DiagnosticSource));
        CollectionAssert.AreEquivalent(new[] { "public.t.t_a_check", "public.t.t_a_check1" }, model.Constraints.Keys.ToArray());
    }

    [TestMethod]
    public void ConstraintKeysAreConsistentAcrossFiles()
    {
        var parser = new PgQueryParser();
        var model = new SemanticPipeline().Execute(
        [
            parser.Parse("create table public.t (a integer, b integer);"),
            parser.Parse("alter table public.t add check (a > 0); alter table public.t add check (b > 0);"),
        ]);

        CollectionAssert.AreEquivalent(new[] { "public.t.t_a_check", "public.t.t_b_check" }, model.Constraints.Keys.ToArray());
        CollectionAssert.AreEquivalent(new[] { "public.t.t_a_check", "public.t.t_b_check" }, model.Tables["public.t"].ConstraintKeys.ToArray());
    }
}
