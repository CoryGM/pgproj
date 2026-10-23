using System.CommandLine;
using PgProj.Cli;

args = args
    .Select(static argument => string.Equals(argument, "-h", StringComparison.Ordinal)
        ? "--help"
        : argument)
    .ToArray();

args = CommandLineAliases.Normalize(args);

var cliContext = new CliContext();
var rootCommand = new RootCommand("pgproj")
{
    Description =
        """
        PostgreSQL Database Project compiler.

        Parses the SQL files referenced by a pgproj.json project, builds a semantic model of the database schema, and can compare or publish that model against a live PostgreSQL database.

        Examples:
          pgproj init --source-connection "Host=localhost;Database=app"
          pgproj build
          pgproj diff --target-connection "Host=localhost;Database=app"
          pgproj publish --target-connection "Host=localhost;Database=app" --execute
          pgproj document --format html,markdown,json
          pgproj about
        """,
};

rootCommand.Subcommands.Add(InitCommand.Create(cliContext));
rootCommand.Subcommands.Add(BuildCommand.Create(cliContext));
rootCommand.Subcommands.Add(DiffCommand.Create(cliContext));
rootCommand.Subcommands.Add(PublishCommand.Create(cliContext));
rootCommand.Subcommands.Add(DocumentCommand.Create(cliContext));
rootCommand.Subcommands.Add(AboutCommand.Create(cliContext));
HelpLayout.Install(rootCommand);

return await rootCommand.Parse(args).InvokeAsync();