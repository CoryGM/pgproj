using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-60: the SQL generated for the sample project's grants. Its privilege lists used to contain pieces of neighbouring
/// statements, which produced an invalid statement and reduced <c>ALL PRIVILEGES</c> on tables to <c>USAGE</c>.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SampleGrantDeployTests
{
    private const string SampleGrants = """
        GRANT USAGE ON SCHEMA public TO PUBLIC;
        GRANT USAGE, CREATE ON SCHEMA public TO local_pg_admin;
        GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO local_pg_admin;
        GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public TO local_pg_admin;
        GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA public TO local_pg_admin;
        ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON TABLES TO local_pg_admin;
        ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON SEQUENCES TO local_pg_admin;
        ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT EXECUTE ON FUNCTIONS TO local_pg_admin;
        """;

    [TestMethod]
    public void EveryProjectGrantIsGeneratedOnceWithItsOwnPrivileges()
    {
        // The schema has a table, a sequence, and a function, so every "ALL ... IN SCHEMA" grant has something to apply to (a
        // grant over nothing is not emitted, G-61). GRANT USAGE ON SCHEMA public TO PUBLIC is PostgreSQL's own automatic
        // default (G-86): stating it explicitly is a no-op, the same treatment G-61 already gives PUBLIC's default EXECUTE
        // on functions, so it is never emitted even from an empty starting model.
        var objects = "create table public.t (id integer); create sequence public.q; create function public.f() returns integer language sql as $$ select 1 $$; ";
        var statements = Statements(DatabaseModel.Empty, Build(objects + SampleGrants))
            .Where(static statement => statement.StartsWith("GRANT ", StringComparison.Ordinal) || statement.StartsWith("ALTER DEFAULT PRIVILEGES", StringComparison.Ordinal))
            .ToArray();

        CollectionAssert.AreEquivalent(
            new[]
            {
                "GRANT ALL PRIVILEGES ON SCHEMA \"public\" TO \"local_pg_admin\";",
                "GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA \"public\" TO \"local_pg_admin\";",
                "GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA \"public\" TO \"local_pg_admin\";",
                "GRANT ALL PRIVILEGES ON ALL FUNCTIONS IN SCHEMA \"public\" TO \"local_pg_admin\";",
                "ALTER DEFAULT PRIVILEGES IN SCHEMA \"public\" GRANT ALL PRIVILEGES ON TABLES TO \"local_pg_admin\";",
                "ALTER DEFAULT PRIVILEGES IN SCHEMA \"public\" GRANT ALL PRIVILEGES ON SEQUENCES TO \"local_pg_admin\";",
                "ALTER DEFAULT PRIVILEGES IN SCHEMA \"public\" GRANT ALL PRIVILEGES ON FUNCTIONS TO \"local_pg_admin\";",
            },
            statements.ToList(),
            string.Join("\n", statements));
    }

    /// <summary>No generated statement may contain another statement's text, whatever order the file lists them in.</summary>
    [TestMethod]
    public void GeneratedStatementsAreSingleAndDoNotDependOnFileOrder()
    {
        var forward = Statements(DatabaseModel.Empty, Build(SampleGrants));
        var reversed = Statements(DatabaseModel.Empty, Build(string.Join("\n", SampleGrants.Split('\n').Reverse())));

        CollectionAssert.AreEquivalent(forward.ToList(), reversed.ToList());
        Assert.IsTrue(forward.All(static statement => statement.Count(static character => character == ';') == 1), string.Join("\n", forward));
        Assert.IsFalse(forward.Any(static statement => statement.Contains("PRIVILEGES,", StringComparison.Ordinal) || statement.Contains("\n", StringComparison.Ordinal)), string.Join("\n", forward));
    }

    private static IReadOnlyList<string> Statements(DatabaseModel oldModel, DatabaseModel newModel)
    {
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));
        return batch.Operations
            .Where(static operation => operation is not SchemaCreateOperation)
            .Select(static operation => operation.GenerateSql().Trim())
            .ToArray();
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
