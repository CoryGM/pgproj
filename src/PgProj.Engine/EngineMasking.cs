using PgProj.Parser;

namespace PgProj.Engine;

/// <summary>
/// The engine masks every message it returns (G-318, the user's decision of 2026-10-21 on #229), so a shell shows them as they are and needs no masker of its own:
/// statuses' messages, diagnostics, events, the texts of a publish's warnings and refusals, a failed statement's report, and what <c>init</c> and <c>document</c>
/// say. A secret is masked as <see cref="SecretRedactor"/> masks one in a diagnostic (G-141, G-142). The deployment SQL is never masked (a script, a part's script,
/// a <c>--concurrently</c> step, what runs): a build may put a secret into the SQL on purpose, and masking it would change what is deployed. Masking is idempotent,
/// and what has nothing to mask is returned as the same object, so a result a hook was given is the result returned.
/// </summary>
internal static class EngineMasking
{
    public static string? Text(string? text) => text is null ? null : SecretRedactor.Redact(text);

    /// <summary>Every event reported to <paramref name="progress"/> with its message, and its notice's texts, masked; the script an event carries is not.</summary>
    public static IProgress<EngineEvent>? Progress(IProgress<EngineEvent>? progress)
        => progress is null or MaskedProgress ? progress : new MaskedProgress(progress);

    public static EngineEvent Event(EngineEvent value)
    {
        var message = Text(value.Message);
        var notice = Notice(value.Notice);
        return Same(message, value.Message) && ReferenceEquals(notice, value.Notice) ? value : value with { Message = message, Notice = notice };
    }

    public static EngineDiagnostic Diagnostic(EngineDiagnostic diagnostic)
    {
        var message = SecretRedactor.Redact(diagnostic.Message);
        return Same(message, diagnostic.Message) ? diagnostic : diagnostic with { Message = message };
    }

    public static PublishNotice? Notice(PublishNotice? notice)
    {
        if (notice is null)
        {
            return null;
        }

        var text = SecretRedactor.Redact(notice.Text);
        var items = List(notice.Items, Item);
        return Same(text, notice.Text) && ReferenceEquals(items, notice.Items) ? notice : notice with { Text = text, Items = items };
    }

    public static BuildResult Build(BuildResult result)
    {
        var message = Text(result.Message);
        var diagnostics = List(result.Diagnostics, Diagnostic);
        return Same(message, result.Message) && ReferenceEquals(diagnostics, result.Diagnostics) ? result : result with { Message = message, Diagnostics = diagnostics };
    }

    public static CompareResult Compare(CompareResult result)
    {
        var message = Text(result.Message);
        var diagnostics = List(result.Diagnostics, Diagnostic);
        return Same(message, result.Message) && ReferenceEquals(diagnostics, result.Diagnostics) ? result : result with { Message = message, Diagnostics = diagnostics };
    }

    public static PublishResult Publish(PublishResult result)
    {
        var message = Text(result.Message);
        var compare = Compare(result.Compare);
        var warnings = List(result.Warnings, static notice => Notice(notice)!);
        var refusals = List(result.Refusals, static notice => Notice(notice)!);
        var execution = Execution(result.Execution);
        return Same(message, result.Message)
            && ReferenceEquals(compare, result.Compare)
            && ReferenceEquals(warnings, result.Warnings)
            && ReferenceEquals(refusals, result.Refusals)
            && ReferenceEquals(execution, result.Execution)
                ? result
                : result with { Message = message, Compare = compare, Warnings = warnings, Refusals = refusals, Execution = execution };
    }

    public static InitResult Init(InitResult result)
    {
        var message = Text(result.Message);
        var messages = List(result.Messages, static line => Masked(line.Text) is { } text ? line with { Text = text } : line);
        var project = result.Project is null ? null : Build(result.Project);
        return Same(message, result.Message) && ReferenceEquals(messages, result.Messages) && ReferenceEquals(project, result.Project)
            ? result
            : result with { Message = message, Messages = messages, Project = project };
    }

    public static DocumentResult Document(DocumentResult result)
    {
        var message = Text(result.Message);
        var problems = List(result.Problems, static problem => Masked(problem.Message) is { } text ? problem with { Message = text } : problem);
        var build = result.Build is null ? null : Build(result.Build);
        return Same(message, result.Message) && ReferenceEquals(problems, result.Problems) && ReferenceEquals(build, result.Build)
            ? result
            : result with { Message = message, Problems = problems, Build = build };
    }

    public static DocumentCheckResult DocumentCheck(DocumentCheckResult result)
    {
        var message = Text(result.Message);
        var diagnostics = List(result.Diagnostics, Diagnostic);
        return Same(message, result.Message) && ReferenceEquals(diagnostics, result.Diagnostics) ? result : result with { Message = message, Diagnostics = diagnostics };
    }

    private static PublishItem Item(PublishItem item)
    {
        var text = SecretRedactor.Redact(item.Text);
        var detail = Text(item.Detail);
        return Same(text, item.Text) && Same(detail, item.Detail) ? item : item with { Text = text, Detail = detail };
    }

    private static PublishExecution? Execution(PublishExecution? execution)
    {
        if (execution is null)
        {
            return null;
        }

        var failure = execution.Failure;
        if (failure is not null)
        {
            var masked = failure with
            {
                Message = SecretRedactor.Redact(failure.Message),
                Detail = Text(failure.Detail),
                Hint = Text(failure.Hint),
                Statement = Text(failure.Statement),
                NotDone = List(failure.NotDone, SecretRedactor.Redact),
            };
            failure = masked.Equals(failure) ? failure : masked;
        }

        var report = List(execution.FailureReport, SecretRedactor.Redact);
        return ReferenceEquals(failure, execution.Failure) && ReferenceEquals(report, execution.FailureReport)
            ? execution
            : execution with { Failure = failure, FailureReport = report };
    }

    /// <summary>The masked text, or null when masking changes nothing.</summary>
    private static string? Masked(string text)
    {
        var masked = SecretRedactor.Redact(text);
        return Same(masked, text) ? null : masked;
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.Ordinal);

    /// <summary><paramref name="items"/> with <paramref name="mask"/> applied to each; the same list when every item came back as itself.</summary>
    private static IReadOnlyList<T> List<T>(IReadOnlyList<T> items, Func<T, T> mask)
        where T : notnull
    {
        T[]? changed = null;
        for (var i = 0; i < items.Count; i++)
        {
            var masked = mask(items[i]);
            if (changed is null && !ReferenceEquals(masked, items[i]) && !(typeof(T) == typeof(string) && Equals(masked, items[i])))
            {
                changed = [.. items];
            }

            if (changed is not null)
            {
                changed[i] = masked;
            }
        }

        return changed ?? items;
    }

    private sealed class MaskedProgress(IProgress<EngineEvent> inner) : IProgress<EngineEvent>
    {
        public void Report(EngineEvent value) => inner.Report(Event(value));
    }
}
