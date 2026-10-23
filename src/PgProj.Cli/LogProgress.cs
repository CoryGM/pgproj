using PgProj.Engine;

namespace PgProj.Cli;

/// <summary>
/// Writes the engine's progress (G-309b) as the <c>--verbose</c> lines the commands always wrote, word for word and in the order they happen: the engine reports
/// from the thread that runs it, so nothing is queued.
/// </summary>
internal sealed class LogProgress : IProgress<EngineEvent>
{
    private readonly CliLog _log;
    private readonly bool _announceProjectFile;
    private readonly bool _keepsScript;
    private readonly Action<string>? _writeScript;

    /// <param name="log">Where the lines go.</param>
    /// <param name="announceProjectFile">In source mode, say which project is read once the source has been scaffolded, where the command used to say so.</param>
    /// <param name="keepsScript">
    /// Whether the command prints or writes the script (<c>publish</c> without <c>--execute</c>, or with <c>--output</c>): a warning about a kept script (G-316) is
    /// written only then.
    /// </param>
    /// <param name="writeScript">What <c>publish</c> does with the planned script, where it has always done it (G-316).</param>
    public LogProgress(CliLog log, bool announceProjectFile = false, bool keepsScript = true, Action<string>? writeScript = null)
    {
        _log = log;
        _announceProjectFile = announceProjectFile;
        _keepsScript = keepsScript;
        _writeScript = writeScript;
    }

    public void Report(EngineEvent value)
    {
        if (value.Kind == EngineEventKind.ScriptPlanned)
        {
            _writeScript?.Invoke(value.Script ?? string.Empty);
            return;
        }

        if (value.Notice is { ForKeptScript: true } && !_keepsScript)
        {
            return;
        }

        if (value.Message is { } message)
        {
            switch (value.Level)
            {
                case EngineEventLevel.Information:
                    _log.Info(message);
                    break;
                case EngineEventLevel.Warning:
                    _log.Warning(message);
                    break;
                default:
                    _log.Detail(message);
                    break;
            }
        }

        if (_announceProjectFile && value.Kind == EngineEventKind.SourceRead)
        {
            _log.Detail("Reading project file: source:/pgproj.json");
        }
    }
}
