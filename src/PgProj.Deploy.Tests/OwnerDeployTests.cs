using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-146: an owner is set with <c>ALTER ... OWNER TO</c> after its object exists and never by dropping the object, a changed owner is an access-loss event
/// that needs <c>--allow-destructive</c> (decision 3), and an object dropped and recreated in the same publish gets the owner the project states set again.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class OwnerDeployTests
{
    private const string Objects = """
        CREATE SCHEMA app;
        CREATE TABLE app.t (id int PRIMARY KEY);
        CREATE VIEW app.v AS SELECT id FROM app.t;
        CREATE MATERIALIZED VIEW app.mv AS SELECT id FROM app.t;
        CREATE SEQUENCE app.s;
        CREATE FUNCTION app.f(a integer, b text) RETURNS integer LANGUAGE sql AS $$ SELECT a $$;
        CREATE PROCEDURE app.p(a integer) LANGUAGE sql AS $$ SELECT 1 $$;
        CREATE TYPE app.mood AS ENUM ('a', 'b');
        CREATE DOMAIN app.d AS integer;
        CREATE AGGREGATE app.agg(integer) (SFUNC = int4pl, STYPE = integer);
        CREATE OPERATOR app.#+# (LEFTARG = integer, RIGHTARG = integer, FUNCTION = int4pl);
        CREATE FUNCTION app.evt() RETURNS event_trigger LANGUAGE plpgsql AS $$ BEGIN NULL; END $$;
        CREATE EVENT TRIGGER audit ON ddl_command_end EXECUTE FUNCTION app.evt();
        CREATE PUBLICATION pub FOR TABLE app.t;
        """;

    [TestMethod]
    [DataRow("ALTER SCHEMA app OWNER TO lc_owner;", "ALTER SCHEMA \"app\" OWNER TO \"lc_owner\";")]
    [DataRow("ALTER TABLE app.t OWNER TO lc_owner;", "ALTER TABLE \"app\".\"t\" OWNER TO \"lc_owner\";")]
    [DataRow("ALTER VIEW app.v OWNER TO lc_owner;", "ALTER VIEW \"app\".\"v\" OWNER TO \"lc_owner\";")]
    [DataRow("ALTER MATERIALIZED VIEW app.mv OWNER TO lc_owner;", "ALTER MATERIALIZED VIEW \"app\".\"mv\" OWNER TO \"lc_owner\";")]
    [DataRow("ALTER SEQUENCE app.s OWNER TO lc_owner;", "ALTER SEQUENCE \"app\".\"s\" OWNER TO \"lc_owner\";")]
    [DataRow("ALTER TYPE app.mood OWNER TO lc_owner;", "ALTER TYPE \"app\".\"mood\" OWNER TO \"lc_owner\";")]
    [DataRow("ALTER DOMAIN app.d OWNER TO lc_owner;", "ALTER DOMAIN \"app\".\"d\" OWNER TO \"lc_owner\";")]
    [DataRow("ALTER EVENT TRIGGER audit OWNER TO lc_owner;", "ALTER EVENT TRIGGER \"audit\" OWNER TO \"lc_owner\";")]
    [DataRow("ALTER PUBLICATION pub OWNER TO lc_owner;", "ALTER PUBLICATION \"pub\" OWNER TO \"lc_owner\";")]
    public void AnOwnerStatedForANewObjectIsSetWithAnAlterOwnerTo(string statement, string expected)
    {
        // Act
        var statements = Statements(Build("CREATE SCHEMA other;"), Build(Objects + statement));

        // Assert
        CollectionAssert.Contains(statements, expected, string.Join("\n", statements));
    }

    [TestMethod]
    public void ARoutineAggregateAndOperatorAreNamedByTheirArgumentsAsTheStatementThatDropsThemIs()
    {
        // Act
        var statements = Statements(
            Build("CREATE SCHEMA other;"),
            Build(Objects + "ALTER FUNCTION app.f(int, text) OWNER TO o; ALTER PROCEDURE app.p(int) OWNER TO o; ALTER AGGREGATE app.agg(int) OWNER TO o; ALTER OPERATOR app.#+# (int, int) OWNER TO o;"));

        // Assert
        Assert.AreEqual(1, statements.Count(static s => s.StartsWith("ALTER FUNCTION \"app\".\"f\"(", StringComparison.Ordinal) && s.EndsWith("OWNER TO \"o\";", StringComparison.Ordinal) && s.Contains("text", StringComparison.Ordinal)), string.Join("\n", statements));
        Assert.AreEqual(1, statements.Count(static s => s.StartsWith("ALTER PROCEDURE \"app\".\"p\"(", StringComparison.Ordinal) && s.EndsWith("OWNER TO \"o\";", StringComparison.Ordinal)));
        Assert.AreEqual(1, statements.Count(static s => s.StartsWith("ALTER AGGREGATE \"app\".\"agg\" (", StringComparison.Ordinal) && s.EndsWith("OWNER TO \"o\";", StringComparison.Ordinal)));
        Assert.AreEqual(1, statements.Count(static s => s.StartsWith("ALTER OPERATOR \"app\".#+# (", StringComparison.Ordinal) && s.EndsWith("OWNER TO \"o\";", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ARoleThatNeedsQuotingIsQuoted()
    {
        // Act
        var statements = Statements(Build("CREATE SCHEMA other;"), Build("CREATE SCHEMA app; CREATE TABLE app.t (id int); ALTER TABLE app.t OWNER TO \"Mixed \"\"Q\"\" Case\";"));

        // Assert
        CollectionAssert.Contains(statements, "ALTER TABLE \"app\".\"t\" OWNER TO \"Mixed \"\"Q\"\" Case\";");
    }

    [TestMethod]
    public void AnOwnerIsSetAfterItsObjectIsCreatedAndBeforeAGrantOnIt()
    {
        // Act
        var statements = Statements(
            Build("CREATE SCHEMA other;"),
            Build("CREATE SCHEMA app; CREATE TABLE app.t (id int); GRANT SELECT ON app.t TO reader; ALTER TABLE app.t OWNER TO lc_owner;"));

        // Assert
        var create = statements.FindIndex(static s => s.StartsWith("CREATE TABLE \"app\".\"t\"", StringComparison.Ordinal));
        var owner = statements.IndexOf("ALTER TABLE \"app\".\"t\" OWNER TO \"lc_owner\";");
        var grant = statements.FindIndex(static s => s.StartsWith("GRANT SELECT ON TABLE \"app\".\"t\"", StringComparison.Ordinal));
        Assert.IsTrue(create >= 0 && create < owner && owner < grant, string.Join("\n", statements));
    }

    [TestMethod]
    public void AChangedOwnerIsAnAlterInPlaceNeverADropAndCreate()
    {
        // Act
        var operations = Operations(
            Build(Objects + "ALTER TABLE app.t OWNER TO old_owner;"),
            Build(Objects + "ALTER TABLE app.t OWNER TO new_owner;"));

        // Assert
        var alter = operations.Single();
        Assert.IsInstanceOfType<AlterOperation>(alter);
        Assert.AreEqual("ALTER TABLE \"app\".\"t\" OWNER TO \"new_owner\";", alter.GenerateSql());
    }

    [TestMethod]
    public void AChangedOwnerIsDestructiveAndSaysWhoLosesWhat()
    {
        // Act
        var destructive = Batch(Build(Objects + "ALTER TABLE app.t OWNER TO old_owner;"), Build(Objects + "ALTER TABLE app.t OWNER TO new_owner;")).GetDestructiveOperations().Single();

        // Assert
        Assert.AreEqual(ChangeNodeKind.Owner, destructive.Operation.NodeKind);
        Assert.AreEqual("owner of table app.t changes from old_owner to new_owner; old_owner loses the privileges it had as the owner", destructive.Reasons.Single());
    }

    [TestMethod]
    public void AnOwnerSetOnANewObjectIsNotDestructive()
    {
        // Act
        var batch = Batch(Build("CREATE SCHEMA app;"), Build("CREATE SCHEMA app; CREATE TABLE app.t (id int); ALTER TABLE app.t OWNER TO lc_owner;"));

        // Assert: nobody had the object, so nobody loses it.
        Assert.IsEmpty(batch.GetDestructiveOperations());
        Assert.IsEmpty(batch.GetUnsupportedOperations());
    }

    [TestMethod]
    public void AnOwnerThatIsTheSameInTheProjectAndTheDatabaseIsNothingToDo()
    {
        // Act
        var operations = Operations(Build(Objects + "ALTER TABLE app.t OWNER TO lc_owner;"), Build(Objects + "ALTER TABLE app.t OWNER TO LC_OWNER;"));

        // Assert
        Assert.IsEmpty(operations);
    }

    [TestMethod]
    public void AnOwnerOnlyTheDatabaseHasIsNeverChanged()
    {
        // Act: the project states none of the owners the database has.
        var operations = Operations(Build(Objects + "ALTER TABLE app.t OWNER TO lc_owner; ALTER SCHEMA app OWNER TO lc_owner;"), Build(Objects));

        // Assert
        Assert.IsEmpty(operations);
    }

    [TestMethod]
    public void ARecreatedViewIsGivenTheOwnerTheProjectStatesOnceAndNotTheOneItHad()
    {
        // Arrange: the column the view reads changes type, so the view is dropped and created again around it (G-103).
        const string Table = "CREATE SCHEMA s; CREATE TABLE s.c (id int PRIMARY KEY, score int);";
        const string View = " CREATE VIEW s.v AS SELECT id, score FROM s.c;";
        var live = Build(Table + View + " ALTER VIEW s.v OWNER TO had_owner;");
        live.LiveDependencies = [new LiveDependency("view", "s.v", "s.c", "score")];
        live.LiveOwners = new Dictionary<string, string> { ["s.v"] = "had_owner" };
        live.ConnectedRole = "deployer";

        // Act
        var statements = Statements(live, Build(Table.Replace("score int", "score bigint", StringComparison.Ordinal) + View + " ALTER VIEW s.v OWNER TO stated_owner;"));

        // Assert: the owner is set to what the project says, once; the older "keeps the owner it had" statement does not also run.
        Assert.AreEqual(1, statements.Count(static s => s.StartsWith("ALTER VIEW \"s\".\"v\" OWNER TO", StringComparison.Ordinal)), string.Join("\n", statements));
        CollectionAssert.DoesNotContain(statements, "ALTER VIEW \"s\".\"v\" OWNER TO \"had_owner\";", string.Join("\n", statements));
        var drop = statements.IndexOf("DROP VIEW \"s\".\"v\";");
        var create = statements.FindIndex(static s => s.StartsWith("CREATE OR REPLACE VIEW \"s\".\"v\"", StringComparison.Ordinal));
        var owner = statements.FindIndex(static s => s.StartsWith("ALTER VIEW \"s\".\"v\" OWNER TO \"stated_owner\"", StringComparison.Ordinal));
        Assert.IsTrue(drop >= 0 && drop < create && create < owner, string.Join("\n", statements));
    }

    [TestMethod]
    public void ARecreatedViewWhoseOwnerTheProjectDoesNotStateKeepsTheOwnerItHad()
    {
        // Arrange: nothing stated, so the older behavior (G-103, decision 7) is unchanged.
        const string Table = "CREATE SCHEMA s; CREATE TABLE s.c (id int PRIMARY KEY, score int);";
        const string View = " CREATE VIEW s.v AS SELECT id, score FROM s.c;";
        var live = Build(Table + View);
        live.LiveDependencies = [new LiveDependency("view", "s.v", "s.c", "score")];
        live.LiveOwners = new Dictionary<string, string> { ["s.v"] = "had_owner" };
        live.ConnectedRole = "deployer";

        // Act
        var statements = Statements(live, Build(Table.Replace("score int", "score bigint", StringComparison.Ordinal) + View));

        // Assert
        CollectionAssert.Contains(statements, "ALTER VIEW \"s\".\"v\" OWNER TO \"had_owner\";");
    }

    [TestMethod]
    public void AnOwnerWhoseObjectIsNotKnownIsAPlaceholderAndRefused()
    {
        // Arrange: a routine's owner with no definition to write its arguments from.
        var owner = new OwnerDefinition("owner:function:s.f(int4)", "function s.f(int4)")
        {
            ObjectKind = "function",
            ObjectKey = "s.f(int4)",
            ObjectName = "f",
            SchemaName = "s",
            OwnerRole = "o",
        };
        var operation = new CreateOperation { NodeId = owner.Key, NodeKind = ChangeNodeKind.Owner, Node = owner };

        // Act
        var batch = new OperationBatch { Operations = [operation] };

        // Assert
        StringAssert.StartsWith(operation.GenerateSql(), "-- TODO:");
        Assert.AreEqual(1, batch.GetUnsupportedOperations().Count);
    }

    private static OperationBatch Batch(DatabaseModel oldModel, DatabaseModel newModel)
        => new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));

    private static IReadOnlyList<SqlOperation> Operations(DatabaseModel oldModel, DatabaseModel newModel) => Batch(oldModel, newModel).Operations;

    private static List<string> Statements(DatabaseModel oldModel, DatabaseModel newModel)
        => Operations(oldModel, newModel)
            .Select(static operation => operation.GenerateSql().Trim().Replace("\r\n", "\n", StringComparison.Ordinal))
            .ToList();

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
