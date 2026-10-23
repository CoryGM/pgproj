using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-47: <c>ALTER TABLE ... ADD COLUMN</c> was silently ignored, so an added column was missing from the model and <c>publish</c> reported
/// success without creating it. The column now joins its table (in the same file or another), an action the model cannot represent is
/// reported as a warning, and a column that already exists is an error.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class AlterTableAddColumnSemanticsTests
{
    private const string Table = "CREATE TABLE public.customers (id integer PRIMARY KEY, name text);";

    [TestMethod]
    public void AnAddedColumnJoinsItsTableWithEverythingItDeclares()
    {
        var model = Build(Table + "ALTER TABLE public.customers ADD COLUMN phone varchar(20) NOT NULL DEFAULT 'none';");

        var column = model.Tables["public.customers"].Columns.Single(static column => column.Name == "phone");
        Assert.AreEqual("public.customers.phone", column.Key);
        Assert.IsFalse(column.IsNullable ?? true);
        Assert.AreEqual("'none'", column.RawDefaultExpression);
        CollectionAssert.AreEqual(new[] { "20" }, column.TypeModifiers!.ToArray());
        Assert.HasCount(3, model.Tables["public.customers"].Columns);
        Assert.IsFalse(HasErrors(model), Dump(model));
    }

    [TestMethod]
    public void ColumnsAddedByOneStatementAndByLaterStatementsAreAllKept()
    {
        var model = Build(Table + "ALTER TABLE public.customers ADD COLUMN a int, ADD COLUMN b int; ALTER TABLE public.customers ADD c int;");

        CollectionAssert.AreEquivalent(new[] { "id", "name", "a", "b", "c" }, model.Tables["public.customers"].Columns.Select(static column => column.Name).ToArray());
    }

    [TestMethod]
    public void TheTableAndTheAlterMayBeInDifferentFilesInEitherOrder()
    {
        var alter = "ALTER TABLE public.customers ADD COLUMN phone text;";

        var tableFirst = Build(Table, alter);
        var alterFirst = Build(alter, Table);

        CollectionAssert.AreEquivalent(new[] { "id", "name", "phone" }, tableFirst.Tables["public.customers"].Columns.Select(static column => column.Name).ToArray());
        CollectionAssert.AreEquivalent(new[] { "id", "name", "phone" }, alterFirst.Tables["public.customers"].Columns.Select(static column => column.Name).ToArray());
        Assert.IsFalse(HasErrors(tableFirst) || HasErrors(alterFirst));
    }

    [TestMethod]
    public void TheAlterMayNameTheTableWithOrWithoutTheDefaultSchema()
    {
        var unqualifiedTable = Build("CREATE TABLE customers (id integer); ALTER TABLE public.customers ADD COLUMN a int;");
        var unqualifiedAlter = Build("CREATE TABLE public.customers (id integer); ALTER TABLE customers ADD COLUMN a int;");

        Assert.HasCount(2, unqualifiedTable.Tables.Values.Single().Columns);
        Assert.HasCount(2, unqualifiedAlter.Tables.Values.Single().Columns);
        Assert.IsFalse(HasErrors(unqualifiedTable) || HasErrors(unqualifiedAlter));
    }

    [TestMethod]
    public void IfNotExistsLeavesAnExistingColumnAlone()
    {
        var model = Build(Table + "ALTER TABLE public.customers ADD COLUMN IF NOT EXISTS name integer;");

        Assert.AreEqual("text", model.Tables["public.customers"].Columns.Single(static column => column.Name == "name").RawType);
        Assert.HasCount(2, model.Tables["public.customers"].Columns);
        Assert.IsEmpty(AlterTableDiagnostics(model));
    }

    [TestMethod]
    public void AddingAColumnThatAlreadyExistsIsAnError()
    {
        var model = Build(Table + "ALTER TABLE public.customers ADD COLUMN name text;");

        var error = model.Diagnostics.Single(static diagnostic => diagnostic.Severity == SemanticDiagnosticSeverity.Error);
        StringAssert.Contains(error.Message, "already exists");
        Assert.HasCount(2, model.Tables["public.customers"].Columns);
    }

    [TestMethod]
    public void AddingTheSameColumnTwiceIsAnError()
    {
        var model = Build(Table + "ALTER TABLE public.customers ADD COLUMN phone text; ALTER TABLE public.customers ADD COLUMN phone text;");

        Assert.IsTrue(HasErrors(model));
        Assert.HasCount(3, model.Tables["public.customers"].Columns);
    }

    [TestMethod]
    public void AColumnAddedToATableTheProjectDoesNotDeclareIsAWarning()
    {
        var model = Build("ALTER TABLE public.nothing ADD COLUMN phone text;");

        var warning = AlterTableDiagnostics(model).Single();
        Assert.AreEqual(SemanticDiagnosticSeverity.Warning, warning.Severity);
        StringAssert.Contains(warning.Message, "public.nothing");
        Assert.IsFalse(HasErrors(model));
        Assert.IsEmpty(model.Tables);
    }

    [TestMethod]
    [DataRow("ALTER TABLE public.customers DROP COLUMN name;", "DROP COLUMN")]
    [DataRow("ALTER TABLE public.customers ALTER COLUMN name SET NOT NULL;", "SET/DROP NOT NULL")]
    [DataRow("ALTER TABLE public.customers ALTER COLUMN name SET STATISTICS 100;", "SET STATISTICS")]
    [DataRow("ALTER TABLE public.customers ALTER COLUMN name TYPE varchar(10);", "TYPE")]
    [DataRow("ALTER TABLE public.customers OWNER TO CURRENT_USER;", "OWNER TO")]
    public void AnActionTheModelCannotRepresentIsAWarningNotSilence(string sql, string expected)
    {
        var model = Build(Table + sql);

        var warning = AlterTableDiagnostics(model).Single();
        Assert.AreEqual(SemanticDiagnosticSeverity.Warning, warning.Severity);
        StringAssert.Contains(warning.Message, expected);
        StringAssert.Contains(warning.Message, "public.customers");
        Assert.IsFalse(HasErrors(model), Dump(model));
        Assert.HasCount(2, model.Tables["public.customers"].Columns);
    }

    [TestMethod]
    [DataRow("ALTER TABLE public.customers ENABLE ROW LEVEL SECURITY;", true, false)]
    [DataRow("ALTER TABLE public.customers ENABLE ROW LEVEL SECURITY; ALTER TABLE public.customers FORCE ROW LEVEL SECURITY;", true, true)]
    [DataRow("ALTER TABLE public.customers DISABLE ROW LEVEL SECURITY;", false, false)]
    public void RowLevelSecurityActionsApplyToTheTableProducingNoWarning(string sql, bool enabled, bool forced)
    {
        var model = Build(Table + sql);

        Assert.IsEmpty(AlterTableDiagnostics(model));
        Assert.IsFalse(HasErrors(model), Dump(model));
        var table = model.Tables["public.customers"];
        Assert.AreEqual(enabled, table.IsRowSecurityEnabled);
        Assert.AreEqual(forced, table.IsRowSecurityForced);
    }

    [TestMethod]
    public void AddConstraintProducesNoWarning()
    {
        var model = Build(Table + "ALTER TABLE public.customers ADD CONSTRAINT id_positive CHECK (id > 0);");

        Assert.IsEmpty(AlterTableDiagnostics(model));
        Assert.IsTrue(model.Constraints.ContainsKey("public.customers.id_positive"), string.Join(", ", model.Constraints.Keys));
    }

    [TestMethod]
    public void AnAddedColumnsInlineConstraintsBelongToTheTable()
    {
        var model = Build(Table + "CREATE TABLE public.orders (id integer PRIMARY KEY); ALTER TABLE public.customers ADD COLUMN order_id integer REFERENCES public.orders (id);");

        var foreignKey = model.Constraints.Values.Single(static constraint => string.Equals(constraint.ConstraintType, "foreign key", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual("customers", foreignKey.TableName);
        Assert.IsFalse(HasErrors(model), Dump(model));
    }

    [TestMethod]
    public void AnAddedSerialColumnGetsItsOwnedSequence()
    {
        var model = Build(Table + "ALTER TABLE public.customers ADD COLUMN ticket serial;");

        Assert.IsTrue(model.Sequences.ContainsKey("public.customers_ticket_seq"), string.Join(", ", model.Sequences.Keys));
        Assert.IsFalse(HasErrors(model), Dump(model));
    }

    [TestMethod]
    public void AnAddedPrimaryKeyColumnImpliesNotNull()
    {
        var model = Build("CREATE TABLE public.t (a integer); ALTER TABLE public.t ADD COLUMN id bigint PRIMARY KEY;");

        Assert.IsFalse(model.Tables["public.t"].Columns.Single(static column => column.Name == "id").IsNullable ?? true);
    }

    [TestMethod]
    public void ADefaultThatCallsAProjectFunctionResolvesInAnotherFile()
    {
        var model = Build(
            "CREATE FUNCTION public.utc_now() RETURNS timestamptz LANGUAGE sql AS $$ SELECT now() $$;",
            Table,
            "ALTER TABLE public.customers ADD COLUMN created timestamptz NOT NULL DEFAULT public.utc_now();");

        Assert.IsFalse(HasErrors(model), Dump(model));
        Assert.AreEqual("public.utc_now()", model.Tables["public.customers"].Columns.Single(static column => column.Name == "created").RawDefaultExpression);
    }

    [TestMethod]
    public void ADefaultThatCallsAnUndeclaredFunctionIsStillReported()
    {
        // schemas is populated so G-83's schema-scope leniency doesn't mask this genuine, in-schema missing function.
        var model = Build(["public"], Table + "ALTER TABLE public.customers ADD COLUMN created timestamptz DEFAULT public.no_such_function();");

        Assert.IsTrue(HasErrors(model), "An added column's default must be bound like any other.");
    }

    [TestMethod]
    public void ACreateTableWithTheColumnInlineIsTheSameModel()
    {
        var added = Build(Table + "ALTER TABLE public.customers ADD COLUMN phone varchar(20) NOT NULL DEFAULT 'none';");
        var inline = Build("CREATE TABLE public.customers (id integer PRIMARY KEY, name text, phone varchar(20) NOT NULL DEFAULT 'none');");

        var addedColumn = added.Tables["public.customers"].Columns.Single(static column => column.Name == "phone");
        var inlineColumn = inline.Tables["public.customers"].Columns.Single(static column => column.Name == "phone");
        Assert.AreEqual(inlineColumn.Key, addedColumn.Key);
        Assert.AreEqual(inlineColumn.RawType, addedColumn.RawType);
        Assert.AreEqual(inlineColumn.IsNullable, addedColumn.IsNullable);
        Assert.AreEqual(inlineColumn.RawDefaultExpression, addedColumn.RawDefaultExpression);
        CollectionAssert.AreEqual(inlineColumn.NormalizedTypeModifiers.ToArray(), addedColumn.NormalizedTypeModifiers.ToArray());
        Assert.AreEqual(inlineColumn.NormalizedDefaultExpression, addedColumn.NormalizedDefaultExpression);
    }

    [TestMethod]
    public void ApplyingTheResultAgainChangesNothing()
    {
        var parser = new PgQueryParser();
        var trees = new[] { parser.Parse(Table), parser.Parse("ALTER TABLE public.customers ADD COLUMN phone text; ALTER TABLE public.customers OWNER TO postgres;") };

        var once = AlterTableColumnApplier.Apply(trees);
        var twice = AlterTableColumnApplier.Apply(once);

        Assert.AreEqual(once[0].Nodes.OfType<TableNode>().Single().Columns.Count, twice[0].Nodes.OfType<TableNode>().Single().Columns.Count);
        Assert.AreEqual(once[1].Diagnostics.Count, twice[1].Diagnostics.Count);
        Assert.IsEmpty(twice.SelectMany(static tree => tree.Nodes).OfType<AlterTableAddColumnNode>().ToArray());
    }

    [TestMethod]
    public void ProjectsWithoutAlterTableAreUntouched()
    {
        var parser = new PgQueryParser();
        var trees = new[] { parser.Parse(Table) };

        Assert.AreSame(trees, AlterTableColumnApplier.Apply(trees));
    }

    private static DatabaseModel Build(params string[] files)
        => Build(null, files);

    private static DatabaseModel Build(IReadOnlyList<string>? schemas, params string[] files)
    {
        var parser = new PgQueryParser();
        return new SemanticPipeline().Execute(files.Select(parser.Parse).ToArray(), schemas);
    }

    private static bool HasErrors(DatabaseModel model) => model.Diagnostics.Any(static diagnostic => diagnostic.Severity == SemanticDiagnosticSeverity.Error);

    private static SemanticDiagnostic[] AlterTableDiagnostics(DatabaseModel model)
        => model.Diagnostics.Where(static diagnostic => diagnostic.Source == AlterTableColumnApplier.DiagnosticSource).ToArray();

    private static string Dump(DatabaseModel model) => string.Join(Environment.NewLine, model.Diagnostics.Select(static diagnostic => $"{diagnostic.Severity}: {diagnostic.Message}"));
}
