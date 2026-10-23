using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-266: the element list of <c>CREATE TABLE child PARTITION OF parent (...) FOR VALUES ...</c> (column options and table constraints) was not read: a column option failed
/// <c>build</c> (<c>missing a raw type</c>) and a table <c>NOT NULL</c> was ignored with a warning. It is now read onto the columns the partition takes from its parent, its
/// constraints are the partition's, and the list is deployed as written, its constraints not created a second time. Approved by the user on 2026-10-13.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class PartitionOfElementListTests
{
    private const string Parent = "CREATE TABLE public.p (partid integer NOT NULL, a integer, b integer DEFAULT 1) PARTITION BY RANGE (partid);";

    private const string Child = """
        CREATE TABLE public.c PARTITION OF public.p (
            b WITH OPTIONS DEFAULT 5,
            a CONSTRAINT c_an NOT NULL,
            CONSTRAINT c_ck CHECK (b > 0)
        ) FOR VALUES FROM (1) TO (10);
        """;

    [TestMethod]
    public void TheListIsReadOntoTheInheritedColumnsAndTheBuildIsClean()
    {
        var model = Build(Parent + Child);

        Assert.IsFalse(model.Diagnostics.Any(static diagnostic => diagnostic.Severity is SemanticDiagnosticSeverity.Error or SemanticDiagnosticSeverity.Warning), Messages(model));
        var child = model.Tables["public.c"];
        CollectionAssert.AreEqual(new[] { "partid", "a", "b" }, child.Columns.Select(static column => column.Name).ToArray(), "The columns are the parent's, in its order.");
        var a = child.Columns.Single(static column => column.Name == "a");
        Assert.IsFalse(a.AllowsNull);
        Assert.AreEqual("c_an", a.NotNullName);
        var b = child.Columns.Single(static column => column.Name == "b");
        Assert.AreEqual("5", b.RawDefaultExpression);
        Assert.IsTrue(b.OverridesInheritedDefault);
        Assert.IsTrue(model.Constraints.Values.Any(static constraint => constraint.Name == "c_ck" && constraint.TableName == "c"));
    }

    [TestMethod]
    public void ATableLevelNotNullInTheListNamesTheInheritedColumn()
    {
        var model = Build(Parent + "CREATE TABLE public.c PARTITION OF public.p (CONSTRAINT c_bn NOT NULL b) FOR VALUES FROM (1) TO (10);");

        Assert.IsFalse(model.Diagnostics.Any(static diagnostic => diagnostic.Severity is SemanticDiagnosticSeverity.Error or SemanticDiagnosticSeverity.Warning), Messages(model));
        var b = model.Tables["public.c"].Columns.Single(static column => column.Name == "b");
        Assert.IsFalse(b.AllowsNull);
        Assert.AreEqual("c_bn", b.NotNullName);
    }

    [TestMethod]
    public void TheListIsDeployedAsWrittenAndItsConstraintsAndDefaultsAreNotRepeated()
    {
        var script = Script(Build(Parent + Child));

        StringAssert.Contains(script, """
            CREATE TABLE "public"."c" PARTITION OF "public"."p" (
                b WITH OPTIONS DEFAULT 5,
                a CONSTRAINT c_an NOT NULL,
                CONSTRAINT c_ck CHECK (b > 0)
            ) FOR VALUES FROM (1) TO (10);
            """.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd());
        Assert.IsFalse(script.Contains("ADD CONSTRAINT \"c_ck\"", StringComparison.Ordinal), "The list's CHECK is made by the CREATE TABLE.\n" + script);
        Assert.IsFalse(script.Contains("ALTER TABLE ONLY \"public\".\"c\"", StringComparison.Ordinal), "The list's default is made by the CREATE TABLE.\n" + script);
    }

    [TestMethod]
    public void APartitionWithoutAListIsWrittenAsBefore()
    {
        var script = Script(Build(Parent + "CREATE TABLE public.c PARTITION OF public.p FOR VALUES FROM (1) TO (10);"));

        StringAssert.Contains(script, "CREATE TABLE \"public\".\"c\" PARTITION OF \"public\".\"p\" FOR VALUES FROM (1) TO (10);");
    }

    [TestMethod]
    public void TheListAndTheSameStatesWrittenAsAlterTableAreNoChange()
    {
        // The form init writes for a default (G-237) and the list state the same partition.
        var listed = Build(Parent + "CREATE TABLE public.c PARTITION OF public.p (b WITH OPTIONS DEFAULT 5) FOR VALUES FROM (1) TO (10);");
        var altered = Build(Parent + "CREATE TABLE public.c PARTITION OF public.p FOR VALUES FROM (1) TO (10); ALTER TABLE ONLY public.c ALTER COLUMN b SET DEFAULT 5;");

        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(listed, listed)));
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(altered, listed)), "The written form of the same default is not a change.");
    }

    [TestMethod]
    public void AChangedDefaultOrNotNullInTheListIsAChange()
    {
        var before = Build(Parent + "CREATE TABLE public.c PARTITION OF public.p (b WITH OPTIONS DEFAULT 5) FOR VALUES FROM (1) TO (10);");
        var changedDefault = Build(Parent + "CREATE TABLE public.c PARTITION OF public.p (b WITH OPTIONS DEFAULT 6) FOR VALUES FROM (1) TO (10);");
        var addedNotNull = Build(Parent + "CREATE TABLE public.c PARTITION OF public.p (b WITH OPTIONS DEFAULT 5, a NOT NULL) FOR VALUES FROM (1) TO (10);");

        Assert.IsFalse(IsEmpty(new DiffEngine().Diff(before, changedDefault)));
        Assert.IsFalse(IsEmpty(new DiffEngine().Diff(before, addedNotNull)));
    }

    [TestMethod]
    public void AnOptionForAColumnTheParentLacksIsReported()
    {
        var model = Build(Parent + "CREATE TABLE public.c PARTITION OF public.p (z WITH OPTIONS DEFAULT 5) FOR VALUES FROM (1) TO (10);");

        Assert.IsTrue(model.Diagnostics.Any(static diagnostic => diagnostic.Message == "PARTITION OF list of public.c: column z is not a column of its parent, so its DEFAULT is ignored."), Messages(model));
    }

    [TestMethod]
    public void AnAlterTableNotNullOnAPartitionColumnIsStillReportedAndIgnored()
    {
        // Only the list is deployed with the partition; a separate ALTER TABLE ... ADD CONSTRAINT n NOT NULL would not be, so it stays a warning as before G-266.
        var model = Build(Parent + "CREATE TABLE public.c PARTITION OF public.p FOR VALUES FROM (1) TO (10); ALTER TABLE public.c ADD CONSTRAINT c_bn NOT NULL b;");

        Assert.IsTrue(model.Diagnostics.Any(static diagnostic => diagnostic.Message.Contains("NOT NULL on public.c.b: the project does not declare that column", StringComparison.Ordinal)), Messages(model));
        Assert.IsTrue(model.Tables["public.c"].Columns.Single(static column => column.Name == "b").AllowsNull);
    }

    private static bool IsEmpty(ChangeSet diff) => diff.Added.Count + diff.Removed.Count + diff.Modified.Count == 0;

    private static string Messages(DatabaseModel model) => string.Join(" | ", model.Diagnostics.Select(static diagnostic => diagnostic.Message));

    private static string Script(DatabaseModel newModel)
    {
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(DatabaseModel.Empty, newModel), new DependencyAnalyzer().Analyze(DatabaseModel.Empty, newModel));
        return string.Join("\n", batch.Operations.Where(static operation => operation is not SchemaCreateOperation).Select(static operation => operation.GenerateSql().Replace("\r\n", "\n", StringComparison.Ordinal))).Trim();
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
