using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-76: <c>CREATE POLICY</c>/<c>DROP POLICY</c> and the table's own <c>ENABLE</c>/<c>FORCE ROW LEVEL SECURITY</c> deploy SQL. A
/// modified policy is always dropped and recreated (<c>ALTER POLICY</c> cannot change the command or PERMISSIVE/RESTRICTIVE
/// kind), matching the constraint precedent; the row-security flags are genuine in-place <c>ALTER TABLE</c> toggles.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class PolicyDeployTests
{
    private const string Table = "CREATE TABLE public.items (id integer, owner_id integer, status text);\n";

    [TestMethod]
    public void ANewPolicyWithEveryClauseIsDeployedInFull()
    {
        var statements = Statements(Table, Table
            + "CREATE POLICY p ON public.items AS RESTRICTIVE FOR SELECT TO app_user, other_role "
            + "USING (owner_id = 1) WITH CHECK (status = 'active');");

        var created = statements.Single(static statement => statement.StartsWith("CREATE POLICY", StringComparison.Ordinal));
        Assert.AreEqual(
            "CREATE POLICY \"p\" ON \"public\".\"items\" AS RESTRICTIVE FOR SELECT TO \"app_user\", \"other_role\" "
            + "USING (owner_id = 1) WITH CHECK (status = 'active');",
            created);
    }

    [TestMethod]
    public void DefaultClausesAreOmittedFromTheGeneratedStatement()
    {
        var statements = Statements(Table, Table + "CREATE POLICY p ON public.items USING (true);");

        Assert.AreEqual("CREATE POLICY \"p\" ON \"public\".\"items\" USING (true);", statements.Single(static s => s.StartsWith("CREATE POLICY", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ToPublicIsOmittedButAnExplicitRoleListIsNot()
    {
        var statements = Statements(Table, Table + "CREATE POLICY p ON public.items TO PUBLIC USING (true);");

        StringAssert.DoesNotMatch(statements.Single(static s => s.StartsWith("CREATE POLICY", StringComparison.Ordinal)), new System.Text.RegularExpressions.Regex("TO"));
    }

    [TestMethod]
    public void ARemovedPolicyIsDropped()
    {
        var statements = Statements(Table + "CREATE POLICY p ON public.items USING (true);", Table);

        CollectionAssert.AreEqual(new[] { "DROP POLICY \"p\" ON \"public\".\"items\";" }, statements);
    }

    [TestMethod]
    public void AModifiedPolicyIsDroppedAndRecreatedNotAltered()
    {
        var statements = Statements(
            Table + "CREATE POLICY p ON public.items USING (owner_id = 1);",
            Table + "CREATE POLICY p ON public.items USING (owner_id = 2);");

        Assert.HasCount(2, statements, Dump(statements));
        Assert.AreEqual("DROP POLICY \"p\" ON \"public\".\"items\";", statements[0]);
        StringAssert.Contains(statements[1], "owner_id = 2");
    }

    [TestMethod]
    public void ACosmeticOnlyChangeDeploysNothing()
    {
        Assert.IsEmpty(Statements(
            Table + "CREATE POLICY p ON public.items TO app_user, other_role USING (owner_id=1 AND status='active');",
            Table + "CREATE POLICY p ON public.items TO Other_Role, App_User USING (((owner_id = 1) AND (status = 'active')));"));
    }

    [TestMethod]
    public void EnablingRowSecurityIsAnInPlaceAlterNotADropAndRecreate()
    {
        var statements = Statements(Table, Table + "ALTER TABLE public.items ENABLE ROW LEVEL SECURITY;");

        CollectionAssert.AreEqual(new[] { "ALTER TABLE \"public\".\"items\" ENABLE ROW LEVEL SECURITY;" }, statements);
    }

    [TestMethod]
    public void DisablingRowSecurityIsAnInPlaceAlter()
    {
        var statements = Statements(
            Table + "ALTER TABLE public.items ENABLE ROW LEVEL SECURITY;",
            Table);

        CollectionAssert.AreEqual(new[] { "ALTER TABLE \"public\".\"items\" DISABLE ROW LEVEL SECURITY;" }, statements);
    }

    [TestMethod]
    public void ForcingRowSecurityIsAnInPlaceAlter()
    {
        var statements = Statements(
            Table + "ALTER TABLE public.items ENABLE ROW LEVEL SECURITY;",
            Table + "ALTER TABLE public.items ENABLE ROW LEVEL SECURITY; ALTER TABLE public.items FORCE ROW LEVEL SECURITY;");

        CollectionAssert.AreEqual(new[] { "ALTER TABLE \"public\".\"items\" FORCE ROW LEVEL SECURITY;" }, statements);
    }

    [TestMethod]
    public void ANewTableAlreadyRowSecurityEnabledGetsATrailingAlterStatement()
    {
        var statements = Statements(string.Empty, Table + "ALTER TABLE public.items ENABLE ROW LEVEL SECURITY; ALTER TABLE public.items FORCE ROW LEVEL SECURITY;");

        var createTable = statements.Single(static s => s.Contains("CREATE  TABLE", StringComparison.Ordinal) || s.Contains("CREATE TABLE", StringComparison.Ordinal));
        StringAssert.Contains(createTable, "ENABLE ROW LEVEL SECURITY");
        StringAssert.Contains(createTable, "FORCE ROW LEVEL SECURITY");
    }

    private static string Dump(IEnumerable<string> statements) => string.Join(Environment.NewLine, statements);

    private static List<string> Statements(string oldSql, string newSql)
    {
        var oldModel = Build(oldSql);
        var newModel = Build(newSql);
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));
        return batch.Operations
            .Where(static operation => operation is not SchemaCreateOperation)
            .Select(static operation => operation.GenerateSql().Trim())
            .ToList();
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute(string.IsNullOrWhiteSpace(sql) ? [] : [new PgQueryParser().Parse(sql)]);
}
