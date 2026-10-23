using System.Text.Json;
using System.Text.RegularExpressions;
using PgProj.Cli;
using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-157 against a real server, on every version: <c>init --schemas</c> and <c>--exclude-schemas</c> (and the undocumented <c>--excludeSchemas</c>) choose the schemas, the
/// generated <c>pgproj.json</c> lists the schemas that were modeled only when one of them was used, a mistake is refused before anything is written, <c>--report</c> writes the
/// Markdown report at the root of the output, the folders are Title Case, and a project reads the same whatever the case of its folders.
/// </summary>
[TestClass]
public sealed class InitScopeAndReportEndToEndTests
{
    private const string Sql = """
        CREATE SCHEMA app;
        CREATE SCHEMA audit;
        CREATE SCHEMA staging;
        CREATE TABLE audit.log_entry (id bigint PRIMARY KEY, note text);
        CREATE TABLE app.customers (id bigint PRIMARY KEY, email text NOT NULL UNIQUE);
        CREATE TABLE app.orders (id bigint PRIMARY KEY, customer_id bigint REFERENCES app.customers (id), log_id bigint REFERENCES audit.log_entry (id));
        CREATE VIEW app.v_customers AS SELECT id, email FROM app.customers;
        CREATE FUNCTION app.customer_count() RETURNS bigint LANGUAGE sql AS $$ SELECT count(*) FROM app.customers $$;
        CREATE TABLE staging.scratch (a int);
        CREATE TABLE public.settings (k text PRIMARY KEY, v text);
        CREATE TEXT SEARCH TEMPLATE app.mytmpl (INIT = dsimple_init, LEXIZE = dsimple_lexize);
        """;

