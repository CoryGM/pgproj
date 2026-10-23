using System.CommandLine;
using Npgsql;
using PgProj.Cli;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-46: extensions through the real <c>pgproj publish --execute</c> command. A project declares the extensions it
/// uses (as <c>samples/MultiSchema</c> does), publishing must create them, a second publish must change nothing (the functions and
/// types an extension installs are not the project's objects), an undeclared extension is dropped with
/// <c>DROP EXTENSION</c>, and the <c>plpgsql</c> extension every new database ships with is never touched.
/// </summary>
[TestClass]
public sealed class CliPublishExtensionsEndToEndTests
{
    private const string Header = """
        CREATE EXTENSION plpgsql SCHEMA pg_catalog;
        COMMENT ON EXTENSION plpgsql IS 'PL/pgSQL procedural language';

        CREATE EXTENSION citext SCHEMA public;
        COMMENT ON EXTENSION citext IS 'data type for case-insensitive character strings';

        CREATE EXTENSION "uuid-ossp" SCHEMA public;
        """;

    private const string PeopleTable = """

        CREATE TABLE public.people (
            id uuid PRIMARY KEY,
            email public.citext NOT NULL UNIQUE
        );
        """;

    [TestMethod]
    public async Task ExtensionsCreateConvergeAndDrop()
    {
        await using var database = await TestDatabase.CreateAsync();

        // 1. Publish from a database that only has the default plpgsql: creates citext, uuid-ossp, and a table using both.
        database.WriteSql(Header + PeopleTable);
        var first = await database.PublishAsync();
        Assert.AreEqual(0, first.ExitCode, "First publish failed.\n" + first.Output);
        Assert.AreEqual("citext, plpgsql, uuid-ossp", await database.ExtensionsAsync(), "Extensions after the first publish.\n" + first.Output);
        Assert.AreEqual(1L, await database.ScalarAsync("SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'people';"));

        // 2. Publishing again changes nothing: the functions and types the extensions installed are not the project's.
        var second = await database.PublishAsync();
        Assert.AreEqual(0, second.ExitCode, "Second publish failed.\n" + second.Output);
        AssertNoDdl(second.Output, "second publish");

        // 3. Stop using citext: the column becomes text, the extension is still declared. (Doing this and removing the
        //    extension in the same publish drops the extension before the column is altered; see G-52.) Retyping a
        //    column can discard data (G-35), so this publish must opt in with --allow-destructive.
        const string WithoutCitextUse = """
            CREATE EXTENSION plpgsql SCHEMA pg_catalog;
            COMMENT ON EXTENSION plpgsql IS 'PL/pgSQL procedural language';

            CREATE EXTENSION citext SCHEMA public;
            CREATE EXTENSION "uuid-ossp" SCHEMA public;

            CREATE TABLE public.people (
                id uuid PRIMARY KEY,
                email text NOT NULL UNIQUE
            );
            """;
        database.WriteSql(WithoutCitextUse);
        var retyped = await database.PublishAsync(allowDestructive: true);
        Assert.AreEqual(0, retyped.ExitCode, "Publish that retypes the column failed.\n" + retyped.Output);
        Assert.AreEqual("citext, plpgsql, uuid-ossp", await database.ExtensionsAsync());

        // 4. Remove the citext extension: it is dropped with DROP EXTENSION, the others stay. Dropping an extension
        //    can discard data (G-35: every object it owns is dropped with it), so this also needs --allow-destructive.
        database.WriteSql(WithoutCitextUse.Replace("CREATE EXTENSION citext SCHEMA public;\n", string.Empty, StringComparison.Ordinal).Replace("CREATE EXTENSION citext SCHEMA public;\r\n", string.Empty, StringComparison.Ordinal));
        var third = await database.PublishAsync(allowDestructive: true);
        Assert.AreEqual(0, third.ExitCode, "Publish that drops an extension failed.\n" + third.Output);
        Assert.AreEqual(0L, await database.ScalarAsync("SELECT count(*) FROM pg_catalog.pg_extension WHERE extname = 'citext';"));
        Assert.AreEqual(1L, await database.ScalarAsync("SELECT count(*) FROM pg_catalog.pg_extension WHERE extname = 'uuid-ossp';"));

        var fourth = await database.PublishAsync();
        Assert.AreEqual(0, fourth.ExitCode, "Final publish failed.\n" + fourth.Output);
        AssertNoDdl(fourth.Output, "final publish");
    }

