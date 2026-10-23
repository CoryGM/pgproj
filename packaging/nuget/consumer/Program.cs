// What a user of the PgProj.Engine package does first (G-319): build a project held in memory, and check one document. Parsing goes through the
// native libpg_query the package carries, so a run that succeeds shows the library was found and loaded on this platform.
using PgProj.Core;
using PgProj.Engine;

var root = Path.Combine(Path.GetTempPath(), "pgproj-consumer");
var projectFile = Path.Combine(root, "pgproj.json");
var files = new InMemoryFileSource()
    .Set(projectFile, """{ "name": "consumer", "version": "1.0", "source": "./sql" }""")
    .Set(Path.Combine(root, "sql", "customer.sql"), "CREATE TABLE public.customer (id bigint PRIMARY KEY, email text NOT NULL);\n")
    .Set(Path.Combine(root, "sql", "orders.sql"), "CREATE TABLE public.orders (id bigint PRIMARY KEY, customer_id bigint REFERENCES public.customer (id));\n");

var engine = new PgProjEngine();
var build = await engine.BuildAsync(new BuildRequest { Files = files, ProjectFilePath = projectFile });
Console.WriteLine($"build: {build.Status}, {build.Documents.Count} documents, {build.ErrorCount} errors");
foreach (var diagnostic in build.Diagnostics)
{
    Console.WriteLine($"  {diagnostic.File}:{diagnostic.Line}: [{diagnostic.Severity}] {diagnostic.Message}");
}

var check = await engine.CheckDocumentAsync(new DocumentCheckRequest
{
    Files = files,
    ProjectFilePath = projectFile,
    DocumentPath = Path.Combine(root, "sql", "orders.sql"),
    Text = "CREATE TABLE public.orders (id bigint PRIMARY KEY,, x int);\n",
});
var syntaxError = check.Diagnostics.SingleOrDefault();
Console.WriteLine($"check: {check.Status}, {check.Kind}, {syntaxError?.Message} at {syntaxError?.Line}:{syntaxError?.Column}");

var passed = build.Status == BuildStatus.Succeeded
    && build.Model?.Tables.Count == 2
    && check.Status == DocumentCheckStatus.Succeeded
    && syntaxError is { Severity: EngineSeverity.Error, Line: 1 };
Console.WriteLine(passed ? "consumer test passed" : "consumer test FAILED");
return passed ? 0 : 1;
