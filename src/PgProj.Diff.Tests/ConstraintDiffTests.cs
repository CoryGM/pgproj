using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ConstraintDiffTests
{
    [TestMethod]
    public void ForeignKeyColumnReorderIsReportedAsChange()
    {
        var oldModel = BuildModel(
            "CREATE TABLE public.customers (id int, code text, PRIMARY KEY (id, code)); " +
            "CREATE TABLE public.orders (customer_id int, customer_code text, " +
            "FOREIGN KEY (customer_id, customer_code) REFERENCES public.customers (id, code));");

        var newModel = BuildModel(
            "CREATE TABLE public.customers (id int, code text, PRIMARY KEY (id, code)); " +
            "CREATE TABLE public.orders (customer_id int, customer_code text, " +
            "FOREIGN KEY (customer_id, customer_code) REFERENCES public.customers (code, id));");

        var result = new DiffEngine().Execute(oldModel, newModel);

        Assert.IsTrue(result.ModifiedConstraints.Any(change =>
            change.Details.Any(detail => detail.PropertyName == nameof(ConstraintDefinition.ForeignKeyReferencedColumns))));
    }

    [TestMethod]
    public void UniqueColumnReorderIsNotReportedAsChange()
    {
        var oldModel = BuildModel("CREATE TABLE public.t (a int, b int, UNIQUE (a, b));");
        var newModel = BuildModel("CREATE TABLE public.t (a int, b int, UNIQUE (b, a));");

        var result = new DiffEngine().Execute(oldModel, newModel);

        Assert.HasCount(0, result.ModifiedConstraints);
    }

    [TestMethod]
    public void CheckExpressionCasingAndSpacingIsNotReportedAsChange()
    {
        var oldModel = BuildModel("CREATE TABLE public.orders (quantity int, CHECK (quantity > 0));");
        var newModel = BuildModel("CREATE TABLE public.orders (quantity int, CHECK ( QUANTITY>0 ));");

        var result = new DiffEngine().Execute(oldModel, newModel);

        Assert.HasCount(0, result.ModifiedConstraints);
    }

    [TestMethod]
    public void CheckExpressionRealChangeIsReported()
    {
        var oldModel = BuildModel("CREATE TABLE public.orders (quantity int, CHECK (quantity > 0));");
        var newModel = BuildModel("CREATE TABLE public.orders (quantity int, CHECK (quantity > 1));");

        var oldConstraint = oldModel.Constraints.Values.Single();
        var newConstraint = newModel.Constraints.Values.Single();
        Assert.AreEqual(oldConstraint.Key, newConstraint.Key);
        Assert.AreNotEqual(oldConstraint.RawCheckExpression, newConstraint.RawCheckExpression,
            $"old={oldConstraint.RawCheckExpression} new={newConstraint.RawCheckExpression}");
        Assert.AreNotEqual(
            string.Join(",", oldConstraint.NormalizedExpressions),
            string.Join(",", newConstraint.NormalizedExpressions),
            $"oldNorm={string.Join(",", oldConstraint.NormalizedExpressions)} newNorm={string.Join(",", newConstraint.NormalizedExpressions)}");

        var result = new DiffEngine().Execute(oldModel, newModel);

        Assert.IsTrue(result.ModifiedConstraints.Any(change =>
            change.Details.Any(detail => detail.PropertyName == nameof(ConstraintDefinition.CheckExpressionNode))));
    }

    private static DatabaseModel BuildModel(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