    [TestMethod]
    public async Task ProjectThatDoesNotDeclarePlpgsqlNeverDropsIt()
    {
        await using var database = await TestDatabase.CreateAsync();

        database.WriteSql("CREATE TABLE public.t (id bigint PRIMARY KEY);");
        var result = await database.PublishAsync();

        Assert.AreEqual(0, result.ExitCode, "Publish failed.\n" + result.Output);
        Assert.AreEqual(1L, await database.ScalarAsync("SELECT count(*) FROM pg_catalog.pg_extension WHERE extname = 'plpgsql';"));
        Assert.AreEqual(1L, await database.ScalarAsync("SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 't';"));
    }

    private static void AssertNoDdl(string output, string step)
    {
        foreach (var keyword in new[] { "CREATE ", "ALTER ", "DROP " })
        {
            Assert.IsFalse(output.Contains(keyword, StringComparison.Ordinal), $"Expected the {step} to change nothing but the script contained '{keyword.Trim()}':\n{output}");
        }
    }

    /// <summary>A disposable database inside the shared container, with a project directory and a publish helper.</summary>
    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly string _databaseName;
        private readonly string _projectDirectory;

        private TestDatabase(string databaseName, string projectDirectory, string connectionString)
        {
            _databaseName = databaseName;
            _projectDirectory = projectDirectory;
            ConnectionString = connectionString;
        }

        public string ConnectionString { get; }

        private string ProjectFile => Path.Combine(_projectDirectory, "pgproj.json");

        public static async Task<TestDatabase> CreateAsync()
        {
            var databaseName = $"g46_{Guid.NewGuid():N}";
            var projectDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g46-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(projectDirectory, "sql"));
            await File.WriteAllTextAsync(Path.Combine(projectDirectory, "pgproj.json"), """{ "name": "g46", "version": "1.0", "source": "./sql" }""");

            await using (var admin = new NpgsqlConnection(PostgresContainerFixture.ConnectionString))
            {
                await admin.OpenAsync();
                await using var create = new NpgsqlCommand($"CREATE DATABASE {databaseName};", admin);
                await create.ExecuteNonQueryAsync();
            }

            var connectionString = new NpgsqlConnectionStringBuilder(PostgresContainerFixture.ConnectionString) { Database = databaseName }.ConnectionString;
            return new TestDatabase(databaseName, projectDirectory, connectionString);
        }

        public void WriteSql(string sql) => File.WriteAllText(Path.Combine(_projectDirectory, "sql", "schema.sql"), sql);

        public async Task<(int ExitCode, string Output)> PublishAsync(bool allowDestructive = false)
        {
            var root = new RootCommand("pgproj") { PublishCommand.Create(PostgresContainerFixture.CreateCliContext()) };

            var originalOut = Console.Out;
            var originalError = Console.Error;
            var output = new StringWriter();
            Console.SetOut(output);
            Console.SetError(output);
            try
            {
                var args = new List<string> { "publish", "--project", ProjectFile, "--target-connection", ConnectionString, "--execute" };
                if (allowDestructive)
                {
                    args.Add("--allow-destructive");
                }

                var exitCode = await root.Parse(args.ToArray()).InvokeAsync();
                return (exitCode, output.ToString());
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalError);
            }
        }

        public async Task<string> ExtensionsAsync()
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT coalesce(string_agg(extname, ', ' ORDER BY extname), '') FROM pg_catalog.pg_extension;", connection);
            return (string)(await command.ExecuteScalarAsync())!;
        }

        public async Task<long> ScalarAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            return (long)(await command.ExecuteScalarAsync())!;
        }

        public async ValueTask DisposeAsync()
        {
            await using (var admin = new NpgsqlConnection(PostgresContainerFixture.ConnectionString))
            {
                await admin.OpenAsync();
                await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {_databaseName} WITH (FORCE);", admin);
                await drop.ExecuteNonQueryAsync();
            }

            try
            {
                Directory.Delete(_projectDirectory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup of a temp directory.
            }
        }
    }
}
