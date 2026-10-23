using Npgsql;
using PgProj.Core;
using PgProj.Deploy;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Engine;

public sealed partial class PgProjEngine
{
    /// <summary>
    /// Plans a publish, or plans and runs it (what <c>pgproj publish</c> does, G-309c): reads the project, compares it with the target database, composes the script, and
    /// checks it. With <see cref="PublishMode.Plan"/> nothing is run; with <see cref="PublishMode.Execute"/> the script is planned again on this call's own connection and
    /// run in a transaction (a project with tenants: the shared part, then each tenant in a transaction of its own), unless a check refuses it. A request that cannot be run,
    /// a refusal, and a failed statement are a <see cref="PublishStatus"/> on the result, not exceptions; a defect is an exception, as is a cancelled
    /// <paramref name="cancellationToken"/>. The engine never writes a file: the script is in the result.
    /// </summary>
    public async Task<PublishResult> PublishAsync(PublishRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sourceMode = !string.IsNullOrWhiteSpace(request.SourceConnectionString);
        if (request.ProjectFilePath is not null && request.Settings is not null)
        {
            throw new ArgumentException("A request names the project by its file (ProjectFilePath) or by its Settings, not both.", nameof(request));
        }

        if (!sourceMode && request.ProjectFilePath is null && request.Settings is null)
        {
            throw new ArgumentException("A request names the project by its file (ProjectFilePath) or by its Settings, and by one of them.", nameof(request));
        }

        if (request.Settings is not null && string.IsNullOrWhiteSpace(request.RootDirectory))
        {
            throw new ArgumentException("A request with Settings needs the RootDirectory its relative paths are resolved against.", nameof(request));
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Every message it returns or reports is masked (G-318); the script and its steps are deployed, printed, and written as they are.
        request = request with { Progress = EngineMasking.Progress(request.Progress) };
        return EngineMasking.Publish(await PublishOrStopAsync(request, cancellationToken));
    }

    private static async Task<PublishResult> PublishOrStopAsync(PublishRequest request, CancellationToken cancellationToken)
    {
        var state = new CompareState();
        if (PublishTexts.RequestProblem(request.Concurrently, request.CommentsOnly, request.LockTimeout) is { } problem)
        {
            return Stopped(state, PublishStatus.Refused, CompareStatus.Refused, problem);
        }

        try
        {
            return await RunPublishAsync(request, state, cancellationToken);
        }
        catch (FileNotFoundException) when (state.Project is null)
        {
            return Stopped(state, PublishStatus.ProjectNotFound, CompareStatus.ProjectNotFound, null);
        }
        catch (PgProjectValidationException exception) when (state.Project is null)
        {
            return Stopped(state, PublishStatus.ProjectInvalid, CompareStatus.ProjectInvalid, exception.Message);
        }
        catch (RefusedException exception)
        {
            return Stopped(state, PublishStatus.Refused, CompareStatus.Refused, exception.Message);
        }
        catch (NpgsqlException exception) when (state.Connecting)
        {
            return Stopped(state, PublishStatus.ConnectionFailed, CompareStatus.ConnectionFailed, exception.Message);
        }
        catch (System.Net.Sockets.SocketException exception) when (state.Connecting)
        {
            return Stopped(state, PublishStatus.ConnectionFailed, CompareStatus.ConnectionFailed, exception.Message);
        }
    }

    private static PublishResult Stopped(CompareState state, PublishStatus status, CompareStatus compareStatus, string? message)
        => new() { Status = status, Message = message, Compare = state.ToResult(compareStatus, message) };

    private static void Say(IProgress<EngineEvent>? progress, EngineEventKind kind, string message, EngineEventLevel level = EngineEventLevel.Information)
        => progress?.Report(new EngineEvent(kind, null, 0, message, level));

    private static PublishPart ToPart(PublishUnit unit)
        => new()
        {
            Label = unit.Label,
            Changes = unit.Changes,
            TransactionalScript = unit.TransactionalScript,
            AfterCommit = [.. unit.AfterCommit.Select(static step => new PublishStep(step.Description, step.Sql))],
            Script = unit.Script,
            SuperuserStatements = SuperuserStatements.Describe(unit.SuperuserStatements),
            HasStatements = unit.HasStatements,
        };

    private static string Lines(params string[] lines) => string.Join(Environment.NewLine, lines);

    /// <summary>A warning, kept for the result and reported as it is found, so a shell can print it in the order the checks find it (G-316).</summary>
    private static void Warn(List<PublishNotice> warnings, IProgress<EngineEvent>? progress, PublishNotice notice)
    {
        warnings.Add(notice);
        progress?.Report(new EngineEvent(EngineEventKind.Notice, null, 0, notice.Text, EngineEventLevel.Warning, notice));
    }

    private static async Task<PublishResult> RunPublishAsync(PublishRequest request, CompareState state, CancellationToken cancellationToken)
    {
        var live = new LiveComparison();
        var progress = request.Progress;
        var executing = request.Mode == PublishMode.Execute;
        var commentsOnly = request.CommentsOnly;
        var concurrently = request.Concurrently;
        var lockTimeout = request.LockTimeout?.Trim();
        var opened = await OpenProjectAsync(
            request.Files,
            request.ProjectFilePath,
            request.Settings,
            request.RootDirectory,
            request.SourceConnectionString,
            request.TargetConnectionString,
            request.ExcludeGrants,
            request.ExcludeRoleMemberships,
            request.ExcludeOwners,
            live,
            progress,
            state,
            cancellationToken);
        var (snapshot, built, source, stopwatch) = (opened.Snapshot, opened.Built, opened.Source, opened.Stopwatch);
        var project = snapshot.Project;
        var model = built.Model;

        if (request.AfterBuild is { } afterBuild)
        {
            await afterBuild(EngineMasking.Build(state.ToBuildResult()), cancellationToken);
        }

        if (model.HasErrors)
        {
            return Stopped(state, PublishStatus.Failed, CompareStatus.Failed, null);
        }

        if (request.Tenants.Count > 0 && project.Tenants is null)
        {
            throw new RefusedException("--tenants is only for a project with tenants ('tenants' in pgproj.json).");
        }

        var targetConnectionString = source?.TargetConnectionString ?? SourceProject.ResolveTarget(request.TargetConnectionString, project.TargetConnectionString);

        stopwatch.Restart();
        state.Connecting = true;
        await using var connection = await LiveComparison.OpenConnectionAsync(targetConnectionString, cancellationToken);
        state.Connecting = false;
        progress?.Report(new EngineEvent(EngineEventKind.TargetConnected, null, stopwatch.ElapsedMilliseconds, $"Connected to PostgreSQL {connection.PostgreSqlVersion} (database \"{connection.Database}\") in {stopwatch.ElapsedMilliseconds} ms."));
        var serverMajor = connection.PostgreSqlVersion.Major;

        // A project with tenants is published in parts: the shared changes, then each tenant in a transaction of its own (G-290c).
        if (project.Tenants is not null)
        {
            await LiveComparison.ApplySearchPathAsync(connection, project, cancellationToken);
            return await PublishTenantsAsync(request, state, live, connection, snapshot, built, cancellationToken);
        }

        DatabaseEnd? sourceEnd = null;
        DatabaseEnd? targetEnd = null;
        if (source is not null)
        {
            await source.EnsureNotTheTargetAsync(connection, cancellationToken);
            sourceEnd = source.Source;
            targetEnd = SourceProject.EndOf(connection);
            Say(progress, EngineEventKind.Direction, SourceProject.Direction(sourceEnd, targetEnd));
        }

        await LiveComparison.ApplySearchPathAsync(connection, project, cancellationToken);
        if (await live.ReadLikeSourcesAsync(connection, model, cancellationToken) is { } likeSources)
        {
            built = ProjectModelBuilder.Build(snapshot, request.ExcludeGrants, request.ExcludeRoleMemberships, request.ExcludeOwners, likeSources, cancellationToken);
            model = built.Model;
            state.SetBuilt(snapshot, built, source, stopwatch.ElapsedMilliseconds);
        }

        var scopeSchemas = LiveComparison.GetLiveScanSchemas(project, model);
        var excludeGrants = request.ExcludeGrants ?? project.ExcludeGrants;
        var excludeRoleMemberships = request.ExcludeRoleMemberships ?? project.ExcludeRoleMemberships;

        // A project that states no owner compares none (an owner only the database has is never a change), so the database's are not read at all (G-146).
        var excludeOwners = (request.ExcludeOwners ?? project.ExcludeOwners) || model.Owners.Count == 0;
        progress?.Report(new EngineEvent(
            EngineEventKind.LiveReadStarted,
            null,
            0,
            scopeSchemas.Count == 0 ? "Reading the live database (all schemas)." : $"Reading the live database (schemas: {string.Join(", ", scopeSchemas)})."));
        stopwatch.Restart();
        var (liveModel, changeSet) = await live.CompareAsync(connection, model, scopeSchemas, excludeGrants, excludeRoleMemberships, excludeOwners, LiveComparison.GetScopeExtensions(model), cancellationToken);
        progress?.Report(new EngineEvent(EngineEventKind.LiveRead, null, stopwatch.ElapsedMilliseconds, $"Read the live database in {stopwatch.ElapsedMilliseconds} ms."));

        var unit = PublishUnit.Build(
            project,
            label: null,
            model,
            liveModel,
            changeSet,
            DeployScripts.ForProject(project, changeSet),
            searchPath: null,
            commentsOnly,
            concurrently,
            serverMajor,
            concurrently ? await PublishTexts.ReadRelationNamesAsync(connection, cancellationToken) : null,
            commentsOnly ? [] : await SuperuserStatements.ReadUntrustedLanguagesAsync(connection, cancellationToken),
            progress);

        // What a printed or written script needs to bound its waits the same way (G-270); --execute sets it for its own transaction.
        var written = lockTimeout is null ? unit.Script : $"SET lock_timeout = '{lockTimeout}';{Environment.NewLine}{Environment.NewLine}{unit.Script}";
        var hasStatements = SqlStatementSplitter.Split(unit.Script).Count > 0;
        var warnings = new List<PublishNotice>();
        var refusals = new List<PublishNotice>();

        // A change that rewrites, scans, or blocks writes to a table that already exists, named before anything runs (G-270); nothing is refused for it.
        var lockWarnings = commentsOnly ? [] : LockWarnings.Find(unit.Transactional, liveModel, model);
        if (lockWarnings.Count > 0)
        {
            Warn(warnings, progress, new PublishNotice(PublishNoticeKind.LockRewrite, LockWarnings.Describe(lockWarnings), PublishItems.LockRewrites(lockWarnings)));
        }

        // A drop, rename, or retype of something a PL/pgSQL body names: PostgreSQL will not stop it and the routine fails when it runs (G-303b).
        var bodyNotes = commentsOnly ? [] : PlpgsqlBodyNotes.Find(changeSet, model);
        if (bodyNotes.Count > 0)
        {
            Warn(warnings, progress, new PublishNotice(PublishNoticeKind.PlpgsqlBody, PlpgsqlBodyNotes.Describe(bodyNotes, "This publish drops, renames, or retypes"), PublishItems.BodyNotes(bodyNotes)));
        }

        progress?.Report(new EngineEvent(
            EngineEventKind.Compared,
            null,
            0,
            $"Compared the project with the live database: {changeSet.Added.Count} added, {changeSet.Removed.Count} removed, {changeSet.Modified.Count} modified; {SqlStatementSplitter.Split(unit.Script).Count} statement(s) in the script."));

        progress?.Report(new EngineEvent(EngineEventKind.ScriptPlanned, Script: written));

        // The script pgproj writes or prints is a bare sequence of statements; only --execute runs it in a transaction (G-165, deferred by the user). A shell that
        // runs the script and does not keep it leaves this out.
        if (hasStatements)
        {
            Warn(warnings, progress, new PublishNotice(PublishNoticeKind.NotTransactional, unit.AfterCommit.Count == 0 ? PublishTexts.ScriptNotTransactionalWarning : PublishTexts.ConcurrentScriptWarning, [], ForKeptScript: true));
        }

        // pgproj never drops a schema (G-166, the user's decision); it says which ones it is leaving behind.
        var undroppedSchemas = commentsOnly ? [] : UndroppedSchemas.Find(liveModel, model);
        if (undroppedSchemas.Count > 0)
        {
            Warn(warnings, progress, new PublishNotice(PublishNoticeKind.UndroppedSchema, UndroppedSchemas.Describe(undroppedSchemas), PublishItems.Schemas(PublishNoticeKind.UndroppedSchema, undroppedSchemas)));
        }

        // Nothing but comments is deployed with --comments-only, so what the rest of the project needs from the server is not checked (G-169).
        var newerThanServer = commentsOnly ? [] : ServerFeatureRequirements.FindNewerThan(model, serverMajor);
        if (newerThanServer.Count > 0 && !executing)
        {
            Warn(warnings, progress, new PublishNotice(PublishNoticeKind.NewerThanServer, PublishTexts.DescribeNewerThanServer(newerThanServer, serverMajor, executing: false), PublishItems.Features(PublishNoticeKind.NewerThanServer, newerThanServer)));
        }

        var missingTablespaces = commentsOnly ? [] : ServerTablespaceRequirements.FindMissing(
            model,
            await ServerTablespaceRequirements.ReadServerTablespacesAsync(connection, cancellationToken));
        if (missingTablespaces.Count > 0 && !executing)
        {
            Warn(warnings, progress, new PublishNotice(PublishNoticeKind.MissingTablespace, PublishTexts.DescribeMissingTablespaces(missingTablespaces, executing: false), PublishItems.Tablespaces(PublishNoticeKind.MissingTablespace, missingTablespaces)));
        }

        var missingRoles = commentsOnly ? [] : ServerRoleRequirements.FindMissing(
            model,
            await ServerRoleRequirements.ReadServerRolesAsync(connection, cancellationToken),
            project.PreDeployScriptContent);
        if (missingRoles.Count > 0 && !executing)
        {
            Warn(warnings, progress, new PublishNotice(PublishNoticeKind.MissingRole, PublishTexts.DescribeMissingRoles(missingRoles, executing: false), PublishItems.Roles(PublishNoticeKind.MissingRole, missingRoles)));
        }

        // The script holds every lock until it commits (G-280): estimated against the server's lock table; refused over it below, warned about near it.
        var lockEstimate = commentsOnly ? 0 : LockCapacity.Estimate(unit.Transactional, liveModel, model);
        var lockTable = lockEstimate == 0 ? null : await LockCapacity.ReadAsync(connection, cancellationToken);
        var overLockTable = lockTable is not null && LockCapacity.IsOver(lockEstimate, lockTable);
        if (lockTable is not null && LockCapacity.IsNear(lockEstimate, lockTable) && (!executing || !overLockTable || request.AllowLockLimit))
        {
            Warn(warnings, progress, new PublishNotice(PublishNoticeKind.LockLimit, "Warning: " + LockCapacity.Describe(lockEstimate, lockTable), []));
        }

        var superuserLines = SuperuserStatements.Describe(unit.SuperuserStatements);
        var superuserItems = PublishItems.Superuser(unit.SuperuserStatements.Select(static statement => ((string?)null, statement)));
        if (unit.SuperuserStatements.Count > 0 && !executing)
        {
            Warn(warnings, progress, new PublishNotice(
                PublishNoticeKind.Superuser,
                "Warning: the script has statements that only a superuser can run; pgproj publish --execute runs them only with " + SuperuserStatements.OptionName + ":"
                    + Environment.NewLine + string.Join(Environment.NewLine, superuserLines.Select(static line => "  " + line)),
                superuserItems));
        }

        if (executing)
        {
            await RefuseAsync();
        }

        var compare = state.ToResult(CompareStatus.Succeeded, null) with
        {
            Source = sourceEnd,
            Target = targetEnd,
            Changes = changeSet,
            AddedCount = changeSet.Added.Count,
            RemovedCount = changeSet.Removed.Count,
            ModifiedCount = changeSet.Modified.Count,
            BodyNotes = bodyNotes,
            UndroppedSchemas = undroppedSchemas,
        };
        var plan = new PublishResult { Status = PublishStatus.Planned, Compare = compare, Parts = [ToPart(unit)], Warnings = warnings, Refusals = refusals, Script = written };
        if (request.AfterPlan is { } afterPlan)
        {
            await afterPlan(EngineMasking.Publish(plan), cancellationToken);
        }

        if (refusals.Count > 0)
        {
            return plan with { Status = PublishStatus.Refused };
        }

        if (!executing)
        {
            return plan;
        }

        // Only comments are deployed: the server reports each one it skipped as a WARNING notice, and a shell shows them too (G-169).
        var serverNotices = new List<PublishNotice>();
        if (commentsOnly)
        {
            connection.Notice += (_, noticeArgs) =>
            {
                if (noticeArgs.Notice.MessageText.StartsWith("pgproj publish --comments-only skipped", StringComparison.Ordinal))
                {
                    Warn(serverNotices, progress, new PublishNotice(PublishNoticeKind.ServerNotice, "Warning: " + noticeArgs.Notice.MessageText, []));
                }
            };
        }

        try
        {
            await ScriptExecution.ExecuteScriptAsync(connection, unit.TransactionalScript, lockTimeout, cancellationToken);
            Say(progress, EngineEventKind.ScriptExecuted, "Executed deployment script.");
            if (unit.AfterCommit.Count > 0)
            {
                Say(progress, EngineEventKind.AfterCommitStarted, $"Running {unit.AfterCommit.Count} statement(s) outside the transaction (--concurrently):");
                await ScriptExecution.ExecuteAfterCommitAsync(connection, unit.AfterCommit, lockTimeout, message => Say(progress, EngineEventKind.AfterCommitStep, "  " + message), cancellationToken);
                Say(progress, EngineEventKind.AfterCommitDone, "Ran the statements outside the transaction.");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var failure = FailureOf(exception);
            return plan with
            {
                Status = PublishStatus.ExecutionFailed,
                Message = failure.Message,
                Warnings = [.. warnings, .. serverNotices],
                Execution = new PublishExecution { Failure = failure, FailureReport = [$"Publish failed: {failure.Message}"] },
            };
        }

        return plan with { Status = PublishStatus.Published, Warnings = [.. warnings, .. serverNotices], Execution = new PublishExecution() };

        // What stops --execute before anything runs, in the order the checks have always been made; the first that fails ends the run.
        async Task RefuseAsync()
        {
            if (newerThanServer.Count > 0)
            {
                refusals.Add(new PublishNotice(PublishNoticeKind.NewerThanServer, $"Publish failed: {PublishTexts.DescribeNewerThanServer(newerThanServer, serverMajor, executing: true)}", PublishItems.Features(PublishNoticeKind.NewerThanServer, newerThanServer)));
                return;
            }

            // Both are reported before returning, so one run shows everything the server lacks.
            if (missingTablespaces.Count > 0)
            {
                refusals.Add(new PublishNotice(PublishNoticeKind.MissingTablespace, $"Publish failed: {PublishTexts.DescribeMissingTablespaces(missingTablespaces, executing: true)}", PublishItems.Tablespaces(PublishNoticeKind.MissingTablespace, missingTablespaces)));
            }

            if (missingRoles.Count > 0)
            {
                refusals.Add(new PublishNotice(PublishNoticeKind.MissingRole, $"Publish failed: {PublishTexts.DescribeMissingRoles(missingRoles, executing: true)}", PublishItems.Roles(PublishNoticeKind.MissingRole, missingRoles)));
            }

            if (refusals.Count > 0)
            {
                return;
            }

            if (overLockTable && !request.AllowLockLimit)
            {
                refusals.Add(new PublishNotice(
                    PublishNoticeKind.LockLimit,
                    Lines($"Publish failed: {LockCapacity.Describe(lockEstimate, lockTable!)}", $"Nothing was run. Pass {LockCapacity.OptionName} to run it anyway."),
                    []));
                return;
            }

            // Consent first, then the role (G-265): without the option the script is refused even for a superuser.
            if (unit.SuperuserStatements.Count > 0)
            {
                var listed = string.Join(Environment.NewLine, superuserLines.Select(static line => "  " + line));
                if (!request.AllowSuperuserStatements)
                {
                    refusals.Add(new PublishNotice(
                        PublishNoticeKind.Superuser,
                        Lines(
                            $"Publish failed: the script has {unit.SuperuserStatements.Count} statement(s) that only a superuser can run, so it was not run:{Environment.NewLine}{listed}",
                            $"Use --output/--dry-run to review the generated script, and pass {SuperuserStatements.OptionName} to run it as a superuser."),
                        superuserItems));
                    return;
                }

                if (!await SuperuserStatements.IsSuperuserAsync(connection, cancellationToken))
                {
                    refusals.Add(new PublishNotice(
                        PublishNoticeKind.Superuser,
                        Lines(
                            $"Publish failed: the connected role is not a superuser, and the script has {unit.SuperuserStatements.Count} statement(s) that only a superuser can run, so it was not run:{Environment.NewLine}{listed}",
                            "Connect as a superuser, or have one run the script (--output writes it)."),
                        superuserItems));
                    return;
                }
            }

            var unsupportedOperations = ScriptComposition.GetUnsupportedOperations(unit.Batch);
            if (unsupportedOperations.Count > 0)
            {
                var items = PublishItems.Unsupported(unsupportedOperations.Select(static operation => ((string?)null, operation)));
                refusals.Add(new PublishNotice(
                    PublishItems.UnsupportedKind(items),
                    Lines(
                        [
                            $"Publish failed: {unsupportedOperations.Count} operation(s) could only emit a TODO/unsupported placeholder and cannot be executed safely.",
                            .. items.Select(static item => $"  {item.Text}"),
                            "Use --output/--dry-run to review the generated script, and address the unsupported operations before running --execute.",
                        ]),
                    items));
                return;
            }

            if (!request.AllowDestructive)
            {
                var destructiveOperations = ScriptComposition.GetDestructiveOperations(unit.Batch);
                if (destructiveOperations.Count > 0)
                {
                    var items = PublishItems.Destructive(destructiveOperations.Select(static operation => ((string?)null, operation)));
                    refusals.Add(new PublishNotice(
                        PublishNoticeKind.Destructive,
                        Lines(
                            [
                                $"Publish failed: {destructiveOperations.Count} operation(s) can discard data and were not executed.",
                                .. items.Select(static item => $"  {item.Text}"),
                                "Use --output/--dry-run to review the generated script, and pass --allow-destructive to run it anyway.",
                            ]),
                        items));
                }
            }
        }
    }

    /// <summary>A statement that failed while the script ran, as a <see cref="PublishFailure"/>: the server's own account when it is the server's.</summary>
    private static PublishFailure FailureOf(Exception exception)
        => exception is ScriptExecutionException scripted
            ? scripted.Failure
            : new PublishFailure(exception.Message, null, null, null, null, AfterCommit: false, []);
}
