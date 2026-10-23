using System.CommandLine;
using Npgsql;
using PgProj.Cli;

namespace PgProj.Integration.Tests;

/// <summary>
/// What the end-to-end tests written from G-121 part 2 on share: run a CLI command in process and capture its output, make a database and
/// a project directory, read a value back, and check that <c>diff</c> is <c>0/0/0</c>. (Older test classes carry their own copies.)
/// </summary>
internal static class EndToEndSupport
{
    /// <summary>The major version of the test server: 16, 17, or 18.</summary>
    internal static async Task<int> ServerMajorAsync()
    {
        await using var connection = new NpgsqlConnection(PostgresContainerFixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT current_setting('server_version_num')::int / 10000", connection);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    internal static string ConnectionStringFor(string databaseName)
        => new NpgsqlConnectionStringBuilder(PostgresContainerFixture.ConnectionString) { Database = databaseName }.ConnectionString;

    /// <summary>Creates a database with a unique name (<paramref name="prefix"/> and a GUID) and returns its name.</summary>
    internal static async Task<string> CreateDatabaseAsync(string prefix)
    {
        var name = $"{prefix}_{Guid.NewGuid():N}";
        await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE DATABASE {name};");
        return name;
    }

    internal static Task DropDatabaseAsync(string name)
        => ExecuteAsync(PostgresContainerFixture.ConnectionString, $"DROP DATABASE IF EXISTS {name} WITH (FORCE);");

    /// <summary>A new project directory with <c>sql/</c> in it, and a <c>pgproj.json</c> that names the project and points at <c>sql</c>.</summary>
    internal static string CreateProjectDirectory(string prefix, string extraProjectJson = "")
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pgproj-{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(directory, "sql"));
        File.WriteAllText(Path.Combine(directory, "pgproj.json"), $$"""{ "name": "{{prefix}}", "version": "1.0", "source": "./sql"{{extraProjectJson}} }""");
        return directory;
    }

    internal static string ProjectFile(string projectDirectory) => Path.Combine(projectDirectory, "pgproj.json");

    /// <summary>
    /// Edits a test's SQL: returns <paramref name="sql"/> with line endings as <c>\n</c> and <paramref name="find"/> replaced. It fails when the
    /// text is not there, because a plain <c>Replace</c> that matches nothing (G-145: a raw-string literal carries its file's line endings) makes
    /// the edit disappear and the test passes without testing the change.
    /// </summary>
    internal static string Edit(string sql, string find, string replacement)
    {
        var text = sql.Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.IsTrue(text.Contains(find, StringComparison.Ordinal), $"The test's SQL does not contain the text to edit: {find}");
        return text.Replace(find, replacement, StringComparison.Ordinal);
    }

    internal static void WriteSchema(string projectDirectory, string sql)
        => File.WriteAllText(Path.Combine(projectDirectory, "sql", "schema.sql"), sql);

    /// <summary>
    /// Diffs a live model and a project model the way <c>diff</c> and <c>publish</c> do: the project's views that exist live are first rendered by the server (G-96), so both
    /// sides are compared in PostgreSQL's own spelling. A test that calls <see cref="PgProj.Diff.DiffEngine"/> directly skipped that step and passed on PostgreSQL 16 and later
    /// only because they print a single-table view's columns unqualified, as a project usually writes them; 14 and 15 qualify them (<c>t.id</c>), G-269.
    /// </summary>
    internal static async Task<PgProj.Diff.ChangeSet> DiffAsPublishedAsync(NpgsqlConnection connection, PgProj.Semantics.DatabaseModel live, PgProj.Semantics.DatabaseModel project)
    {
        await new PgProj.Core.ServerViewRenderer(new PgProj.Parser.PgQueryParser()).RenderAsync(connection, project, live, CancellationToken.None);
        return new PgProj.Diff.DiffEngine().Diff(live, project);
    }

    internal static async Task AssertDiffIsEmptyAsync(string projectFile, string connectionString)
    {
        var diffed = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString);
        Assert.AreEqual(0, diffed.ExitCode, diffed.Output);
        Assert.Contains("Added: 0", diffed.Output, StringComparison.Ordinal);
        Assert.Contains("Removed: 0", diffed.Output, StringComparison.Ordinal);
        Assert.Contains("Modified: 0", diffed.Output, StringComparison.Ordinal);
    }

    internal static async Task<string> ScalarTextAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    internal static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Runs <c>init</c>, <c>build</c>, <c>diff</c>, <c>publish</c>, or <c>document</c> in process; standard output and error are captured together.</summary>
    internal static async Task<(int ExitCode, string Output)> RunAsync(params string[] args)
    {
        var context = PostgresContainerFixture.CreateCliContext();
        var root = new RootCommand("pgproj")
        {
            InitCommand.Create(context),
            BuildCommand.Create(context),
            DiffCommand.Create(context),
            PublishCommand.Create(context),
            DocumentCommand.Create(context),
        };

        var originalOut = Console.Out;
        var originalError = Console.Error;
        var output = new StringWriter();
        Console.SetOut(output);
        Console.SetError(output);
        try
        {
            var exitCode = await root.Parse(args).InvokeAsync();
            return (exitCode, output.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    internal static void TryDeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of a temp directory.
        }
    }
}