    [TestMethod]
    public async Task SchemasScaffoldsOnlyThoseNamedAndPublicIsNotAssumed()
    {
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            var project = Path.Combine(directory, "pgproj.json");
            var run = await RunAsync("init", "--source-connection", connectionString, "--project", project, "--schemas", "app,audit");

            Assert.AreEqual(0, run.ExitCode, run.Output);
            Assert.IsTrue(Directory.Exists(Path.Combine(directory, "sql", "Schema", "app")));
            Assert.IsTrue(Directory.Exists(Path.Combine(directory, "sql", "Schema", "audit")));
            Assert.IsFalse(Directory.Exists(Path.Combine(directory, "sql", "Schema", "public")), "public was not named.");
            Assert.IsFalse(Directory.Exists(Path.Combine(directory, "sql", "Schema", "staging")));
            CollectionAssert.AreEqual(new[] { "app", "audit" }, ReadSchemasField(project));
            await AssertDiffIsEmptyAsync(project, connectionString);
        });
    }

    [TestMethod]
    public async Task ExcludeSchemasScaffoldsTheRestAndTheCamelCaseSpellingIsAccepted()
    {
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            var project = Path.Combine(directory, "pgproj.json");
            var args = CommandLineAliases.Normalize(["init", "--source-connection", connectionString, "--project", project, "--excludeSchemas", "audit,staging"]);
            var run = await RunAsync(args);

            Assert.AreEqual(0, run.ExitCode, run.Output);
            CollectionAssert.AreEqual(new[] { "app", "public" }, ReadSchemasField(project));
            Assert.IsFalse(Directory.Exists(Path.Combine(directory, "sql", "Schema", "audit")));
            Assert.IsTrue(File.Exists(Path.Combine(directory, "sql", "Schema", "public", "Table", "settings.sql")));

            // The new project builds although orders refers to audit (a warning, since audit is outside its schemas), and it converges with the database.
            var built = await RunAsync("build", "--project", project);
            Assert.AreEqual(0, built.ExitCode, built.Output);
            await AssertDiffIsEmptyAsync(project, connectionString);
        });
    }

    [TestMethod]
    public async Task WithNeitherOptionEverySchemaIsScaffoldedAndTheProjectStatesNone()
    {
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            var project = Path.Combine(directory, "pgproj.json");
            var run = await RunAsync("init", "--source-connection", connectionString, "--project", project);

            Assert.AreEqual(0, run.ExitCode, run.Output);
            foreach (var schema in new[] { "app", "audit", "staging", "public" })
            {
                Assert.IsTrue(Directory.Exists(Path.Combine(directory, "sql", "Schema", schema)), schema);
            }

            using var document = JsonDocument.Parse(File.ReadAllText(project));
            Assert.IsFalse(document.RootElement.TryGetProperty("schemas", out _), "No schema option, no schemas field.");
        });
    }

    [TestMethod]
    public async Task BothOptionsAndAnUnknownSchemaAreRefusedBeforeAnythingIsWritten()
    {
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            var project = Path.Combine(directory, "pgproj.json");

            var both = await RunAsync("init", "--source-connection", connectionString, "--project", project, "--schemas", "app", "--exclude-schemas", "audit");
            Assert.AreNotEqual(0, both.ExitCode);
            StringAssert.Contains(both.Output, "--schemas and --exclude-schemas cannot be used together.");

            var unknown = await RunAsync("init", "--source-connection", connectionString, "--project", project, "--schemas", "app,nope");
            Assert.AreEqual(1, unknown.ExitCode, unknown.Output);
            StringAssert.Contains(unknown.Output, "--schemas names a schema the database does not have: nope.");

            var everything = await RunAsync("init", "--source-connection", connectionString, "--project", project, "--exclude-schemas", "app,audit,staging,public");
            Assert.AreEqual(1, everything.ExitCode, everything.Output);
            StringAssert.Contains(everything.Output, "leaves no schema to scaffold");

            Assert.IsFalse(File.Exists(project));
            Assert.IsFalse(Directory.Exists(Path.Combine(directory, "sql")));
        });
    }

    [TestMethod]
    public async Task ReportWritesTheMarkdownReportAtTheRootOfTheOutput()
    {
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            var project = Path.Combine(directory, "pgproj.json");
            var before = DateTime.UtcNow.AddSeconds(-1);
            var run = await RunAsync("init", "--source-connection", connectionString, "--project", project, "--exclude-schemas", "audit,staging", "--exclude-owners", "--report");
            var after = DateTime.UtcNow.AddSeconds(1);

            Assert.AreEqual(0, run.ExitCode, run.Output);
            var reports = Directory.GetFiles(Path.Combine(directory, "sql"), "init-report-*.md", SearchOption.TopDirectoryOnly);
            Assert.AreEqual(1, reports.Length, string.Join("; ", reports));
            StringAssert.Contains(run.Output, "Wrote report:");
            var match = Regex.Match(Path.GetFileName(reports[0]), @"^init-report-(\d{4}-\d{2}-\d{2}-\d{2}-\d{2}-\d{2})\.md$");
            Assert.IsTrue(match.Success, reports[0]);
            var stamp = DateTime.ParseExact(match.Groups[1].Value, "yyyy-MM-dd-HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);
            Assert.IsTrue(stamp >= before && stamp <= after, $"{stamp:O} is not the UTC time of this run.");

            var report = File.ReadAllText(reports[0]);
            StringAssert.Contains(report, "## Contents");
            StringAssert.Contains(report, "| --exclude-schemas | `audit, staging` |");
            StringAssert.Contains(report, "| --exclude-owners | `true` |");
            StringAssert.Contains(report, "Password=*****");
            Assert.IsFalse(report.Contains("Password=" + "postgres", StringComparison.OrdinalIgnoreCase), "The password is masked.");
            StringAssert.Contains(report, "| Schemas modeled | `app`, `public` |");
            StringAssert.Contains(report, "| Table | `customers` | `sql/Schema/app/Table/customers.sql` |");
            StringAssert.Contains(report, "| View | `v_customers` | `sql/Schema/app/View/v_customers.sql` |");
            StringAssert.Contains(report, "| `app` | Text search template | `mytmpl` | not supported |");
            StringAssert.Contains(report, "| `audit` | excluded (--exclude-schemas) |");
            StringAssert.Contains(report, "| `staging` | excluded (--exclude-schemas) |");
            StringAssert.Contains(report, "| `sql/Schema/app/Table/orders.sql` | `audit` | 1 |");
            StringAssert.Contains(report, "| --exclude-owners |");
            Assert.IsFalse(report.Contains("log_entry", StringComparison.Ordinal) && report.Contains("| Table | `log_entry`", StringComparison.Ordinal), "Objects of an excluded schema are not listed.");
            StringAssert.Contains(report, "## Not modeled: cluster-wide");
            StringAssert.Contains(report, "| Role | `");

            // The report is not SQL, so the project still builds with it in the source directory.
            var built = await RunAsync("build", "--project", project);
            Assert.AreEqual(0, built.ExitCode, built.Output);
        });
    }

    [TestMethod]
    public async Task ErdIsBuiltFromTheProjectFilesAndInitLinksItInTheReport()
    {
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            var project = Path.Combine(directory, "pgproj.json");
            var run = await RunAsync("init", "--source-connection", connectionString, "--project", project, "--exclude-schemas", "audit,staging", "--report", "--erd");

            Assert.AreEqual(0, run.ExitCode, run.Output);
            var diagrams = Directory.GetFiles(directory, "erd-*.mmd");
            Assert.AreEqual(1, diagrams.Length, string.Join("; ", Directory.GetFiles(directory)));
            Assert.AreEqual(0, Directory.GetFiles(directory, "erd-*.md").Length, "init writes only the bare diagram.");
            var diagram = File.ReadAllText(diagrams[0]);
            StringAssert.StartsWith(diagram, "erDiagram");
            StringAssert.Contains(diagram, "\"app.customers\" {");
            StringAssert.Contains(diagram, "\"app.orders\" {");
            StringAssert.Contains(diagram, "\"public.settings\" {");
            StringAssert.Contains(diagram, "\"audit.log_entry (external)\"");
            Assert.IsFalse(diagram.Contains("v_customers", StringComparison.Ordinal), "Views are not drawn.");

            var report = File.ReadAllText(Directory.GetFiles(Path.Combine(directory, "sql"), "init-report-*.md").Single());
            var link = "../" + Path.GetFileName(diagrams[0]);
            StringAssert.Contains(report, $"| ERD | [{Path.GetFileName(diagrams[0])}]({link}) |");
            StringAssert.Contains(report, $"- [Entity relationship diagram]({link})");
            StringAssert.Contains(report, "| --erd | `true` |");
            StringAssert.Contains(report, "| ERD action | Written |");
            StringAssert.Contains(report, "| ERD action reason | No file was there. |");
        });
    }

    [TestMethod]
    public async Task ErdSchemasNarrowTheDiagramAndAnUnknownOneIsRefusedBeforeAnythingIsWritten()
    {
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            var project = Path.Combine(directory, "pgproj.json");

            var refused = await RunAsync("init", "--source-connection", connectionString, "--project", project, "--exclude-schemas", "audit,staging", "--erd", "--erd-schemas", "nope");
            Assert.AreEqual(1, refused.ExitCode, refused.Output);
            StringAssert.Contains(refused.Output, "names a schema the project has no table in: nope.");

            var run = await RunAsync("init", "--source-connection", connectionString, "--project", project, "--exclude-schemas", "audit,staging", "--report", "--erd", "narrow", "--erd-schemas", "app");
            Assert.AreEqual(0, run.ExitCode, run.Output);
            var diagram = File.ReadAllText(Path.Combine(directory, "narrow.mmd"));
            StringAssert.Contains(diagram, "    customers {");
            StringAssert.Contains(diagram, "    orders {");
            StringAssert.Contains(diagram, "    \"audit.log_entry (external)\"");
            Assert.IsFalse(diagram.Contains("settings", StringComparison.Ordinal), "public.settings is outside --erd-schemas.");
            var report = File.ReadAllText(Directory.GetFiles(Path.Combine(directory, "sql"), "init-report-*.md").Single());
            StringAssert.Contains(report, "| --erd-schemas | `app` |");
        });
    }

    [TestMethod]
    public async Task TheDiagramComesFromTheSqlFilesAndNotFromTheDatabase()
    {
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            var project = Path.Combine(directory, "pgproj.json");
            Assert.AreEqual(0, (await RunAsync("init", "--source-connection", connectionString, "--project", project, "--schemas", "app")).ExitCode);
            await File.WriteAllTextAsync(Path.Combine(directory, "sql", "Schema", "app", "Table", "only_in_files.sql"), "CREATE TABLE app.only_in_files (id int PRIMARY KEY);");

            foreach (var command in new[] { "build", "diff", "publish" })
            {
                var args = command == "build"
                    ? new[] { command, "--project", project, "--erd", command }
                    : [command, "--project", project, "--target-connection", connectionString, "--erd", command];
                var run = await RunAsync(args);
                Assert.IsTrue(run.ExitCode == 0, $"{command}: {run.Output}");
                var diagram = File.ReadAllText(Path.Combine(directory, command + ".mmd"));
                StringAssert.Contains(diagram, "only_in_files {");
                StringAssert.Contains(diagram, "customers {");
                Assert.IsTrue(File.Exists(Path.Combine(directory, command + ".md")), $"{command} also writes the companion page.");
            }

            var inDatabase = await ScalarTextAsync(connectionString, "SELECT count(*) FROM pg_class WHERE relname = 'only_in_files';");
            Assert.AreEqual("0", inDatabase, "The table exists only in the project files.");
        });
    }

    [TestMethod]
    public async Task WithoutReportNoReportIsWritten()
    {
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            var run = await RunAsync("init", "--source-connection", connectionString, "--project", Path.Combine(directory, "pgproj.json"));

            Assert.AreEqual(0, run.ExitCode, run.Output);
            Assert.AreEqual(0, Directory.GetFiles(Path.Combine(directory, "sql"), "init-report-*.md").Length);
        });
    }

    [TestMethod]
    public async Task AProjectReadsTheSameWhateverTheCaseOfItsFolders()
    {
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            var project = Path.Combine(directory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--source-connection", connectionString, "--project", project, "--exclude-schemas", "audit,staging");
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);

            // The same project with every folder name upper-cased, and again lower-cased.
            var variants = new List<string> { project };
            foreach (var (label, convert) in new (string, Func<string, string>)[] { ("upper", static name => name.ToUpperInvariant()), ("lower", static name => name.ToLowerInvariant()) })
            {
                var copy = Path.Combine(directory, label);
                CopyWithFolderNames(Path.Combine(directory, "sql"), Path.Combine(copy, "sql"), convert);
                File.Copy(project, Path.Combine(copy, "pgproj.json"));
                variants.Add(Path.Combine(copy, "pgproj.json"));
            }

            string? firstScript = null;
            foreach (var variant in variants)
            {
                var built = await RunAsync("build", "--project", variant);
                Assert.AreEqual(0, built.ExitCode, variant + "\n" + built.Output);
                StringAssert.Contains(built.Output, "SQL files: 7");

                await AssertDiffIsEmptyAsync(variant, connectionString);

                var dryRun = await RunAsync("publish", "--project", variant, "--target-connection", connectionString, "--dry-run");
                Assert.AreEqual(0, dryRun.ExitCode, dryRun.Output);
                // The script and the counts, without the lines that name where the project is or a file in it, which differ by design.
                var script = string.Join("\n", dryRun.Output.Split('\n').Select(static line => line.TrimEnd('\r')).Where(static line => !line.StartsWith("Source path:", StringComparison.Ordinal) && !Regex.IsMatch(line, @"\.sql:\d")));
                firstScript ??= script;
                Assert.AreEqual(firstScript, script, $"{variant} publishes differently from the original.");
            }
        });
    }

    private static void CopyWithFolderNames(string from, string to, Func<string, string> convertFolder)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.GetDirectories(from))
        {
            CopyWithFolderNames(directory, Path.Combine(to, convertFolder(Path.GetFileName(directory))), convertFolder);
        }
    }

    private static string[] ReadSchemasField(string projectFile)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(projectFile));
        return [.. document.RootElement.GetProperty("schemas").EnumerateArray().Select(static item => item.GetString()!)];
    }

    private static async Task WithDatabaseAsync(Func<string, string, Task> test)
    {
        var databaseName = await CreateDatabaseAsync("g157");
        var directory = Path.Combine(Path.GetTempPath(), "pgproj-g157-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            await ExecuteAsync(connectionString, Sql);
            await test(connectionString, directory);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(directory);
        }
    }
}
