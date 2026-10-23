namespace PgProj.Cli.Tests;

/// <summary>
/// G-290d: <c>init --tenant-prefix</c> moves the chosen tenant's files into the template folder with the schema name taken out, leaves out what belongs to the whole
/// database, writes the CSV file of tenants, and takes its tenant options only with the prefix.
/// </summary>
[TestClass]
public sealed class InitTenantsTests
{
    [TestMethod]
    [DataRow("--tenant-from", "acme")]
    [DataRow("--tenant-file", null)]
    [DataRow("--tenant-query", "SELECT 1")]
    public void ATenantOptionNeedsThePrefix(string option, string? value)
    {
        var command = InitCommand.Create(new CliContext());
        string[] args = value is null ? ["--source-connection", "Host=x", option] : ["--source-connection", "Host=x", option, value];

        var errors = command.Parse(args).Errors.Select(static error => error.Message).ToArray();

        CollectionAssert.Contains(errors, "--tenant-from, --tenant-file, and --tenant-query need --tenant-prefix.");
    }

    [TestMethod]
    public void TheFileAndTheQueryCannotBothBeGiven()
    {
        var errors = InitCommand.Create(new CliContext()).Parse(["--source-connection", "Host=x", "--tenant-prefix", "tenant", "--tenant-file", "--tenant-query", "SELECT 1"]).Errors.Select(static error => error.Message).ToArray();

        CollectionAssert.Contains(errors, "--tenant-file and --tenant-query cannot be used together: the tenants come from a file or from a query.");
    }

    [TestMethod]
    public void TheHelpDescribesTheTenantOptions()
    {
        var command = InitCommand.Create(new CliContext());

        foreach (var option in new[] { "--tenant-prefix", "--tenant-from", "--tenant-file", "--tenant-query" })
        {
            Assert.IsTrue(command.Options.Any(candidate => candidate.Name == option), option);
        }

        StringAssert.Contains(command.Description!, "A database with a schema per tenant (--tenant-prefix tenant");
        StringAssert.Contains(command.Description!, "pgproj init --source-connection \"Host=localhost;Database=app\" --tenant-prefix tenant --tenant-from acme --tenant-file");
    }
}
