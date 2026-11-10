using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-132 (the user's design of 2026-11-10): foreign data wrappers, servers, and user mappings are created in that order, after the extension that installs a wrapper, and
/// dropped in reverse; a server's version and options, a mapping's options, and a wrapper's functions and options change in place; a changed wrapper or TYPE drops and
/// creates the server and its mappings; a variable is written as <c>:'name'</c>, for <c>--execute</c> to fill in; dropping a server or a mapping is destructive.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ForeignDataSqlTests
{
    private const string Extension = "CREATE EXTENSION postgres_fdw;\n";
    private const string Server = "CREATE SERVER remote FOREIGN DATA WRAPPER postgres_fdw OPTIONS (host 'db', dbname 'app');\n";
    private const string Mapping = "CREATE USER MAPPING FOR app_user SERVER remote OPTIONS (user 'remote_user', password :'remote_pw');\n";

    [TestMethod]
    public void ANewServerAndMappingAreCreatedAfterTheExtensionWithTheVariableKept()
    {
        var statements = Statements(Build(""), Build(Extension + Server + Mapping), out var batch);

        CollectionAssert.AreEqual(
            new[]
            {
                "CREATE EXTENSION postgres_fdw;",
                "CREATE SERVER \"remote\" FOREIGN DATA WRAPPER \"postgres_fdw\" OPTIONS (host 'db', dbname 'app');",
                "CREATE USER MAPPING FOR \"app_user\" SERVER \"remote\" OPTIONS (user 'remote_user', password :'remote_pw');",
            },
            statements.ToArray(),
            string.Join("\n", statements));
        Assert.IsEmpty(batch.GetDestructiveOperations());
    }

    [TestMethod]
    public void AProjectWrapperIsCreatedBeforeItsServer()
    {
        var statements = Statements(Build(""), Build("CREATE FOREIGN DATA WRAPPER w VALIDATOR app.v OPTIONS (debug 'true');\nCREATE SERVER s FOREIGN DATA WRAPPER w;\nCOMMENT ON SERVER s IS 'one';\n"), out _);

        Assert.AreEqual("CREATE FOREIGN DATA WRAPPER \"w\" VALIDATOR app.v OPTIONS (debug 'true');", statements[0]);
        StringAssert.StartsWith(statements[1], "CREATE SERVER \"s\" FOREIGN DATA WRAPPER \"w\";");
        StringAssert.Contains(statements[1], "COMMENT ON SERVER \"s\" IS 'one';");
    }

    [TestMethod]
    public void OptionsChangeInPlaceAndAVariableOrPasswordValueIsNotCompared()
    {
        var live = Build(Extension + Server.Replace("host 'db'", "host 'old', port '5432'", StringComparison.Ordinal)
            + "CREATE USER MAPPING FOR app_user SERVER remote OPTIONS (user 'remote_user', password '*****');\n");
        var statements = Statements(live, Build(Extension + Server.Replace("dbname 'app'", "dbname 'app', sslmode 'require'", StringComparison.Ordinal) + Mapping), out var batch);

        CollectionAssert.AreEqual(new[] { "ALTER SERVER \"remote\" OPTIONS (SET host 'db', DROP port, ADD sslmode 'require');" }, statements.ToArray(), "The mapping's password is not compared.\n" + string.Join("\n", statements));
        Assert.IsEmpty(batch.GetDestructiveOperations());

        // A value given by a variable is compared only by whether the option is there.
        var hostVariable = Build(Extension + Server.Replace("host 'db'", "host :'db_host'", StringComparison.Ordinal));
        Assert.IsEmpty(Statements(Build(Extension + Server), hostVariable, out _));
    }

    [TestMethod]
    public void AChangedWrapperDropsAndCreatesTheServerAndItsMappings()
    {
        const string other = "CREATE FOREIGN DATA WRAPPER other_fdw;\n";
        var statements = Statements(Build(Extension + other + Server + Mapping), Build(Extension + other + Server.Replace("postgres_fdw", "other_fdw", StringComparison.Ordinal) + Mapping), out var batch);

        var dropMapping = statements.IndexOf("DROP USER MAPPING FOR \"app_user\" SERVER \"remote\";");
        var dropServer = statements.IndexOf("DROP SERVER \"remote\";");
        var createServer = statements.FindIndex(static statement => statement.StartsWith("CREATE SERVER \"remote\" FOREIGN DATA WRAPPER \"other_fdw\"", StringComparison.Ordinal));
        var createMapping = statements.FindIndex(static statement => statement.StartsWith("CREATE USER MAPPING FOR \"app_user\"", StringComparison.Ordinal));
        Assert.IsTrue(dropMapping >= 0 && dropMapping < dropServer && dropServer < createServer && createServer < createMapping, string.Join("\n", statements));
        Assert.IsEmpty(batch.GetDestructiveOperations(), "Dropped only to be created again.");
    }

    [TestMethod]
    public void DroppingAServerOrAMappingIsDestructive()
    {
        var statements = Statements(Build(Extension + Server + Mapping), Build(Extension), out var batch);

        CollectionAssert.AreEqual(new[] { "DROP USER MAPPING FOR \"app_user\" SERVER \"remote\";", "DROP SERVER \"remote\";" }, statements.ToArray());
        Assert.HasCount(2, batch.GetDestructiveOperations());
    }

    [TestMethod]
    public void AMappingForCurrentUserIsTheDeployingRoles()
    {
        var live = Build(Extension + Server + "CREATE USER MAPPING FOR deployer SERVER remote OPTIONS (user 'x');\n");
        live.ConnectedRole = "deployer";

        Assert.IsEmpty(Statements(live, Build(Extension + Server + "CREATE USER MAPPING FOR CURRENT_USER SERVER remote OPTIONS (user 'x');\n"), out _));
    }

    [TestMethod]
    public void ApplySecretsSendsTheNamedVariablesToAMappingThatExists()
    {
        var current = Build(Extension + Server + "CREATE USER MAPPING FOR app_user SERVER remote OPTIONS (user 'remote_user');\n").UserMappings.Values.Single();
        var desired = Build(Extension + Server + Mapping).UserMappings.Values.Single();

        Assert.AreEqual("ALTER USER MAPPING FOR \"app_user\" SERVER \"remote\" OPTIONS (ADD password :'remote_pw');", ForeignDataSql.ApplySecrets(current, desired, null));
        Assert.IsNull(ForeignDataSql.ApplySecrets(current, desired, new HashSet<string>(StringComparer.Ordinal) { "other" }));
    }

    private static List<string> Statements(DatabaseModel oldModel, DatabaseModel newModel, out OperationBatch batch)
    {
        batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));
        return batch.Operations
            .Where(static operation => operation is not SchemaCreateOperation)
            .Select(static operation => operation.GenerateSql().Replace("\r\n", "\n", StringComparison.Ordinal).Trim())
            .ToList();
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
