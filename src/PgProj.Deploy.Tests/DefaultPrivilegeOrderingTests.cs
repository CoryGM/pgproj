using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-339: <c>ALTER DEFAULT PRIVILEGES</c> is written before the objects it covers, after their schema, so an object created in the same publish gets what the rule
/// gives, as the project's SQL would on an empty database (it used to come after them, and they got nothing). An object no rule covers keeps its place.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class DefaultPrivilegeOrderingTests
{
    [TestMethod]
    public void ARuleIsWrittenAfterItsSchemaAndBeforeTheObjectsItCovers()
    {
        var project = Build("""
            CREATE SCHEMA app;
            CREATE TABLE app.t (id integer);
            CREATE SEQUENCE app.q;
            CREATE FUNCTION app.f() RETURNS integer LANGUAGE sql AS $$ SELECT 1 $$;
            CREATE TYPE app.mood AS ENUM ('a');
            ALTER DEFAULT PRIVILEGES IN SCHEMA app GRANT SELECT ON TABLES TO r;
            ALTER DEFAULT PRIVILEGES IN SCHEMA app GRANT USAGE ON SEQUENCES TO r;
            ALTER DEFAULT PRIVILEGES GRANT EXECUTE ON FUNCTIONS TO r;
            ALTER DEFAULT PRIVILEGES IN SCHEMA app GRANT USAGE ON TYPES TO r;
            """);
        var statements = new ScriptGenerator().Generate(new DiffEngine().Diff(DatabaseModel.Empty, project), new DependencyAnalyzer().Analyze(DatabaseModel.Empty, project))
            .Operations.Select(static operation => operation.GenerateSql().Trim()).ToList();

        int At(string prefix)
        {
            var index = statements.FindIndex(statement => statement.StartsWith(prefix, StringComparison.Ordinal));
            Assert.IsGreaterThanOrEqualTo(0, index, $"No statement starts with '{prefix}'.\n{string.Join("\n", statements)}");
            return index;
        }

        var schema = At("CREATE SCHEMA IF NOT EXISTS \"app\"");
        Assert.IsLessThan(At("ALTER DEFAULT PRIVILEGES IN SCHEMA \"app\" GRANT SELECT ON TABLES"), schema);
        Assert.IsLessThan(At("CREATE TABLE \"app\".\"t\""), At("ALTER DEFAULT PRIVILEGES IN SCHEMA \"app\" GRANT SELECT ON TABLES"));
        Assert.IsLessThan(At("CREATE SEQUENCE \"app\".\"q\""), At("ALTER DEFAULT PRIVILEGES IN SCHEMA \"app\" GRANT USAGE ON SEQUENCES"));
        Assert.IsLessThan(At("CREATE OR REPLACE FUNCTION \"app\".\"f\""), At("ALTER DEFAULT PRIVILEGES GRANT ALL PRIVILEGES ON FUNCTIONS"));
        Assert.IsLessThan(At("CREATE TYPE \"app\".\"mood\""), At("ALTER DEFAULT PRIVILEGES IN SCHEMA \"app\" GRANT ALL PRIVILEGES ON TYPES"));
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
