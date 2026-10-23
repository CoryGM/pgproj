using PgProj.Engine;
using System.CommandLine;

namespace PgProj.Cli;

/// <summary>What <c>--erd</c> does with a file already at the path it would write (G-158).</summary>
public enum ErdExisting
{
    /// <summary>Not given: back up a file an earlier <c>--erd</c> wrote, and keep one a person wrote.</summary>
    Default,

    /// <summary>Replace the existing file.</summary>
    Overwrite,

    /// <summary>Rename the existing file to <c>name.&lt;UTC time&gt;.ext</c>, then write the new one at the path.</summary>
    Backup,

    /// <summary>Leave the existing file alone and write the new one to <c>name.&lt;UTC time&gt;.ext</c>.</summary>
    Keep,
}

/// <summary>The <c>--erd</c> option and its schema filters and existing-file mode, which every command takes (G-158).</summary>
public sealed class ErdOptions
{
    private readonly Option<string?> _erd = ErdFiles.CreateOption();

    private readonly Option<string[]> _schemas = new("--erd-schemas")
    {
        HelpName = "schemas",
        Description = "With --erd, draw only the tables in these schemas, as a comma-separated list or the option repeated. A foreign key from a drawn table to a table in a schema left out " +
            "is drawn to a stub marked (external). 'public' is only drawn if it is named. A name the project has no table in is an error. Cannot be combined with --erd-exclude-schemas. A name is folded to lower case unless it is in double quotes, as pg_dump -n does.",
        Arity = ArgumentArity.OneOrMore,
        AllowMultipleArgumentsPerToken = true,
    };

    private readonly Option<string[]> _excludeSchemas = new("--erd-exclude-schemas")
    {
        HelpName = "schemas",
        Description = "With --erd, draw every table except those in these schemas, in the same list format. Cannot be combined with --erd-schemas.",
        Arity = ArgumentArity.OneOrMore,
        AllowMultipleArgumentsPerToken = true,
    };

    private readonly Option<string?> _existing = new("--erd-existing")
    {
        HelpName = "overwrite|backup|keep",
        Description = "With --erd, what to do when a file is already at the path: 'overwrite' replaces it; 'backup' renames it to name.<UTC time>.ext first and writes the new one at the path; " +
            "'keep' leaves it and writes the new one to name.<UTC time>.ext. The .mmd and its .md are handled together. Without this option a file an earlier --erd wrote is backed up, " +
            "and a file that was not written by --erd is kept.",
    };

    private readonly Option<bool> _html = new("--erd-html")
    {
        Description = "With --erd, also write a web page (a .html file of the same path and base name) that draws the diagram in a browser. The page loads Mermaid from a CDN, so it needs internet access to draw.",
    };

    private ErdOptions()
    {
    }

    /// <summary>True when <c>--erd-html</c> was given.</summary>
    public bool Html(ParseResult parseResult) => parseResult.GetValue(_html);

    /// <summary>The mode <c>--erd-existing</c> names, or <see cref="ErdExisting.Default"/> when it is not given.</summary>
    public ErdExisting Existing(ParseResult parseResult)
    {
        ArgumentNullException.ThrowIfNull(parseResult);

        return parseResult.GetResult(_existing) is null ? ErdExisting.Default : ParseExisting(parseResult.GetValue(_existing)) ?? ErdExisting.Default;
    }

    internal static ErdExisting? ParseExisting(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "overwrite" => ErdExisting.Overwrite,
            "backup" => ErdExisting.Backup,
            "keep" => ErdExisting.Keep,
            _ => null,
        };

    /// <summary>Adds <c>--erd</c>, <c>--erd-schemas</c>, and <c>--erd-exclude-schemas</c> to <paramref name="command"/>, refusing the two filters together and either one without <c>--erd</c>.</summary>
    public static ErdOptions AddTo(Command command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var options = new ErdOptions();
        command.Options.Add(options._erd);
        command.Options.Add(options._schemas);
        command.Options.Add(options._excludeSchemas);
        command.Options.Add(options._existing);
        command.Options.Add(options._html);
        command.Validators.Add(result =>
        {
            if (result.GetResult(options._html) is not null && result.GetResult(options._erd) is null)
            {
                result.AddError("--erd-html only applies with --erd.");
            }

            if (result.GetResult(options._existing) is not null)
            {
                if (result.GetResult(options._erd) is null)
                {
                    result.AddError("--erd-existing only applies with --erd.");
                }
                else if (ParseExisting(result.GetValue(options._existing)) is null)
                {
                    result.AddError("--erd-existing takes overwrite, backup, or keep.");
                }
            }

            var schemasGiven = result.GetResult(options._schemas) is not null;
            var excludeGiven = result.GetResult(options._excludeSchemas) is not null;
            if (schemasGiven && excludeGiven)
            {
                result.AddError("--erd-schemas and --erd-exclude-schemas cannot be used together.");
            }
            else if ((schemasGiven || excludeGiven) && result.GetResult(options._erd) is null)
            {
                result.AddError("--erd-schemas and --erd-exclude-schemas only apply with --erd.");
            }
            else if ((schemasGiven && InitCommand.SplitSchemaNames(result.GetValue(options._schemas)).Count == 0)
                || (excludeGiven && InitCommand.SplitSchemaNames(result.GetValue(options._excludeSchemas)).Count == 0))
            {
                result.AddError("A list of schema names is needed after --erd-schemas and --erd-exclude-schemas.");
            }
        });
        return options;
    }

    /// <summary>True when <c>--erd</c> was given, with or without a value.</summary>
    public bool IsRequested(ParseResult parseResult) => ErdFiles.IsRequested(parseResult, _erd);

    /// <summary><c>--erd</c>'s value, or <see langword="null"/> when it has none.</summary>
    public string? Path(ParseResult parseResult) => parseResult.GetValue(_erd);

    public ErdFilter Filter(ParseResult parseResult)
    {
        ArgumentNullException.ThrowIfNull(parseResult);

        if (parseResult.GetResult(_schemas) is not null)
        {
            return new ErdFilter(InitCommand.SplitSchemaNames(parseResult.GetValue(_schemas)), Exclude: false);
        }

        return parseResult.GetResult(_excludeSchemas) is not null
            ? new ErdFilter(InitCommand.SplitSchemaNames(parseResult.GetValue(_excludeSchemas)), Exclude: true)
            : ErdFilter.None;
    }
}
