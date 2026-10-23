using Npgsql;
using PgProj.Core;
using PgProj.Deploy;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Engine;

public sealed partial class PgProjEngine
{
    private const string SharedLabel = "shared schemas";

    /// <summary>How messages name a part: <c>the shared schemas</c>, or the tenant's schema.</summary>
    private static string PartName(PublishUnit unit) => unit.Label == SharedLabel ? "the shared schemas" : unit.Label!;

    /// <summary>
    /// <c>publish</c> for a project with tenants (G-290c, the user's decisions on #168): the shared changes in one transaction, with the pre- and post-deploy
    /// scripts, then each tenant in a transaction of its own, its script starting with <c>SET search_path TO</c> the tenant's schema and then
    /// <c>searchPath</c>. A tenant the query or file lists whose schema does not exist yet gets it created in its transaction. Every part is planned and
    /// checked before anything runs, as <c>publish</c> checks a single script, except when the tenant query reads a table the shared changes create: the
    /// shared changes are then published first, and the tenants read and checked after. A failure stops the run: what was published stays published,
    /// the failing tenant's transaction is rolled back, and the result names what was done and what was not, so running <c>publish</c> again finishes.
    /// A former tenant's schema is never dropped.
    /// </summary>
    private static async Task<PublishResult> PublishTenantsAsync(
        PublishRequest request,
        CompareState state,
        LiveComparison live,
        NpgsqlConnection connection,
        ProjectSnapshot snapshot,
        ModelBuild built,
        CancellationToken cancellationToken)
    {
        var project = snapshot.Project;
        var workspace = snapshot.Workspace;
        var tenants = project.Tenants!;
        var progress = request.Progress;
        var executing = request.Mode == PublishMode.Execute;
        var commentsOnly = request.CommentsOnly;
        var concurrently = request.Concurrently;
        var lockTimeout = request.LockTimeout?.Trim();
        var sharedTrees = (IReadOnlyList<PgProj.Parser.SqlSyntaxTree>)[.. built.SyntaxTrees.Where(static tree => tree.SearchPath is null)];
        var serverMajor = connection.PostgreSqlVersion.Major;
        var relationNames = concurrently ? await PublishTexts.ReadRelationNamesAsync(connection, cancellationToken) : null;
        var untrustedLanguages = commentsOnly ? [] : await SuperuserStatements.ReadUntrustedLanguagesAsync(connection, cancellationToken);
        var grants = request.ExcludeGrants ?? project.ExcludeGrants;
        var memberships = request.ExcludeRoleMemberships ?? project.ExcludeRoleMemberships;

        // The shared part: every schema but the tenants', with the project's pre- and post-deploy scripts.
        var sharedModel = ProjectModelBuilder.BuildModel(project, sharedTrees, request.ExcludeGrants, request.ExcludeRoleMemberships, request.ExcludeOwners);
        IReadOnlyList<string> sharedScope = project.Schemas.Count > 0
            ? LiveComparison.GetLiveScanSchemas(project, sharedModel)
            : await TenantDiscovery.ReadSharedSchemasAsync(connection, tenants, cancellationToken);
        if (sharedScope.Count == 0)
        {
            sharedScope = [project.DefaultSchema];
        }

        var (sharedLive, sharedChanges) = await live.CompareAsync(
            connection, sharedModel, sharedScope, grants, memberships, (request.ExcludeOwners ?? project.ExcludeOwners) || sharedModel.Owners.Count == 0, LiveComparison.GetScopeExtensions(sharedModel), cancellationToken);
        var shared = PublishUnit.Build(
            project, SharedLabel, sharedModel, sharedLive, sharedChanges, DeployScripts.ForProject(project, sharedChanges), searchPath: null,
            commentsOnly, concurrently, serverMajor, relationNames, untrustedLanguages, progress);

        var previousSearchPath = await TenantComparison.ReadSearchPathAsync(connection, cancellationToken);
        try
        {
            var list = await TenantDiscovery.FindAsync(connection, tenants, request.Tenants, cancellationToken);
            var warnings = new List<PublishNotice>();
            var refusals = new List<PublishNotice>();

            // The query reads a table the shared changes may create: those go first, and the tenants are read after them.
            var tenantsAfterShared = list.Unavailable is not null && executing && shared.HasStatements;
            IReadOnlyList<(TenantTarget Tenant, PublishUnit Unit)> tenantUnits = tenantsAfterShared ? [] : await PlanTenantsAsync(list);
            if (list.Unavailable is not null && !tenantsAfterShared)
            {
                Warn(warnings, progress, new PublishNotice(
                    PublishNoticeKind.TenantsNotRead,
                    list.Unavailable + (executing ? " Nothing in the shared schemas creates it, so no tenant was published." : " They are read once the shared project is published."),
                    []));
            }

            IReadOnlyList<PublishUnit> units = [shared, .. tenantUnits.Select(static pair => pair.Unit)];
            var written = ComposeScript(units, lockTimeout, warnings);

            // The first check that refuses (only with --execute) ends the checks, and has said so in the refusals.
            if (await CheckAllAsync(units, warnings, refusals))
            {
                WarnFormerTenants(list, warnings);
            }

            var plan = new PublishResult
            {
                Status = PublishStatus.Planned,
                Compare = CompareOf(list, tenantUnits),
                Parts = [.. units.Select(ToPart)],
                Warnings = warnings,
                Refusals = refusals,
                Script = written,
            };
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

            var published = new List<string>();
            if (await RunPartAsync(shared, published, [.. units.Skip(1).Select(static unit => unit.Label!)]) is { } sharedFailure)
            {
                return plan with { Status = PublishStatus.ExecutionFailed, Message = sharedFailure.Failure!.Message, Execution = sharedFailure };
            }

            if (tenantsAfterShared)
            {
                // The shared changes are published: the tenants can be read now, then planned and checked as above, the run stopping before any tenant if a check refuses.
                list = await TenantDiscovery.FindAsync(connection, tenants, request.Tenants, cancellationToken);
                if (list.Unavailable is not null)
                {
                    Warn(warnings, progress, new PublishNotice(PublishNoticeKind.TenantsNotRead, list.Unavailable + " The shared changes were published; no tenant was.", []));
                    return plan with { Status = PublishStatus.Published, Warnings = warnings, Execution = new PublishExecution { Published = [.. published] } };
                }

                Say(progress, EngineEventKind.TenantsReadAfterShared, $"Read the tenants after publishing the shared changes: {list.Tenants.Count}.");
                tenantUnits = await PlanTenantsAsync(list);
                units = [shared, .. tenantUnits.Select(static pair => pair.Unit)];
                var lateUnits = units.Skip(1).ToArray();
                if (!await CheckAllAsync(lateUnits, warnings, refusals))
                {
                    return plan with
                    {
                        Status = PublishStatus.Refused,
                        Message = "The shared changes were published; no tenant was. Fix the problems above and publish again.",
                        Compare = CompareOf(list, tenantUnits),
                        Parts = [.. units.Select(ToPart)],
                        Warnings = warnings,
                        Refusals = refusals,
                        Execution = new PublishExecution { Published = [.. published] },
                    };
                }

                WarnFormerTenants(list, warnings);
            }

            for (var index = 0; index < tenantUnits.Count; index++)
            {
                if (await RunPartAsync(tenantUnits[index].Unit, published, [.. tenantUnits.Skip(index + 1).Select(static pair => pair.Unit.Label!)]) is { } failed)
                {
                    return plan with { Status = PublishStatus.ExecutionFailed, Message = failed.Failure!.Message, Compare = CompareOf(list, tenantUnits), Parts = [.. units.Select(ToPart)], Execution = failed, Warnings = warnings };
                }
            }

            // Changes are the schema's; a tenant whose only statements are its scripts (G-290e) is counted apart.
            static bool HasChanges(PublishUnit unit) => unit.Changes.Added.Count + unit.Changes.Removed.Count + unit.Changes.Modified.Count > 0;
            var changed = tenantUnits.Count(static pair => HasChanges(pair.Unit));
            var scriptsOnly = tenantUnits.Count(static pair => pair.Unit.HasStatements && !HasChanges(pair.Unit));
            Say(
                progress,
                EngineEventKind.PublishFinished,
                $"Published: the shared schemas{(HasChanges(shared) ? string.Empty : " (no changes)")} and {tenantUnits.Count} tenant(s), {changed} of them with changes"
                + (scriptsOnly > 0 ? $"; {scriptsOnly} ran only their scripts." : "."));
            return plan with
            {
                Status = PublishStatus.Published,
                Compare = CompareOf(list, tenantUnits),
                Parts = [.. units.Select(ToPart)],
                Warnings = warnings,
                Execution = new PublishExecution { Published = [.. published] },
            };

            // The result's compare part: the shared changes, and each tenant's.
            CompareResult CompareOf(TenantList tenantList, IReadOnlyList<(TenantTarget Tenant, PublishUnit Unit)> tenantPairs)
            {
                var tenantsResult = new TenantsComparison(
                    tenants.StandInSchema,
                    tenantList,
                    [.. tenantPairs.Select(static pair => new TenantDifference(pair.Tenant, pair.Unit.Changes))]);
                var (added, removed, modified) = tenantsResult.Totals;
                return state.ToResult(CompareStatus.Succeeded, null) with
                {
                    Changes = sharedChanges,
                    Tenants = tenantsResult,
                    AddedCount = sharedChanges.Added.Count + added,
                    RemovedCount = sharedChanges.Removed.Count + removed,
                    ModifiedCount = sharedChanges.Modified.Count + modified,
                    UndroppedSchemas = UndroppedSchemas.Find(sharedLive, sharedModel),
                };
            }

            async Task<IReadOnlyList<(TenantTarget Tenant, PublishUnit Unit)>> PlanTenantsAsync(TenantList tenantList)
            {
                var planned = new List<(TenantTarget, PublishUnit)>();
                foreach (var tenant in tenantList.Tenants)
                {
                    var (model, tenantLive, changes) = await TenantComparison.CompareTenantAsync(
                        live, connection, project, workspace, sharedTrees, tenant, request.ExcludeGrants, request.ExcludeRoleMemberships, request.ExcludeOwners, cancellationToken);
                    // A tenant's own scripts (G-290e): the files that run for it, with its variables; none with --comments-only.
                    var scripts = commentsOnly ? (null, null) : DeployScripts.ForTenant(tenants, tenant, changes, tenantLive);
                    planned.Add((tenant, PublishUnit.Build(
                        project, tenant.Schema, model, tenantLive, changes, scripts, model.SearchPathFor(tenant.Schema),
                        commentsOnly, concurrently, serverMajor, relationNames, untrustedLanguages, progress)));
                    progress?.Report(new EngineEvent(
                        EngineEventKind.PartPlanned,
                        Message: $"Planned {tenant.Schema}{(tenant.Exists ? string.Empty : " (a new tenant; its schema is created)")}: {changes.Added.Count} added, {changes.Removed.Count} removed, {changes.Modified.Count} modified."));
                }

                return planned;
            }

            // One part's transaction, then its --concurrently statements. A failure names what was published and what was not.
            async Task<PublishExecution?> RunPartAsync(PublishUnit unit, List<string> publishedSoFar, IReadOnlyList<string> notStarted)
            {
                if (!unit.HasStatements)
                {
                    publishedSoFar.Add(PartName(unit));
                    return null;
                }

                try
                {
                    await ScriptExecution.ExecuteScriptAsync(connection, unit.TransactionalScript, lockTimeout, cancellationToken);
                    if (unit.AfterCommit.Count > 0)
                    {
                        Say(progress, EngineEventKind.AfterCommitStarted, $"Running {unit.AfterCommit.Count} statement(s) of {PartName(unit)} outside the transaction (--concurrently):");
                        await ScriptExecution.ExecuteAfterCommitAsync(connection, unit.AfterCommit, lockTimeout, message => Say(progress, EngineEventKind.AfterCommitStep, "  " + message), cancellationToken);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    var failure = FailureOf(exception);
                    var name = PartName(unit);
                    List<string> report =
                    [
                        $"Publish failed in {name}: {failure.Message}",
                        publishedSoFar.Count == 0 ? $"Nothing was published before {name}." : $"Published before {name}, and kept: {string.Join(", ", publishedSoFar)}.",
                    ];
                    if (notStarted.Count > 0)
                    {
                        report.Add($"Not started: {string.Join(", ", notStarted.Take(20))}{(notStarted.Count > 20 ? $", and {notStarted.Count - 20} more" : string.Empty)}.");
                    }

                    report.Add("Fix the problem and run publish again: what was published has nothing left to do.");
                    return new PublishExecution { Published = [.. publishedSoFar], FailedPart = name, Failure = failure, NotStarted = notStarted, FailureReport = report };
                }

                publishedSoFar.Add(PartName(unit));
                Say(progress, EngineEventKind.PartPublished, $"Published {PartName(unit)}.");
                return null;
            }
        }
        finally
        {
            await TenantComparison.RestoreSearchPathAsync(connection, previousSearchPath, CancellationToken.None);
        }

        // The checks over the parts, in the order publish has always made them; false when one refuses.
        async Task<bool> CheckAllAsync(IReadOnlyList<PublishUnit> units, List<PublishNotice> warnings, List<PublishNotice> refusals)
            => WarnAndCheck(units, warnings, refusals)
                && await CheckServerAsync(units, warnings, refusals)
                && await CheckSuperuserAsync(units, warnings, refusals);

        // The script: the shared part, then each tenant with changes under a line naming it; with a lock timeout, starting with its SET.
        string ComposeScript(IReadOnlyList<PublishUnit> parts, string? timeout, List<PublishNotice> warnings)
        {
            var scripts = parts
                .Where(static unit => unit.HasStatements)
                .Select(static unit => unit.Label == SharedLabel ? unit.Script.TrimEnd() : $"-- pgproj: tenant {unit.Label} (a transaction of its own){Environment.NewLine}{unit.Script.TrimEnd()}")
                .ToArray();
            var script = scripts.Length == 0 ? string.Empty : string.Join(Environment.NewLine + Environment.NewLine, scripts) + Environment.NewLine;
            var composed = timeout is null ? script : $"SET lock_timeout = '{timeout}';{Environment.NewLine}{Environment.NewLine}{script}";
            progress?.Report(new EngineEvent(EngineEventKind.ScriptPlanned, Script: composed));

            // A shell that runs the script and does not keep it leaves this out.
            if (scripts.Length > 0)
            {
                Warn(warnings, progress, new PublishNotice(PublishNoticeKind.NotTransactional, PublishTexts.TenantScriptWarning, [], ForKeptScript: true));
            }

            return composed;
        }

        // The warnings, and the checks that refuse --execute before anything runs, over every part: the lock warnings (G-270), newer-than-server constructs
        // (G-121), unsupported operations (G-28), and destructive ones (G-35). False when one refuses.
        bool WarnAndCheck(IReadOnlyList<PublishUnit> units, List<PublishNotice> warnings, List<PublishNotice> refusals)
        {
            foreach (var unit in units)
            {
                var lockWarnings = commentsOnly ? [] : LockWarnings.Find(unit.Transactional, unit.LiveModel, unit.ProjectModel);
                if (lockWarnings.Count > 0)
                {
                    Warn(warnings, progress, new PublishNotice(PublishNoticeKind.LockRewrite, $"In {PartName(unit)}: " + LockWarnings.Describe(lockWarnings), PublishItems.LockRewrites(lockWarnings, unit.Label), unit.Label));
                }
            }

            if (commentsOnly)
            {
                return true;
            }

            var newer = units.SelectMany(unit => ServerFeatureRequirements.FindNewerThan(unit.ProjectModel, serverMajor)).DistinctBy(static feature => feature.Description).ToArray();
            if (newer.Length > 0)
            {
                var items = PublishItems.Features(PublishNoticeKind.NewerThanServer, newer);
                if (!executing)
                {
                    Warn(warnings, progress, new PublishNotice(PublishNoticeKind.NewerThanServer, PublishTexts.DescribeNewerThanServer(newer, serverMajor, executing: false), items));
                }
                else
                {
                    refusals.Add(new PublishNotice(PublishNoticeKind.NewerThanServer, $"Publish failed: {PublishTexts.DescribeNewerThanServer(newer, serverMajor, executing: true)}", items));
                    return false;
                }
            }

            if (!executing)
            {
                return true;
            }

            var unsupported = PublishItems.Unsupported(units.SelectMany(static unit => unit.Batch.GetUnsupportedOperations().Select(operation => (unit.Label, operation))));
            if (unsupported.Length > 0)
            {
                refusals.Add(new PublishNotice(
                    PublishItems.UnsupportedKind(unsupported),
                    Lines(
                        [
                            $"Publish failed: {unsupported.Length} operation(s) could only emit a TODO/unsupported placeholder and cannot be executed safely; nothing was run.",
                            .. unsupported.Take(50).Select(static item => $"  {item.Text}"),
                            "Use --output/--dry-run to review the generated script, and address the unsupported operations before running --execute.",
                        ]),
                    unsupported));
                return false;
            }

            if (!request.AllowDestructive)
            {
                var destructive = PublishItems.Destructive(units.SelectMany(static unit => unit.Batch.GetDestructiveOperations().Select(operation => (unit.Label, operation))));
                if (destructive.Length > 0)
                {
                    refusals.Add(new PublishNotice(
                        PublishNoticeKind.Destructive,
                        Lines(
                            [
                                $"Publish failed: {destructive.Length} operation(s) can discard data, in {destructive.Select(static item => item.Part).Distinct().Count()} part(s); nothing was run.",
                                .. destructive.Take(50).Select(static item => $"  {item.Text}"),
                                "Use --output/--dry-run to review the generated script, and pass --allow-destructive to run it anyway.",
                            ]),
                        destructive));
                    return false;
                }
            }

            return true;
        }

        // What the server must have for the parts (refused with --execute, warned about otherwise): the tablespaces and roles they name (G-151, G-147), and room in
        // the lock table for each part's transaction (G-280: one transaction per tenant is what bounds it).
        async Task<bool> CheckServerAsync(IReadOnlyList<PublishUnit> units, List<PublishNotice> warnings, List<PublishNotice> refusals)
        {
            if (commentsOnly || units.Count == 0)
            {
                return true;
            }

            var serverTablespaces = await ServerTablespaceRequirements.ReadServerTablespacesAsync(connection, cancellationToken);
            var missingTablespaces = units
                .SelectMany(unit => ServerTablespaceRequirements.FindMissing(unit.ProjectModel, serverTablespaces))
                .GroupBy(static missing => missing.TablespaceName, StringComparer.Ordinal)
                .Select(static group => new MissingTablespace(group.Key, [.. group.SelectMany(static missing => missing.Tables).Distinct(StringComparer.Ordinal)]))
                .ToArray();
            var serverRoles = await ServerRoleRequirements.ReadServerRolesAsync(connection, cancellationToken);
            var missingRoles = units
                .SelectMany(unit => ServerRoleRequirements.FindMissing(unit.ProjectModel, serverRoles, project.PreDeployScriptContent))
                .GroupBy(static missing => missing.RoleName, StringComparer.Ordinal)
                .Select(static group => new MissingRole(group.Key, [.. group.SelectMany(static missing => missing.NamedBy).Distinct(StringComparer.Ordinal)]))
                .ToArray();
            var tablespaceItems = PublishItems.Tablespaces(PublishNoticeKind.MissingTablespace, missingTablespaces);
            var roleItems = PublishItems.Roles(PublishNoticeKind.MissingRole, missingRoles);
            if (!executing)
            {
                if (missingTablespaces.Length > 0)
                {
                    Warn(warnings, progress, new PublishNotice(PublishNoticeKind.MissingTablespace, PublishTexts.DescribeMissingTablespaces(missingTablespaces, executing: false), tablespaceItems));
                }

                if (missingRoles.Length > 0)
                {
                    Warn(warnings, progress, new PublishNotice(PublishNoticeKind.MissingRole, PublishTexts.DescribeMissingRoles(missingRoles, executing: false), roleItems));
                }
            }
            else if (missingTablespaces.Length > 0 || missingRoles.Length > 0)
            {
                if (missingTablespaces.Length > 0)
                {
                    refusals.Add(new PublishNotice(PublishNoticeKind.MissingTablespace, $"Publish failed: {PublishTexts.DescribeMissingTablespaces(missingTablespaces, executing: true)}", tablespaceItems));
                }

                if (missingRoles.Length > 0)
                {
                    refusals.Add(new PublishNotice(PublishNoticeKind.MissingRole, $"Publish failed: {PublishTexts.DescribeMissingRoles(missingRoles, executing: true)}", roleItems));
                }

                return false;
            }

            var estimates = units.Select(static unit => (unit.Label, Locks: LockCapacity.Estimate(unit.Transactional, unit.LiveModel, unit.ProjectModel))).Where(static item => item.Locks > 0).ToArray();
            if (estimates.Length == 0)
            {
                return true;
            }

            var lockTable = await LockCapacity.ReadAsync(connection, cancellationToken);
            var over = new List<string>();
            foreach (var (label, locks) in estimates)
            {
                var isOver = LockCapacity.IsOver(locks, lockTable);
                if (LockCapacity.IsNear(locks, lockTable) && (!executing || !isOver || request.AllowLockLimit))
                {
                    Warn(warnings, progress, new PublishNotice(PublishNoticeKind.LockLimit, $"Warning ({label}): " + LockCapacity.Describe(locks, lockTable), [], label));
                }

                if (isOver && executing && !request.AllowLockLimit)
                {
                    over.Add($"{label}: {LockCapacity.Describe(locks, lockTable)}");
                }
            }

            if (over.Count > 0)
            {
                refusals.Add(new PublishNotice(
                    PublishNoticeKind.LockLimit,
                    Lines("Publish failed: " + string.Join(Environment.NewLine, over), $"Nothing was run. Pass {LockCapacity.OptionName} to run it anyway."),
                    []));
                return false;
            }

            return true;
        }

        // Statements only a superuser can run, in any part (G-265): warned about; with --execute, consent first, then the role, before anything runs.
        async Task<bool> CheckSuperuserAsync(IReadOnlyList<PublishUnit> units, List<PublishNotice> warnings, List<PublishNotice> refusals)
        {
            var items = PublishItems.Superuser(units.SelectMany(static unit => unit.SuperuserStatements.Select(statement => (unit.Label, statement))));
            if (items.Length == 0)
            {
                return true;
            }

            var listed = string.Join(Environment.NewLine, items.Take(50).Select(static item => "  " + item.Text));
            if (!executing)
            {
                Warn(warnings, progress, new PublishNotice(
                    PublishNoticeKind.Superuser,
                    $"Warning: the script has statements that only a superuser can run; pgproj publish --execute runs them only with {SuperuserStatements.OptionName}:{Environment.NewLine}{listed}",
                    items));
                return true;
            }

            if (!request.AllowSuperuserStatements)
            {
                refusals.Add(new PublishNotice(
                    PublishNoticeKind.Superuser,
                    Lines(
                        $"Publish failed: the script has {items.Length} statement(s) that only a superuser can run, so it was not run:{Environment.NewLine}{listed}",
                        $"Use --output/--dry-run to review the generated script, and pass {SuperuserStatements.OptionName} to run it as a superuser."),
                    items));
                return false;
            }

            if (!await SuperuserStatements.IsSuperuserAsync(connection, cancellationToken))
            {
                refusals.Add(new PublishNotice(
                    PublishNoticeKind.Superuser,
                    Lines(
                        $"Publish failed: the connected role is not a superuser, and the script has {items.Length} statement(s) that only a superuser can run, so it was not run:{Environment.NewLine}{listed}",
                        "Connect as a superuser, or have one run the script (--output writes it)."),
                    items));
                return false;
            }

            return true;
        }

        void WarnFormerTenants(TenantList tenantList, List<PublishNotice> warnings)
        {
            if (tenantList.FormerSchemas.Count > 0)
            {
                Warn(warnings, progress, new PublishNotice(PublishNoticeKind.FormerTenant, PublishTexts.DescribeFormerTenants(tenantList.FormerSchemas, forPublish: true), PublishItems.Schemas(PublishNoticeKind.FormerTenant, tenantList.FormerSchemas)));
            }
        }
    }
}
