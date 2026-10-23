using System.CommandLine;
using PgProj.Parser;

namespace PgProj.Cli;

/// <summary>How much a command says besides its result (G-143): <c>--quiet</c>, nothing; the default, what it always said; <c>--verbose</c>, also what it is doing.</summary>
public enum Verbosity
{
    Quiet,
    Normal,
    Verbose,
}

/// <summary>
/// The CLI's leveled log writer (G-143, design agreed with the user 2026-10-01). A command's <b>result</b> (the script <c>publish</c> prints, the
/// change list <c>diff</c> prints) is written to standard output directly and is never filtered. Everything else goes through this class:
/// <list type="bullet">
/// <item><see cref="Info"/>: progress and summaries, as before to the writer the command already used (standard output, or standard error where
/// standard output carries a result); hidden by <c>--quiet</c>.</item>
/// <item><see cref="Warning"/>: to standard error; hidden by <c>--quiet</c>.</item>
/// <item><see cref="Error"/>: to standard error; always shown, whatever the level.</item>
/// <item><see cref="Detail"/>: to standard error, prefixed <c>[verbose]</c>; shown only by <c>--verbose</c>.</item>
/// </list>
/// Every line passes through the parser's secret redactor (G-141, G-142), so a password in an exception message or a connection string is masked
/// at every level.
/// </summary>
public sealed class CliLog
{
    private readonly TextWriter _info;
    private readonly TextWriter _error;

    public CliLog(Verbosity verbosity, TextWriter info, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(error);
        Verbosity = verbosity;
        _info = info;
        _error = error;
    }

    public Verbosity Verbosity { get; }

    public bool IsQuiet => Verbosity == Verbosity.Quiet;

    public bool IsVerbose => Verbosity == Verbosity.Verbose;

    /// <summary>The writer for <see cref="Info"/>, for a caller that writes several lines to it (the diagnostics list).</summary>
    public TextWriter InfoWriter => _info;

    public void Info(string message)
    {
        if (!IsQuiet)
        {
            _info.WriteLine(SecretRedactor.Redact(message));
        }
    }

    public void Warning(string message)
    {
        if (!IsQuiet)
        {
            _error.WriteLine(SecretRedactor.Redact(message));
        }
    }

    /// <summary>
    /// A line that is written to standard error even with <c>--quiet</c>, for the one case where a command did something other than what it was asked and the
    /// user would otherwise not know where to look (<c>--erd</c> keeping a file a person wrote and writing the diagram under another name).
    /// </summary>
    public void Notice(string message) => _error.WriteLine(SecretRedactor.Redact(message));

    public void Error(string message) => _error.WriteLine(SecretRedactor.Redact(message));

    public void Detail(string message)
    {
        if (IsVerbose)
        {
            _error.WriteLine("[verbose] " + SecretRedactor.Redact(message));
        }
    }
}

/// <summary>The <c>--verbose</c> and <c>--quiet</c> options every command takes (G-143).</summary>
public sealed class LogOptions
{
    private LogOptions()
    {
        Verbose = new Option<bool>("--verbose")
        {
            Description = "Also write what the command is doing (each file read, the server version, the time each step took, the counts) " +
                "to standard error, prefixed '[verbose]'. Secrets in the text are masked. Cannot be combined with --quiet.",
        };
        Quiet = new Option<bool>("--quiet")
        {
            Description = "Write only the command's result and its errors: no progress lines, no warnings, no notices. " +
                "Errors, refusals, and the exit code are unchanged. Cannot be combined with --verbose.",
        };
    }

    public Option<bool> Verbose { get; }

    public Option<bool> Quiet { get; }

    /// <summary>Adds both options to <paramref name="command"/> and rejects the pair together.</summary>
    public static LogOptions AddTo(Command command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var options = new LogOptions();
        command.Options.Add(options.Verbose);
        command.Options.Add(options.Quiet);
        command.Validators.Add(result =>
        {
            if (result.GetValue(options.Verbose) && result.GetValue(options.Quiet))
            {
                result.AddError("--verbose and --quiet cannot be used together.");
            }
        });
        return options;
    }

    /// <summary>
    /// The log for this run. The writers are read now, not when the command was built, so a caller that redirects the console
    /// (a test) is honored. <paramref name="infoToError"/> is for <c>build --format json</c> and <c>diff --format json</c>, where standard output carries only the JSON document.
    /// </summary>
    public CliLog Create(ParseResult parseResult, bool infoToError = false)
    {
        ArgumentNullException.ThrowIfNull(parseResult);

        var verbosity = parseResult.GetValue(Quiet)
            ? Verbosity.Quiet
            : parseResult.GetValue(Verbose) ? Verbosity.Verbose : Verbosity.Normal;
        return new CliLog(verbosity, infoToError ? Console.Error : Console.Out, Console.Error);
    }
}
