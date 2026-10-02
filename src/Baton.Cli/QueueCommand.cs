using System.Globalization;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Accounting;
using Baton.Cli.Daemon;
using Baton.Domain;
using Baton.Queue;
using Baton.Status;
using Baton.Store;
using Baton.Vendors;

namespace Baton.Cli;

/// <summary>
/// <c>baton queue add|list|hold|resume|cancel|retire|restore|import</c> (#1934 slice 1): the operator's control surface over
/// the dispatch queue the daemon's scheduler drains. Produces no <see cref="CommandResult"/> — there
/// is no workflow to pump — so it joins <c>trust</c>/<c>keep</c>/<c>watch</c> in <c>Program.cs</c>'s
/// carve-out rather than the CommandResult/FlowStateReporter switch.
/// </summary>
/// <remarks>
/// <b>Nothing here starts a lane</b> — spec/baton.md §13 states that split and why. What it means for
/// this file specifically: list inspections do not write the queue; mutations write queue control
/// state (and, for <c>add</c>, the spec copy and possibly a worktree), never starting a room.
/// </remarks>
public static class QueueCommand
{
    /// <summary>
    /// Test-only controls for the narrow interval after a cleanup claim is durable and before its
    /// mandatory final recheck. Production supplies no controls and always uses the Git probe below.
    /// </summary>
    internal sealed record WorktreeApplyTestHooks(
        Func<QueueWorktreeCleanupClaim, CancellationToken, Task>? AfterClaim = null,
        Func<QueueWorktreeCleanupClaim, CancellationToken, Task>? BeforeProtectedRemoval = null,
        Func<QueueWorktreeCleanupClaim, CancellationToken, Task>? AfterReferenceFence = null,
        bool FailReferenceFence = false,
        Func<string, IReadOnlyList<string>, string, CancellationToken, Task<(int ExitCode, string Output)>>? RunProbeAsync = null,
        QueueWorktreeLivenessProbe? LivenessProbe = null);

    internal static Task<int> ExecuteJanitorNowAsync(
        TextWriter output,
        CancellationToken cancellationToken = default,
        string? repositoryDirectory = null,
        Func<string, CancellationToken, Task<RepositoryIdentity?>>? repositoryResolver = null,
        WorktreeApplyTestHooks? worktreeApplyTestHooks = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        return JanitorNowAsync(
            output,
            cancellationToken,
            repositoryDirectory,
            repositoryResolver ?? RepositoryIdentityResolver.TryResolveAsync,
            worktreeApplyTestHooks);
    }

    public static Task<int> ExecuteAsync(
        QueueOptions options,
        TextWriter output,
        CancellationToken cancellationToken = default,
        string? repositoryDirectory = null)
        => ExecuteAsync(
            options,
            output,
            cancellationToken,
            repositoryDirectory,
            RepositoryIdentityResolver.TryResolveAsync,
            static (issue, sourceRepository, worktreeRoot, repository, lifecycle, writer, token) =>
                IssueWorktreeProvisioner.ProvisionAsync(
                    issue,
                    sourceRepository,
                    worktreeRoot,
                    repository,
                    output: writer,
                    cancellationToken: token,
                    deterministicSourceCeiling: lifecycle));

    /// <summary>
    /// Test seam for the complete queue-add route. Production supplies the canonical repository
    /// resolver and issue provisioner above; tests replace both so they exercise admission and the
    /// durable row without spawning <c>gh</c>/<c>git</c> or creating a live forge branch.
    /// </summary>
    internal static Task<int> ExecuteAsync(
        QueueOptions options,
        TextWriter output,
        CancellationToken cancellationToken,
        string? repositoryDirectory,
        Func<string, CancellationToken, Task<RepositoryIdentity?>> repositoryResolver,
        Func<int, string, string?, string, bool, TextWriter, CancellationToken, Task<IssueWorktreeProvisioner.ProvisionedIssueWorktree>> issueProvisioner,
        Action<string, string>? writeSpecFile = null,
        IGhCliRunner? ghRunner = null,
        WorktreeApplyTestHooks? worktreeApplyTestHooks = null,
        OwnedTaskSubmission? ownedTask = null,
        byte[]? capturedSpecBytes = null,
        Func<string, IReadOnlyList<string>, string, CancellationToken, Task<(int ExitCode, string Output)>>? preparationRunner = null,
        Action? beforePreparationCommit = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(repositoryResolver);
        ArgumentNullException.ThrowIfNull(issueProvisioner);

        return options.Verb switch
        {
            QueueVerb.Add => AddAsync(
                options, output, repositoryDirectory, repositoryResolver, issueProvisioner, writeSpecFile, cancellationToken,
                ownedTask, capturedSpecBytes, preparationRunner, beforePreparationCommit),
            QueueVerb.List => ListAsync(options, output, cancellationToken),
            QueueVerb.Worktrees => WorktreesAsync(
                options.Format, options.Apply, output, repositoryDirectory, cancellationToken, worktreeApplyTestHooks),
            QueueVerb.Hold => SetHoldAsync(true, output, cancellationToken),
            QueueVerb.Resume => SetHoldAsync(false, output, cancellationToken),
            QueueVerb.Cancel => CancelAsync(options.Tag!, output, cancellationToken),
            QueueVerb.Retire => RetireAsync(
                options.Tag!, options.Reason!, options.MergedPullRequest, output, cancellationToken,
                repositoryResolver, ghRunner ?? new GhCliRunner()),
            QueueVerb.Restore => RestoreAsync(options.Tag!, options.Reason!, output, cancellationToken),
            QueueVerb.Import => ImportAsync(options, output, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(options)),
        };
    }

    private static async Task<int> AddAsync(
        QueueOptions options,
        TextWriter output,
        string? repositoryDirectory,
        Func<string, CancellationToken, Task<RepositoryIdentity?>> repositoryResolver,
        Func<int, string, string?, string, bool, TextWriter, CancellationToken, Task<IssueWorktreeProvisioner.ProvisionedIssueWorktree>> issueProvisioner,
        Action<string, string>? writeSpecFile,
        CancellationToken cancellationToken,
        OwnedTaskSubmission? ownedTask,
        byte[]? capturedSpecBytes,
        Func<string, IReadOnlyList<string>, string, CancellationToken, Task<(int ExitCode, string Output)>>? preparationRunner,
        Action? beforePreparationCommit)
    {
        var tag = options.Tag!;
        if (options.Lifecycle && options.DeclaredTaskSize is null)
        {
            throw new CliArgumentException(
                $"A lifecycle queue item requires '--declared-size <{Baton.Domain.TaskSizeDeclaration.Usage}>' and '--size-rationale <clause>'.");
        }

        var specSource = options.SpecFilePath;
        if (capturedSpecBytes is not null && ownedTask is null)
            throw new CliArgumentException("A captured spec snapshot is only valid for an owned task submission.");
        if (ownedTask is not null && (specSource is null) != (capturedSpecBytes is null))
            throw new CliArgumentException("Owned task brief snapshot does not match the declared --spec input.");
        if (specSource is not null && capturedSpecBytes is null && !File.Exists(specSource))
        {
            throw new CliArgumentException(
                $"Spec file '{specSource}' does not exist.",
                "pass an existing file to --spec; the queue copies it, so the original may be deleted afterwards.");
        }

        var settings = await DaemonSettingsStore.LoadAsync(BatonPaths.SettingsFile, cancellationToken).ConfigureAwait(false);
        var (adapter, tier, adapterFromModel, stageSelections) = ResolveTierForAdd(options, settings.Queue);
        IReadOnlyList<string> requirements;
        try
        {
            // QueueOptionsParser owns CLI syntax, but this command also has an in-process test and
            // host seam. Keep the persisted record canonical on both paths; null here means an old
            // imported row, never a freshly added task.
            requirements = TaskRequirements.Normalize(options.Requirements ?? []);
        }
        catch (ArgumentException ex)
        {
            throw new CliArgumentException(ex.Message, "pass a supported --require value, or remove the declaration.");
        }

        // `--issue` provisions a worktree at queue-add time. Check the role against this task's
        // declaration before that side effect, not merely later in the daemon; the scheduler repeats
        // it for imported/hand-edited rows and any role-catalog change between add and launch.
        var role = WorkerRoleCatalog.For(options.Role!);
        var admissionRequirements = requirements
            .Where(requirement => !string.Equals(requirement, TaskRequirements.MemoryAdd, StringComparison.Ordinal))
            .ToArray();
        var admissionItem = new QueueItem
        {
            Tag = options.Tag!,
            Role = options.Role!,
            Workspace = "",
            SpecFile = "",
            Requirements = admissionRequirements,
        };
        // `memory-add` is not a role capability. Its explicit queue declaration is conductor
        // approval, narrowed below to the issue repository and a new durable dispatch identity.
        var requestsMemoryAdd = requirements.Contains(TaskRequirements.MemoryAdd, StringComparer.Ordinal);
        if (requestsMemoryAdd && !WorkerAdapterRegistry.ProvidesHostMediatedExecution(adapter))
        {
            throw new CliArgumentException(
                $"Task requirement '{TaskRequirements.MemoryAdd}' cannot be admitted for adapter '{adapter}': "
                + "this capability requires a host-mediated adapter.",
                "select an adapter that provides host-mediated execution before queueing this capability.");
        }
        var admission = TaskRequirementPreflight.Evaluate(
            admissionItem, role, settings.Queue.RequireDeclaredRequirements);
        if (admission.Result == TaskRequirementAdmission.Refused)
        {
            var missing = admission.Missing is { Count: > 0 }
                ? string.Join(", ", admission.Missing)
                : "an invalid requirement declaration";
            throw new CliArgumentException(
                $"Task requirements are incompatible with role '{options.Role}'s effective grant: missing {missing}. "
                + "Requirements never grant authority.",
                "choose a role whose grant supplies the requirement, or amend --require before queueing the task.");
        }

        // Admission uses the already-resolved tuple the launcher will forward. Refuse before the
        // early tag read, spec copy, worktree provision, or queue mutation so a bad request leaves
        // no queue side effect.
        if (tier.Adapter is { } resolvedAdapter)
        {
            WorkerInvocationValidation.Validate(
                resolvedAdapter, tier.Model, null, tier.Effort, WorkerAdapterRegistry.Default);
        }

        // The launched-tag refusal is raised HERE, before the spec copy and before any worktree is
        // provisioned — not only inside the mutate below (#1939 review). File.Copy(overwrite: true)
        // would otherwise already have replaced the running lane's brief by the time the refusal was
        // raised, which is the exact record that refusal exists to protect. This read is the early
        // half; the mutate re-checks under the file lock, which is where the authority stays.
        var queueSnapshot = await QueueStore.LoadAsync(BatonPaths.QueueFile, cancellationToken).ConfigureAwait(false);
        if (ownedTask is null)
        {
            RefuseIfNotReplaceable(
                queueSnapshot.Items.FirstOrDefault(i => string.Equals(i.Tag, tag, StringComparison.Ordinal)), tag);
        }

        var sourceRepository = repositoryDirectory ?? Directory.GetCurrentDirectory();
        var effectiveWorktreeRoot = options.Issue is not null
            ? IssueWorktreeProvisioner.ResolveWorktreeRoot(settings.Queue.WorktreeRoot, sourceRepository)
            : null;
        var issueRepository = options.Issue is not null
            ? await ResolveIssueRepositoryAsync(sourceRepository, repositoryResolver, cancellationToken).ConfigureAwait(false)
            : null;
        if (ownedTask is not null && (issueRepository != ownedTask.Repository || options.Issue != ownedTask.Issue))
        {
            throw new CliArgumentException("Task identity does not match the canonical issue repository.");
        }
        if (requestsMemoryAdd && issueRepository is null)
        {
            throw new CliArgumentException("'--require memory-add' requires --issue so the grant has one canonical repository.");
        }

        // An issue plus a lifecycle workspace is the explicit retained-checkout form. Its validator
        // owns all Git, liveness, PR and exact-ceiling evidence and runs before any trust/provision/spec/queue mutation.
        var retained = options is { Lifecycle: true, Issue: { }, WorkspaceDirectory: { } };
        var retainedProof = retained
            ? await RetainedIssueWorktreeValidator.ValidateAsync(
                options.WorkspaceDirectory!, options.Issue!.Value, issueRepository!, effectiveWorktreeRoot,
                role, settings.Queue.RequireDeclaredRequirements, admissionRequirements, queueSnapshot.Items, cancellationToken)
                .ConfigureAwait(false)
            : null;

        if (options.Lifecycle && !retained)
        {
            var preflight = await IssueWorktreeProvisioner.PreflightAsync(options.Issue!.Value,
                sourceRepository, effectiveWorktreeRoot, issueRepository!, repositoryResolver,
                preparationRunner, cancellationToken).ConfigureAwait(false);
            var eligibility = RecordedProjectCeilingAdmission.Evaluate(admissionItem, role,
                settings.Queue.RequireDeclaredRequirements, preflight.Ceiling);
            if (eligibility.Admission.Result == TaskRequirementAdmission.Refused)
                throw new CliArgumentException(eligibility.RefusalMessage(preflight.Candidate.Workspace, options.Role!),
                    "choose a role whose grant fits the exact source ceiling before queueing this issue.");
        }

        // Reserve the canonical repository/issue while the queue mutex is held, before the
        // provisioner can create a branch, worktree, or trust entry. Both task and legacy lifecycle
        // adds use this seam; a different tag cannot conceal the same issue's existing lifecycle.
        var reserved = false;
        var reservationId = options.Lifecycle ? Guid.NewGuid().ToString("N") : null;
        if (options.Lifecycle)
        {
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot =>
            {
                var owner = snapshot.Items.FirstOrDefault(i => i.Stage is not null
                    && i.Issue == options.Issue
                    && string.Equals(i.Repository, issueRepository, StringComparison.OrdinalIgnoreCase));
                if (owner is not null)
                {
                    if (ownedTask is not null && owner.OwnedTask is { } prior
                        && prior.Id == ownedTask.Id && prior.InputDigest == ownedTask.InputDigest)
                    {
                        return snapshot;
                    }

                    throw new CliArgumentException(
                        $"Issue {options.Issue} in '{issueRepository}' is already owned by queue item '{owner.Tag}'"
                        + (owner.OwnedTask is null ? "." : $" (task {owner.OwnedTask.Id})."));
                }

                var byTag = snapshot.Items.FirstOrDefault(i => string.Equals(i.Tag, tag, StringComparison.Ordinal));
                RefuseIfNotReplaceable(byTag, tag);
                if (byTag is not null)
                {
                    throw new CliArgumentException($"Tag '{tag}' is already in use; issue reservations cannot replace it.");
                }

                reserved = true;
                using var preparationProcess = Process.GetCurrentProcess();
                var placeholder = new QueueItem
                {
                    Tag = tag,
                    Role = options.Role!,
                    Workspace = string.Empty,
                    SpecFile = BatonPaths.QueueSpecFile(tag),
                    Repository = issueRepository,
                    Issue = options.Issue,
                    Stage = WorkStage.Implement,
                    DeclaredTaskSize = options.DeclaredTaskSize ?? Baton.Domain.TaskSizeDeclaration.Unknown,
                    AddedAt = DateTimeOffset.UtcNow,
                    OwnedTask = ownedTask,
                    IssuePreparation = new QueueIssuePreparation(TaskPreparationState.Preparing, DateTimeOffset.UtcNow,
                        ProcessId: Environment.ProcessId,
                        ProcessStartedAt: preparationProcess.StartTime.ToUniversalTime(), ReservationId: reservationId),
                };
                return snapshot with { Items = snapshot.Items.Append(placeholder).ToList() };
            }, cancellationToken).ConfigureAwait(false);

            if (!reserved)
            {
                output.WriteLine($"Task '{ownedTask!.Id}' is already retained as '{tag}'.");
                return 0;
            }
        }

        // Fresh issue adds retain their original provisioning route. Explicit retained reuse never
        // invokes this delegate, so it cannot create a suffix or alter the path's trust record.
        try
        {
            var provisioned = options.Issue is { } issue && !retained
                ? await issueProvisioner(
                    issue,
                    sourceRepository,
                    effectiveWorktreeRoot,
                    issueRepository!,
                    options.Lifecycle,
                    output,
                    cancellationToken).ConfigureAwait(false)
                : null;
            var workspace = retainedProof?.Workspace ?? provisioned?.Workspace ?? Path.GetFullPath(options.WorkspaceDirectory!);

            if (!Directory.Exists(workspace))
            {
                throw new CliArgumentException(
                    $"Workspace '{workspace}' does not exist.",
                    "create it, or pass --issue <n> to have the queue provision a worktree for you.");
            }

            if (options.Lifecycle)
            {
                // Persist the provisioner's exact result before further admission/brief work. A
                // crash before this checkpoint remains uncertain; a crash after it has a concrete
                // workspace/branch to inspect. Neither case authorizes a second provision call.
                await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
                {
                    Items = snapshot.Items.Select(i => i.Tag != tag ? i
                        : i is { State: QueueItemState.Queued, CancelledAt: null, Retirement: null }
                        && i.IssuePreparation is { State: TaskPreparationState.Preparing } preparation
                        && preparation.ReservationId == reservationId
                        && i.Repository == issueRepository && i.Issue == options.Issue
                        && i.OwnedTask?.Id == ownedTask?.Id
                        ? i with
                        {
                            IssuePreparation = preparation with
                            {
                                ExpectedBranch = retainedProof?.Branch ?? provisioned?.Branch,
                                ExpectedWorkspace = workspace,
                            },
                        }
                        : throw new CliArgumentException($"Issue preparation for '{tag}' changed before its provisioned result was recorded; side effects require reconciliation.")).ToList(),
                }, cancellationToken).ConfigureAwait(false);
            }

            // A recorded project ceiling is part of the effective grant, not a permission request.
            // Refuse before spec/queue/WIP writes. --issue may already have provisioned the named path.
            var projectAdmission = retainedProof?.Admission ?? RecordedProjectCeilingAdmission.Evaluate(
                admissionItem with { Workspace = workspace }, role, settings.Queue.RequireDeclaredRequirements);
            if (projectAdmission.Admission.Result == TaskRequirementAdmission.Refused)
            {
                throw new CliArgumentException(
                    projectAdmission.RefusalMessage(workspace, options.Role!),
                    $"choose a role that fits this ceiling, or explicitly trust that exact workspace for the needed categories, then re-add '{tag}'; a provisioned worktree remains.");
            }

            admission = projectAdmission.Admission;

            // Q6: the spec is COPIED, not referenced. The runner's briefs were rewritten inline eight
            // times in one evening (#1934 body); an item that launched days later against whatever the
            // file had become is the failure this copy exists to stop.
            EnsureQueueSpecsDirectory();
            var specDestination = BatonPaths.QueueSpecFile(tag);

            // Shipped pools remain one candidate in this migration. Freeze that exact tuple now so a
            // later tier-file edit cannot change the worker between queue display and launch.
            var frozenAssignment = FrozenWorkerAssignment.ForLegacyTier(tier, DateTimeOffset.UtcNow);
            var item = new QueueItem
            {
                Tag = tag,
                Role = options.Role!,
                Workspace = workspace,
                SpecFile = specDestination,
                ScopeClass = options.ScopeClass?.ToLowerInvariant(),
                Adapter = adapter,
                Model = options.Model,
                Effort = options.Effort,
                WorkerAssignment = frozenAssignment,
                DeclaredTaskSize = options.DeclaredTaskSize ?? Baton.Domain.TaskSizeDeclaration.Unknown,
                Skills = options.Skills,
                Requirements = requirements,
                MemoryAddGrant = requestsMemoryAdd
                    ? new MemoryAddDispatchGrant(Guid.NewGuid().ToString("N"), issueRepository!)
                    : null,
                LastAdmission = admission,
                StageSelections = stageSelections,
                LifecyclePin = options.LifecyclePin,
                LifecycleReason = options.LifecycleReason,
                TimeoutMinutes = options.TimeoutMinutes,
                MaxToolSteps = options.MaxToolSteps,
                MaxRepeatedToolSteps = options.MaxRepeatedToolSteps,
                TokenBudget = options.TokenBudget,
                OverrideRunwayReason = options.OverrideRunwayReason,
                Reason = options.Reason,
                Issue = options.Issue,
                Stage = options.Lifecycle ? WorkStage.Implement : null,
                // Every --issue row needs the provisioner's exact branch. Lifecycle rows use it for
                // advancement; ordinary rows retain the same durable anchor for later PR discovery.
                Branch = retainedProof?.Branch ?? provisioned?.Branch,
                Repository = issueRepository,
                // Explicit false distinguishes a newly-created lifecycle item from a pre-#2131 item
                // whose persisted history has no trustworthy automatic-fix budget.
                AutomaticFixUsed = options.Lifecycle ? false : null,
                // A retained checkout was supplied by the operator. Baton proved it safe to reuse, but
                // did not create it and therefore must never later treat it as cleanup-owned.
                WorkspaceOrigin = retainedProof is not null
                    ? WorkspaceOrigins.OperatorSupplied
                    : options.Issue is not null ? WorkspaceOrigins.IssueProvisioned : WorkspaceOrigins.OperatorSupplied,
                RetainedWorktreeReuse = retainedProof is null ? null : new RetainedWorktreeReuse(
                    retainedProof.Repository, retainedProof.Branch, retainedProof.Head, new RetainedWorktreeCeiling(
                        retainedProof.Ceiling.ReadFiles, retainedProof.Ceiling.WriteFiles,
                        retainedProof.Ceiling.RunShellCommands, retainedProof.Ceiling.NetworkAccess,
                        retainedProof.Ceiling.InheritedFrom),
                    retainedProof.TerminalPredecessorTags),
                AddedAt = DateTimeOffset.UtcNow,
                OwnedTask = ownedTask,
                IssuePreparation = options.Lifecycle
                    ? new QueueIssuePreparation(TaskPreparationState.Prepared, DateTimeOffset.UtcNow,
                        ExpectedBranch: retainedProof?.Branch ?? provisioned?.Branch,
                        ExpectedWorkspace: workspace, ReservationId: reservationId)
                    : null,
            };

            string specContents;
            if (options.Lifecycle)
            {
                // The templates are the product (operator ruling 2026-09-06): a work item's brief is
                // RENDERED here, not written by hand and copied. A --spec is still honoured -- its text
                // becomes the template's "## Do" section -- so an operator who has already written the
                // instructions keeps them, and gets the standing rules and the ship block for free.
                var (title, body) = specSource is null
                    ? await IssueWorktreeProvisioner.FetchIssueAsync(
                        options.Issue!.Value, sourceRepository, issueRepository!,
                        cancellationToken: cancellationToken).ConfigureAwait(false)
                    : ($"Implement #{options.Issue}", capturedSpecBytes is null
                        ? await File.ReadAllTextAsync(specSource, cancellationToken).ConfigureAwait(false)
                        : ReadCapturedSpec(capturedSpecBytes));

                // Captured on the ITEM as well as rendered into the brief -- QueueItem.Instructions' own
                // remarks say why the brief cannot be the register for this.
                item = item with { Instructions = body.Trim() };

                specContents = QueueBriefTemplates.Compose(
                    WorkStage.Implement, item, new QueueBriefTemplates.BriefContext(Title: title, Do: body.Trim()));
            }
            else
            {
                specContents = await File.ReadAllTextAsync(specSource!, cancellationToken).ConfigureAwait(false);
            }

            var replaced = false;
            // Internal deterministic test seam for drift after admission/rendering. Public callers
            // supply no callback; the mutation's own CAS and ceiling read remain authoritative.
            beforePreparationCommit?.Invoke();
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot =>
            {
                // A tag is an identity, not just a label: it names one spec file, so two items sharing one
                // would silently share a brief. Re-adding a tag that is still QUEUED replaces it (the
                // operator is editing their list); re-adding one that has LAUNCHED is refused, because the
                // running lane's own record would be overwritten.
                var existing = snapshot.Items.FirstOrDefault(i => string.Equals(i.Tag, tag, StringComparison.Ordinal));
                if (options.Lifecycle)
                {
                    if (existing is not { State: QueueItemState.Queued, CancelledAt: null, Retirement: null }
                        || existing.IssuePreparation?.State != TaskPreparationState.Preparing
                        || existing.IssuePreparation.ReservationId != reservationId
                        || existing.Repository != issueRepository || existing.Issue != options.Issue
                        || existing.OwnedTask?.Id != ownedTask?.Id)
                    {
                        throw new CliArgumentException($"Issue preparation for '{tag}' changed before commit; its side effects require reconciliation.");
                    }
                }
                else
                {
                    RefuseIfNotReplaceable(existing, tag);
                }

                if (retainedProof is not null)
                {
                    RetainedIssueWorktreeValidator.RefuseIfLiveQueueOwnership(
                        snapshot.Items, retainedProof.Workspace, retainedProof.Branch);
                }

                // Trust can change while the brief is rendered. Re-read the exact target ceiling at
                // the Prepared CAS; preflight and the earlier trust writer cannot authorize stale admission.
                if (options.Lifecycle)
                {
                    var finalAdmission = RecordedProjectCeilingAdmission.Evaluate(
                        admissionItem with { Workspace = workspace }, role, settings.Queue.RequireDeclaredRequirements);
                    if (!finalAdmission.CeilingFound || finalAdmission.Admission.Result == TaskRequirementAdmission.Refused)
                        throw new CliArgumentException(finalAdmission.RefusalMessage(workspace, options.Role!));
                    item = item with { LastAdmission = finalAdmission.Admission };
                }

                // The copied brief is part of replacing this tag, not a preliminary side effect. Keep it
                // inside the queue's authoritative mutation so a cancellation that wins the same lock is
                // refused before it can overwrite the retained brief.
                WriteSpecFile(specDestination, specContents, writeSpecFile ?? WriteSpecFileAtomically);

                replaced = existing is not null;
                var items = snapshot.Items.Where(i => !string.Equals(i.Tag, tag, StringComparison.Ordinal)).ToList();
                items.Add(item);
                return snapshot with { Items = items };
            }, cancellationToken).ConfigureAwait(false);

            output.WriteLine($"{(replaced ? "Replaced" : "Queued")} '{tag}' ({item.Role}) in {workspace}");
            output.WriteLine($"  spec: {specDestination}");
            output.WriteLine($"  tier: {DescribeTier(tier, adapterFromModel)}");
            output.WriteLine($"  assignment: {frozenAssignment.Adapter}/{frozenAssignment.Model ?? "role-default"}/{frozenAssignment.Effort ?? "role-default"} ({frozenAssignment.DecisionId})");
            if (tier.IsOverride)
            {
                output.WriteLine($"  override: {tier.OverrideReason}");
            }

            return 0;
        }
        catch (Exception ex) when (options.Lifecycle && reserved && ex is not OperationCanceledException)
        {
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
            {
                Items = snapshot.Items.Select(i => i.Tag == tag
                    && i.State == QueueItemState.Queued && i.CancelledAt is null
                    && i.IssuePreparation?.State == TaskPreparationState.Preparing
                    && i.IssuePreparation.ReservationId == reservationId
                    ? i with
                    {
                        IssuePreparation = i.IssuePreparation with
                        {
                            State = TaskPreparationState.Blocked,
                            Reason = $"preparation-failed: {ex.GetType().Name}: {ex.Message}",
                        },
                        OwnedTask = i.OwnedTask is null ? null : i.OwnedTask with
                        {
                            Blocked = new TaskBlockedDisposition("preparation-failed", ex.Message,
                                DateTimeOffset.UtcNow, null),
                        },
                    }
                    : i).ToList(),
            }, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static string ReadCapturedSpec(byte[] bytes)
    {
        // Match File.ReadAllText's UTF-8 default and BOM detection, but read the bytes TaskCommand
        // already hashed. The mutable source path is never reopened after reservation.
        using var reader = new StreamReader(new MemoryStream(bytes, writable: false), Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Replaces a queued brief only after its complete successor is durable at a sibling path. A
    /// destination held open by a Windows reader can therefore refuse replacement without exposing
    /// that reader to a truncated brief or changing the queue row that still names the old one.
    /// </summary>
    private static void WriteSpecFile(string destination, string contents, Action<string, string> write)
    {
        try
        {
            write(destination, contents);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw QueueSpecWriteFailure(destination, ex);
        }
    }

    private static void EnsureQueueSpecsDirectory()
    {
        try
        {
            Directory.CreateDirectory(BatonPaths.QueueSpecsDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw QueueSpecWriteFailure(BatonPaths.QueueSpecsDirectory, ex);
        }
    }

    private static CliArgumentException QueueSpecWriteFailure(string path, Exception exception) => new(
        $"Could not write the queue spec at '{path}': {exception.Message}",
        "make the queue-spec path writable, then retry 'baton queue add'.");

    internal static void WriteSpecFileAtomically(string destination, string contents)
    {
        var temporary = $"{destination}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, contents);
            File.Move(temporary, destination, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception cleanupEx) when (cleanupEx is IOException or UnauthorizedAccessException)
            {
                // The destination remains authoritative; a failed cleanup must not hide its write failure.
            }

            throw;
        }
    }

    /// <summary>
    /// Captures the existing canonical remote identity before issue provisioning can mutate anything. The
    /// common-directory fallback is deliberately insufficient: it identifies local worktrees for
    /// accounting, but cannot be passed to <c>gh --repo</c> as lifecycle ownership.
    /// </summary>
    private static async Task<string> ResolveIssueRepositoryAsync(
        string sourceRepository,
        Func<string, CancellationToken, Task<RepositoryIdentity?>> repositoryResolver,
        CancellationToken cancellationToken)
    {
        var identity = await repositoryResolver(sourceRepository, cancellationToken).ConfigureAwait(false);
        if (identity?.RemoteValue is not { Length: > 0 } repository)
        {
            throw new CliArgumentException(
                $"Cannot establish a canonical remote repository identity for issue provisioning from '{sourceRepository}'.",
                "configure that checkout's origin remote, then re-run 'baton queue add --issue <n>'.");
        }

        return repository;
    }

    /// <summary>
    /// Every refusal `add` makes twice — once before it touches anything, once under the file lock.
    /// One method so the two can never word them differently.
    /// </summary>
    /// <remarks>
    /// <b>The second refusal is slice 2's</b> (#1934). A work item's tag defaults to <c>&lt;n&gt;-lane</c>,
    /// so re-running the same <c>--lifecycle</c> add is an ordinary thing to type — and without this it
    /// would replace an item at <c>fix</c> round 3 with a fresh <c>implement</c> round 0 and overwrite
    /// its rendered brief, silently discarding the rounds and the findings that produced them. The
    /// launched-tag refusal does not cover it: an item between rounds is <em>queued</em>, not launched.
    /// An item still at <c>implement</c> is left replaceable, because there is no history to lose.
    /// </remarks>
    private static void RefuseIfNotReplaceable(QueueItem? existing, string tag)
    {
        if (existing?.OwnedTask is { } task)
        {
            throw new CliArgumentException(
                $"Item '{tag}' belongs to retained task '{task.Id}'. Re-adding it would erase task ownership and evidence.",
                $"inspect 'baton task status {task.Id}' instead.");
        }

        if (existing?.IssuePreparation is { State: not TaskPreparationState.Prepared })
        {
            throw new CliArgumentException(
                $"Item '{tag}' has an unfinished issue preparation reservation. Re-adding it could repeat external side effects.");
        }

        if (existing?.ReplacementReviewAction is not null)
        {
            throw new CliArgumentException(
                $"Item '{tag}' retains a replacement-review action. Re-adding it would erase its single-use identity.",
                "inspect the retained action and choose a different tag for new work.");
        }

        if (existing?.DraftPullRequestCreateMarker is not null)
        {
            throw new CliArgumentException(
                $"Item '{tag}' retains a may-have-called draft PR create marker. Re-adding it would erase at-most-once evidence.",
                "reconcile the exact marked PR or choose a different tag for genuinely new work.");
        }

        if (existing?.Retirement is not null)
        {
            throw new CliArgumentException(
                $"Item '{tag}' is retired as '{existing.Retirement.Kind}'. Re-adding it would erase its retained disposition evidence.",
                "pick a different tag; merged work cannot be restored, and operator-retired work must be restored in place.");
        }

        if (existing?.ReadinessMutationClaim is { Length: > 0 })
        {
            throw new CliArgumentException(
                $"Item '{tag}' has an in-flight pull-request readiness update. Re-adding it would supersede "
                + "an operation that is already authorized.",
                "wait for the daemon to finish reconciliation, then retry.");
        }

        if (existing is { State: QueueItemState.Launched })
        {
            throw new CliArgumentException(
                $"Item '{tag}' is already launched into room '{existing.RoomDirectory}'. Re-adding it would "
                + "overwrite that lane's record.",
                "pick a different tag, or wait for the lane to settle.");
        }

        if (existing is { State: QueueItemState.Cancelled })
        {
            throw new CliArgumentException(
                $"Item '{tag}' was cancelled before launch. Re-adding it would overwrite that cancellation record.",
                "pick a different tag for new work.");
        }

        if (existing?.Stage is { } stage && stage != WorkStage.Implement)
        {
            throw new CliArgumentException(
                $"Item '{tag}' is a work item at stage '{WorkStages.Token(stage)}' (round {existing.Round}). "
                + "Re-adding it would reset it to implement round 0 and overwrite the brief its next round "
                + "runs, losing the reviewer's findings.",
                "let the daemon advance it, or pick a different tag if this is genuinely new work.");
        }
    }

    private static async Task<int> WorktreesAsync(
        QueueWorktreesOutputFormat format,
        bool apply,
        TextWriter output,
        string? repositoryDirectory,
        CancellationToken cancellationToken,
        WorktreeApplyTestHooks? worktreeApplyTestHooks = null)
    {
        var snapshot = await QueueStore.LoadAsync(BatonPaths.QueueFile, cancellationToken).ConfigureAwait(false);
        var settings = await DaemonSettingsStore.LoadAsync(BatonPaths.SettingsFile, cancellationToken).ConfigureAwait(false);
        var sourceRepository = Path.GetFullPath(repositoryDirectory ?? Directory.GetCurrentDirectory());
        var worktreeRoot = settings.Queue.WorktreeRoot ?? Path.GetDirectoryName(sourceRepository);
        var report = await QueueWorktreeReport.CreateAsync(
            snapshot.Items, worktreeRoot, cancellationToken,
            livenessProbe: worktreeApplyTestHooks?.LivenessProbe).ConfigureAwait(false);
        if (!apply)
        {
            output.WriteLine(format == QueueWorktreesOutputFormat.Json ? report.ToJson() : report.ToText());
            return 0;
        }

        return await ApplyWorktreeReportAsync(
            report, sourceRepository, worktreeRoot, snapshot.Items, output, format, cancellationToken,
            worktreeApplyTestHooks, _ => true, printSummary: false).ConfigureAwait(false);
    }

    private static async Task<int> JanitorNowAsync(
        TextWriter output,
        CancellationToken cancellationToken,
        string? repositoryDirectory,
        Func<string, CancellationToken, Task<RepositoryIdentity?>> repositoryResolver,
        WorktreeApplyTestHooks? worktreeApplyTestHooks)
    {
        var snapshot = await QueueStore.LoadAsync(BatonPaths.QueueFile, cancellationToken).ConfigureAwait(false);
        var settings = await DaemonSettingsStore.LoadAsync(BatonPaths.SettingsFile, cancellationToken).ConfigureAwait(false);
        var sourceRepository = Path.GetFullPath(repositoryDirectory ?? Directory.GetCurrentDirectory());
        var worktreeRoot = settings.Queue.WorktreeRoot ?? Path.GetDirectoryName(sourceRepository);
        var currentRepository = await repositoryResolver(sourceRepository, cancellationToken).ConfigureAwait(false);
        var scopedItems = currentRepository is null
            ? []
            : snapshot.Items
                .Where(item => string.Equals(item.Repository, currentRepository.Value, StringComparison.Ordinal))
                .ToList();
        var report = await QueueWorktreeReport.CreateAsync(
            scopedItems,
            worktreeRoot,
            cancellationToken,
            repositoryResolver,
            worktreeApplyTestHooks?.LivenessProbe).ConfigureAwait(false);

        if (currentRepository is null)
        {
            output.WriteLine("Janitor now: current repository identity unavailable; no worktrees selected.");
        }

        return await ApplyWorktreeReportAsync(
            report,
            sourceRepository,
            worktreeRoot,
            scopedItems,
            output,
            QueueWorktreesOutputFormat.Text,
            cancellationToken,
            worktreeApplyTestHooks,
            claim => currentRepository is not null
                && string.Equals(claim.Repository, currentRepository.Value, StringComparison.Ordinal),
            printSummary: true).ConfigureAwait(false);
    }

    private static async Task<int> ApplyWorktreeReportAsync(
        QueueWorktreeReport report,
        string sourceRepository,
        string? worktreeRoot,
        IReadOnlyList<QueueItem> observedItems,
        TextWriter output,
        QueueWorktreesOutputFormat format,
        CancellationToken cancellationToken,
        WorktreeApplyTestHooks? worktreeApplyTestHooks,
        Func<QueueWorktreeCleanupClaim, bool> claimIsInScope,
        bool printSummary)
    {
        var dispositions = new Dictionary<string, string>(QueueWorktreeReport.PathComparer);
        // An unreceipted claim can only be left by a process that did not finish its apply operation.
        // Settle it before considering a fresh claim: the replacement claim below fences the full
        // final recheck, while the old non-success receipt remains the durable crash observation.
        foreach (var active in await QueueStore.GetActiveWorktreeCleanupClaimsAsync(
                     BatonPaths.QueueFile, cancellationToken).ConfigureAwait(false))
        {
            if (!claimIsInScope(active)) continue;
            using var recoveryLease = await QueueStore.TryAcquireWorktreeCleanupOperationAsync(
                BatonPaths.QueueFile, active.Path, cancellationToken).ConfigureAwait(false);
            if (recoveryLease is null) continue;
            var adopted = await QueueStore.TryAdoptWorktreeCleanupClaimAsync(
                BatonPaths.QueueFile, active.Path, recoveryLease, cancellationToken).ConfigureAwait(false);
            if (adopted is null || adopted.Id != active.Id) continue;

            var candidate = report.Workspaces.SingleOrDefault(entry =>
                QueueWorktreeReport.PathComparer.Equals(entry.Path, adopted.Path)
                && entry.Classification == "candidate"
                && string.Equals(entry.Git.ExpectedRepository, adopted.Repository, StringComparison.Ordinal)
                && string.Equals(entry.Git.ExpectedBranch, adopted.Branch, StringComparison.Ordinal)
                && string.Equals(entry.Git.Head, adopted.Head, StringComparison.Ordinal));
            if (candidate is null)
            {
                await QueueStore.CompleteWorktreeCleanupAsync(
                    BatonPaths.QueueFile, adopted, "refused", "abandoned-claim-final-recheck-not-candidate",
                    cancellationToken: cancellationToken, ownerId: recoveryLease.OwnerId).ConfigureAwait(false);
                dispositions[adopted.Path] = "refused";
                continue;
            }

            await QueueStore.CompleteWorktreeCleanupAsync(
                BatonPaths.QueueFile, adopted, "race-lost", "abandoned-claim-recovered",
                cancellationToken: cancellationToken, observedBytes: candidate.SizeBytes, ownerId: recoveryLease.OwnerId).ConfigureAwait(false);
            dispositions[candidate.Path] = await ApplyWorktreeCandidateAsync(
                candidate, sourceRepository, worktreeRoot, observedItems, cancellationToken, worktreeApplyTestHooks, recoveryLease).ConfigureAwait(false);
        }

        foreach (var candidate in report.Workspaces.Where(entry =>
                     entry.Classification == "candidate" && !dispositions.ContainsKey(entry.Path)))
        {
            dispositions[candidate.Path] = await ApplyWorktreeCandidateAsync(
                candidate, sourceRepository, worktreeRoot, observedItems, cancellationToken, worktreeApplyTestHooks).ConfigureAwait(false);
        }

        var applied = report with
        {
            Workspaces = report.Workspaces.Select(entry => dispositions.TryGetValue(entry.Path, out var disposition)
                ? entry with { CleanupDisposition = disposition }
                : entry).ToList(),
        };
        output.WriteLine(format == QueueWorktreesOutputFormat.Json ? applied.ToJson() : applied.ToText());
        if (printSummary)
        {
            var removed = dispositions.Values.Count(disposition => disposition == "removed");
            var retained = applied.Workspaces.Count(entry => entry.Classification == "retain")
                + dispositions.Values.Count(disposition => disposition == "retained");
            var refused = dispositions.Values.Count(disposition => disposition == "refused");
            var raceLost = dispositions.Values.Count(disposition => disposition == "race-lost");
            var unknown = applied.Workspaces.Count(entry => entry.Classification == "unknown");
            output.WriteLine(
                $"Janitor now: removed {removed}; retained {retained}; refused {refused}; "
                + $"race-lost {raceLost}; unknown {unknown}; changed {removed}.");
            if (removed == 0) output.WriteLine("Janitor now changed nothing.");
            return 0;
        }

        return dispositions.Values.Any(disposition => disposition is "retained" or "refused") ? 1 : 0;
    }

    private static async Task<string> ApplyWorktreeCandidateAsync(
        QueueWorktreeEntry candidate,
        string sourceRepository,
        string? worktreeRoot,
        IReadOnlyList<QueueItem> observedItems,
        CancellationToken cancellationToken,
        WorktreeApplyTestHooks? worktreeApplyTestHooks,
        QueueWorktreeCleanupOperationLease? existingLease = null)
    {
        var repository = candidate.Git.ExpectedRepository;
        var branch = candidate.Git.ExpectedBranch;
        var head = candidate.Git.Head;
        if (repository is null || branch is null || head is null)
        {
            return "refused";
        }

        using var acquiredLease = existingLease is null
            ? await QueueStore.TryAcquireWorktreeCleanupOperationAsync(BatonPaths.QueueFile, candidate.Path, cancellationToken).ConfigureAwait(false)
            : null;
        var lease = existingLease ?? acquiredLease;
        if (lease is null) return "race-lost";

        var claim = await QueueStore.TryClaimWorktreeCleanupAsync(
            BatonPaths.QueueFile, candidate.Path, repository, branch, head,
            cancellationToken: cancellationToken,
            queueRevision: QueueStore.ComputeRevision(observedItems),
            classification: candidate.Classification,
            ownerId: lease.OwnerId).ConfigureAwait(false);
        if (claim is null) return "race-lost";

        if (worktreeApplyTestHooks?.AfterClaim is { } afterClaim)
        {
            await afterClaim(claim, cancellationToken).ConfigureAwait(false);
        }

        // A workspace is removed only while Baton holds this OS-fenced durable claim for that exact
        // resolved path and a final recheck still proves every condition that made the same report
        // classify it as a static candidate. Missing, stale, conflicting, or unavailable evidence removes nothing.
        var entry = await RecheckClaimAsync(claim, worktreeRoot, cancellationToken, worktreeApplyTestHooks).ConfigureAwait(false);
        if (entry is null)
        {
            await QueueStore.CompleteWorktreeCleanupAsync(
                BatonPaths.QueueFile, claim, "refused", "final-recheck-not-candidate",
                cancellationToken: cancellationToken, ownerId: lease.OwnerId).ConfigureAwait(false);
            return "refused";
        }

        var runProbe = worktreeApplyTestHooks?.RunProbeAsync ?? IssueWorktreeProvisioner.RunRetainedProbeAsync;
        var owningCheckout = await ResolveOwningCheckoutAsync(claim, cancellationToken, runProbe).ConfigureAwait(false);
        if (owningCheckout is null)
        {
            await QueueStore.CompleteWorktreeCleanupAsync(
                BatonPaths.QueueFile, claim, "refused", "owning-repository-unavailable",
                cancellationToken: cancellationToken, observedBytes: entry.SizeBytes, ownerId: lease.OwnerId).ConfigureAwait(false);
            return "refused";
        }

        if (worktreeApplyTestHooks?.BeforeProtectedRemoval is { } beforeProtectedRemoval)
        {
            await beforeProtectedRemoval(claim, cancellationToken).ConfigureAwait(false);
        }

        // Hold Git's prepared expected-old-value transaction through the last proof, non-force
        // removal, and branch postcondition. The operation lease only excludes Baton; this lock makes
        // a competing Git ref update fail while cleanup is in progress.
        await using var referenceFence = worktreeApplyTestHooks?.FailReferenceFence == true
            ? null
            : await GitReferenceFence.TryAcquireAsync(owningCheckout, claim.Branch, claim.Head, cancellationToken).ConfigureAwait(false);
        if (referenceFence is null)
        {
            await QueueStore.CompleteWorktreeCleanupAsync(
                BatonPaths.QueueFile, claim, "refused", "git-reference-fence-unavailable",
                cancellationToken: cancellationToken, observedBytes: entry.SizeBytes, ownerId: lease.OwnerId).ConfigureAwait(false);
            return "refused";
        }

        if (worktreeApplyTestHooks?.AfterReferenceFence is { } afterReferenceFence)
        {
            await afterReferenceFence(claim, cancellationToken).ConfigureAwait(false);
        }

        // The lease remains held for this last protected proof and the mutation. The active claim
        // excludes launch and provisioning; the prepared Git transaction retains the exact branch
        // value through removal and its postcondition.
        entry = await RecheckClaimAsync(claim, worktreeRoot, cancellationToken, worktreeApplyTestHooks).ConfigureAwait(false);
        var owned = await QueueStore.IsWorktreeCleanupClaimActiveAndOwnedAsync(
            BatonPaths.QueueFile, claim, lease.OwnerId, cancellationToken).ConfigureAwait(false);
        if (entry is null || !owned || !referenceFence.IsHeld)
        {
            var reason = !owned ? "cleanup-claim-ownership-lost"
                : !referenceFence.IsHeld ? "git-reference-fence-lost"
                : "final-protected-recheck-not-candidate";
            await QueueStore.CompleteWorktreeCleanupAsync(
                BatonPaths.QueueFile, claim, "refused", reason,
                cancellationToken: cancellationToken, observedBytes: entry?.SizeBytes, ownerId: lease.OwnerId).ConfigureAwait(false);
            return "refused";
        }

        var removal = await runProbe(
            "git", ["worktree", "remove", claim.Path], owningCheckout, cancellationToken).ConfigureAwait(false);
        if (removal.ExitCode != 0)
        {
            await QueueStore.CompleteWorktreeCleanupAsync(
                BatonPaths.QueueFile, claim, "retained", "git-worktree-remove-failed",
                cancellationToken: cancellationToken, observedBytes: entry.SizeBytes, ownerId: lease.OwnerId).ConfigureAwait(false);
            return "retained";
        }

        var registered = await runProbe(
            "git", ["worktree", "list", "--porcelain"], owningCheckout, cancellationToken).ConfigureAwait(false);
        var branchHead = await runProbe(
            "git", ["rev-parse", "--verify", "refs/heads/" + claim.Branch], owningCheckout, cancellationToken).ConfigureAwait(false);
        var absentFromRegistration = registered.ExitCode == 0
            && !registered.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.StartsWith("worktree ", StringComparison.Ordinal))
                .Select(line => QueueWorktreeReport.TryFullPath(line[9..]))
                .Any(path => QueueWorktreeReport.PathComparer.Equals(path, claim.Path));
        var branchPreserved = branchHead.ExitCode == 0
            && string.Equals(branchHead.Output.Trim(), claim.Head, StringComparison.Ordinal);
        if (Directory.Exists(claim.Path) || !absentFromRegistration || !branchPreserved)
        {
            await QueueStore.CompleteWorktreeCleanupAsync(
                BatonPaths.QueueFile, claim, "retained", "postcondition-contradictory",
                cancellationToken: cancellationToken, observedBytes: entry.SizeBytes, ownerId: lease.OwnerId).ConfigureAwait(false);
            return "retained";
        }

        await QueueStore.CompleteWorktreeCleanupAsync(
            BatonPaths.QueueFile, claim, "removed", "removed",
            cancellationToken: cancellationToken, observedBytes: entry.SizeBytes, ownerId: lease.OwnerId).ConfigureAwait(false);
        return "removed";
    }

    /// <summary>Holds Git's lock for one expected branch value until cleanup releases the transaction.</summary>
    private sealed class GitReferenceFence : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _standardErrorTask;
        private Task<string?>? _pendingStandardOutputRead;
        private bool _prepared;

        private GitReferenceFence(Process process, Task<string> standardErrorTask)
        {
            _process = process;
            _standardErrorTask = standardErrorTask;
        }

        public bool IsHeld => _prepared && !_process.HasExited;

        public static async Task<GitReferenceFence?> TryAcquireAsync(
            string checkout, string branch, string expectedHead, CancellationToken cancellationToken)
        {
            var startInfo = ChildProcessStartInfo.Create("git", info =>
            {
                info.WorkingDirectory = checkout;
                info.RedirectStandardInput = true;
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;
                info.StandardOutputEncoding = Encoding.UTF8;
                info.StandardErrorEncoding = Encoding.UTF8;
            });
            startInfo.ArgumentList.Add("update-ref");
            startInfo.ArgumentList.Add("--stdin");

            Process? process = null;
            GitReferenceFence? fence = null;
            try
            {
                process = Process.Start(startInfo);
                if (process is null) return null;

                fence = new GitReferenceFence(process, process.StandardError.ReadToEndAsync());
                await process.StandardInput.WriteAsync("start\n").ConfigureAwait(false);
                await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (!await fence.ReadAcknowledgementAsync("start: ok", cancellationToken).ConfigureAwait(false))
                {
                    await fence.DisposeAsync().ConfigureAwait(false);
                    return null;
                }

                await process.StandardInput.WriteAsync(
                    $"update refs/heads/{branch} {expectedHead} {expectedHead}\nprepare\n").ConfigureAwait(false);
                await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (!await fence.ReadAcknowledgementAsync("prepare: ok", cancellationToken).ConfigureAwait(false))
                {
                    await fence.DisposeAsync().ConfigureAwait(false);
                    return null;
                }

                fence._prepared = true;
                return fence;
            }
            catch (OperationCanceledException)
            {
                if (fence is not null) await fence.DisposeAsync().ConfigureAwait(false);
                else process?.Dispose();
                throw;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                if (fence is not null) await fence.DisposeAsync().ConfigureAwait(false);
                else process?.Dispose();
                return null;
            }
        }

        private async Task<bool> ReadAcknowledgementAsync(string expected, CancellationToken cancellationToken)
        {
            var read = _process.StandardOutput.ReadLineAsync();
            _pendingStandardOutputRead = read;
            try
            {
                var line = await read.WaitAsync(cancellationToken).ConfigureAwait(false);
                return string.Equals(line, expected, StringComparison.Ordinal);
            }
            finally
            {
                if (read.IsCompleted) _pendingStandardOutputRead = null;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!_process.HasExited)
                {
                    try
                    {
                        await _process.StandardInput.WriteAsync("abort\n").ConfigureAwait(false);
                        await _process.StandardInput.FlushAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or IOException or ObjectDisposedException)
                    {
                    }

                    _process.StandardInput.Close();
                    await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                if (_pendingStandardOutputRead is { } standardOutputRead)
                {
                    await standardOutputRead.ConfigureAwait(false);
                }

                await _standardErrorTask.ConfigureAwait(false);
                _process.Dispose();
            }
        }
    }

    private static async Task<QueueWorktreeEntry?> RecheckClaimAsync(
        QueueWorktreeCleanupClaim claim,
        string? worktreeRoot,
        CancellationToken cancellationToken,
        WorktreeApplyTestHooks? hooks)
    {
        var current = await QueueStore.LoadAsync(BatonPaths.QueueFile, cancellationToken).ConfigureAwait(false);
        var recheck = await QueueWorktreeReport.CreateAsync(
            current.Items, worktreeRoot, cancellationToken, livenessProbe: hooks?.LivenessProbe).ConfigureAwait(false);
        var entry = recheck.Workspaces.SingleOrDefault(workspace => QueueWorktreeReport.PathComparer.Equals(workspace.Path, claim.Path));
        return entry is { Classification: "candidate" }
            && string.Equals(entry.Git.ExpectedRepository, claim.Repository, StringComparison.Ordinal)
            && string.Equals(entry.Git.ExpectedBranch, claim.Branch, StringComparison.Ordinal)
            && string.Equals(entry.Git.Head, claim.Head, StringComparison.Ordinal)
            ? entry
            : null;
    }

    private static async Task<string?> ResolveOwningCheckoutAsync(
        QueueWorktreeCleanupClaim claim,
        CancellationToken cancellationToken,
        Func<string, IReadOnlyList<string>, string, CancellationToken, Task<(int ExitCode, string Output)>> runProbe)
    {
        var commonDirectory = await runProbe(
            "git", ["rev-parse", "--path-format=absolute", "--git-common-dir"], claim.Path, cancellationToken)
            .ConfigureAwait(false);
        if (commonDirectory.ExitCode != 0 || string.IsNullOrWhiteSpace(commonDirectory.Output)) return null;

        var checkout = Directory.GetParent(Path.GetFullPath(commonDirectory.Output.Trim()))?.FullName;
        if (checkout is null) return null;
        var identity = await RepositoryIdentityResolver.TryResolveAsync(checkout, cancellationToken).ConfigureAwait(false);
        return string.Equals(identity?.Value, claim.Repository, StringComparison.Ordinal) ? checkout : null;
    }

    private static async Task<int> ListAsync(
        QueueOptions options,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var snapshot = await QueueStore.LoadAsync(BatonPaths.QueueFile, cancellationToken).ConfigureAwait(false);
        if (options.Recovery)
        {
            return await QueueRecoveryInspection.WriteAsync(options, snapshot, output, cancellationToken)
                .ConfigureAwait(false);
        }
        var active = options.Active || options.ListFormat == QueueListOutputFormat.Json && !options.IncludeRetained;
        var items = options.IncludeRetained
            ? snapshot.Items
            : active
                ? snapshot.Items.Where(IsActive).ToList()
                : snapshot.Items;
        var decisions = await QueueDecisionLedgerStore
            .ReadAllAsync(BatonPaths.QueueDecisionLedgerFile, cancellationToken).ConfigureAwait(false);
        if (options.ListFormat == QueueListOutputFormat.Json)
        {
            return await ListJsonAsync(options, snapshot, items, decisions, output, cancellationToken).ConfigureAwait(false);
        }

        var rows = await QueueInspectionProjection
            .ProjectAsync(
                items, snapshot.PullRequestObservations, decisions, cancellationToken,
                captureUnavailableRoomEvidence: true)
            .ConfigureAwait(false);
        var settings = snapshot.Items.Any(i => i.Stage is not null)
            ? (await DaemonSettingsStore.LoadAsync(BatonPaths.SettingsFile, cancellationToken).ConfigureAwait(false)).Queue
            : null;
        if (snapshot.Held)
        {
            output.WriteLine("Queue is HELD — no new launches until 'baton queue resume'. Live lanes are unaffected.");
        }

        await PrintWaitAsync(output, cancellationToken).ConfigureAwait(false);

        if (settings is not null)
        {
            var recorded = decisions.LastOrDefault(entry => entry.Decision is
                QueueDecisionEntry.Waited or QueueDecisionEntry.Launched);
            if (recorded is
                {
                    ActiveLifecycles: { } activeCount,
                    PrePullRequestLifecycles: { } prePrCount,
                    LiveReviews: { } reviewCount,
                    ConsumingLifecycles: { } occupants,
                })
            {
                output.WriteLine($"Lifecycle WIP (recorded {recorded.At:O}): active {activeCount} / "
                    + $"{settings.EffectiveMaxActiveLifecycles}; pre-PR {prePrCount} / "
                    + $"{settings.EffectiveMaxPrePullRequestLifecycles}; live reviews {reviewCount} / "
                    + $"{settings.EffectiveMaxLiveReviews}");
                output.WriteLine($"  occupying lifecycles, oldest first: "
                    + (occupants.Count == 0 ? "none" : string.Join(", ", occupants)));
                if (recorded.Tag is { Length: > 0 } selected)
                {
                    output.WriteLine($"  last scheduler selection: {selected} "
                        + $"({recorded.PriorityBand ?? "band unrecorded"})"
                        + (recorded.PassedNewWorkHead == true ? "; passed new-work head" : string.Empty)
                        + (recorded.NewWorkHeadCap is { Length: > 0 } cap ? $"; new head held by {cap}" : string.Empty));
                }
            }
            else
            {
                output.WriteLine("Lifecycle WIP: scheduler decision not recorded; counts unavailable.");
            }
        }

        if (items.Count == 0)
        {
            output.WriteLine(active && snapshot.Items.Count > 0 ? "No active queue items." : "Queue is empty.");
            return 0;
        }

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var row = rows[index];
            // `halted` in the status column: an item the queue has given up on and one that merely
            // failed its lane and is still an advance candidate otherwise print identically, and
            // halted is precisely the fact that tells the operator they must act — nothing in the
            // product clears it (QueueItem.Halted).
            var state = row.Lifecycle.State + (row.Lifecycle.Halted ? " halted" : string.Empty);
            var where = row.Room.Directory is { Length: > 0 } room ? $"  room: {room}" : string.Empty;
            var external = item.External ? "  (external — counted, never launched)" : string.Empty;

            // The stage, when there is one. A slice-1 dispatch request prints exactly what it printed
            // before -- the absence of a stage is the absence of a lifecycle, and inventing a word for
            // it ("none", "single") would read as a stage the product has.
            var stage = row.Lifecycle.Stage is { } workStage
                ? $"  stage: {workStage}"
                    + (item.Round > 0 ? $" (round {item.Round})" : string.Empty)
                    + (item.PullRequest is { } pr ? $"  PR #{pr}" : string.Empty)
                : string.Empty;
            output.WriteLine($"{item.Tag}  {state}  {item.Role}{stage}{external}{where}");
            if (item.Retirement is { } retirement)
            {
                output.WriteLine($"  retired: {retirement.Kind} at {retirement.At:O}; {retirement.Reason}");
            }
            if (item.Stage is not null && settings is not null)
            {
                output.WriteLine($"  effective stage plan: {DescribeStagePlan(item, settings)}");
            }
            if (row.Room.Settlement is { } settlement)
            {
                var reason = settlement.Reason is { Length: > 0 } detail ? $": {detail}" : string.Empty;
                output.WriteLine($"  settlement: conductor {settlement.Kind} ({settlement.State}){reason}");
            }
            else if (item.Error is { Length: > 0 } error)
            {
                output.WriteLine($"  error: {error}");
            }
            if (row.Room.ObservationError is { Length: > 0 } observationError)
            {
                output.WriteLine($"  room evidence unavailable: {observationError}");
            }
            output.WriteLine(row.Requirements.Declared is null
                ? "  requirements: unknown (legacy migration row)"
                : $"  requirements: {(row.Requirements.Declared.Count == 0 ? "none" : string.Join(", ", row.Requirements.Declared))}");
            if (row.Routing.WorkerAssignment is { } assignment)
            {
                output.WriteLine($"  assignment: {assignment.Adapter}/{assignment.Model ?? "role-default"}/{assignment.Effort ?? "role-default"} ({assignment.DecisionId}; {assignment.ClosedReason})");
            }
            if (row.Admission is { } admission)
            {
                var missing = admission.Missing is { Count: > 0 }
                    ? $"; missing {string.Join(", ", admission.Missing)}"
                    : string.Empty;
                output.WriteLine($"  admission: {admission.Result}{missing}; effective grant: "
                    + $"{(admission.EffectiveGrant.Count == 0 ? "none" : string.Join(", ", admission.EffectiveGrant))}");
            }
        }

        var known = items.Count(item => item.Requirements is not null);
        output.WriteLine($"Requirement coverage: {known}/{items.Count} declared"
            + (active ? " (selected)" : string.Empty) + "; "
            + $"{items.Count - known} unknown migration row(s).");

        return 0;
    }

    private static async Task<int> ListJsonAsync(
        QueueOptions options,
        QueueSnapshot snapshot,
        IReadOnlyList<QueueItem> items,
        IReadOnlyList<QueueDecisionEntry> decisions,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var fingerprint = QueueInspectionProjection.Fingerprint(snapshot);
        var pageSize = options.PageSize ?? QueueInspectionProjection.DefaultPageSize;
        if (pageSize is < 1 or > QueueInspectionProjection.MaxPageSize)
        {
            throw new CliArgumentException(
                $"'--page-size' must be between 1 and {QueueInspectionProjection.MaxPageSize}. {QueueOptionsParser.Usage}");
        }
        var activeSelection = !options.IncludeRetained;
        var window = QueueInspectionProjection.PageItems(
            items,
            fingerprint,
            activeSelection,
            options.IncludeRetained,
            pageSize,
            options.Cursor);
        var rows = await QueueInspectionProjection
            .ProjectAsync(
                window.Items, snapshot.PullRequestObservations, decisions, cancellationToken,
                captureUnavailableRoomEvidence: true)
            .ConfigureAwait(false);
        var document = new QueueInspectionDocument(
            SchemaVersion: 1,
            Selection: options.IncludeRetained ? "history" : "active",
            SnapshotFingerprint: fingerprint,
            PageSize: pageSize,
            HasMore: window.NextCursor is not null,
            NextCursor: window.NextCursor,
            ObservationConsistency: "queue-snapshot; room observations are read separately and may not be atomic with it",
            Held: snapshot.Held,
            Items: rows);
        await output.WriteLineAsync(JsonSerializer.Serialize(
            document,
            new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            })).ConfigureAwait(false);
        return 0;
    }

    private static bool IsActive(QueueItem item) =>
        item.Retirement is null && (item.State is QueueItemState.Queued or QueueItemState.Launched
        || item.Stage is not null && item.State is QueueItemState.Done or QueueItemState.Failed);

    internal static async Task<int> SetHoldAsync(bool held, TextWriter output, CancellationToken cancellationToken)
    {
        await QueueStore.MutateAsync(
            BatonPaths.QueueFile, snapshot => snapshot with { Held = held }, cancellationToken).ConfigureAwait(false);
        output.WriteLine(held
            ? "Queue held. The daemon keeps running and live lanes are untouched; no new item will launch."
            : "Queue resumed. The next scheduler tick may launch an item.");
        return 0;
    }

    private static string DescribeStagePlan(QueueItem item, QueueSettings settings)
    {
        try
        {
            return string.Join("; ", new[]
            {
                WorkStage.Implement, WorkStage.Review, WorkStage.Fix, WorkStage.ReReview, WorkStage.Continue,
            }.Select(stage =>
            {
                var resolved = QueueTierTable.ResolveForStage(
                    item, stage, settings, WorkerRoleCatalog.QueueTierFor, WorkerRoleCatalog.QueueTierForRole);
                return $"{WorkStages.Token(stage)}={resolved.Adapter ?? "role-default"}/"
                    + $"{resolved.Model ?? "role-default"}/{resolved.Effort ?? "role-default"} "
                    + $"({SelectionSourceToken(resolved.SelectionSource)})";
            }));
        }
        catch (KeyNotFoundException ex)
        {
            return $"unavailable ({ex.Message})";
        }
    }

    private static string SelectionSourceToken(QueueSelectionSource source) => source switch
    {
        QueueSelectionSource.StageDefault => "stage-default",
        QueueSelectionSource.StageOverride => "stage-override",
        QueueSelectionSource.LifecyclePin => "lifecycle-pin",
        QueueSelectionSource.PersistedLifecycleCompatibility => "persisted-compatibility",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown selection source."),
    };

    /// <summary>Cancels one request that has not been launched.</summary>
    /// <remarks>
    /// The state transition is inside <see cref="QueueStore.MutateAsync"/>, the same mutex-protected
    /// read-modify-write seam the scheduler uses to claim a launch. Thus either this mutation changes
    /// <c>Queued</c> to <c>Cancelled</c>, or the scheduler has already changed it to <c>Launched</c> and
    /// the operator is directed to the room-level cancellation verb. The item, its copied brief, and
    /// its provisioned worktree are deliberately retained; the item state and the decision ledger are
    /// the durable cancellation record.
    /// </remarks>
    internal static async Task<int> CancelAsync(
        string tag, TextWriter output, CancellationToken cancellationToken,
        Func<CancellationToken, Task>? beforePrelaunchCommit = null,
        Action? beforePrelaunchWrite = null)
    {
        var initial = await QueueStore.LoadAsync(BatonPaths.QueueFile, cancellationToken).ConfigureAwait(false);
        var target = initial.Items.FirstOrDefault(item => item.Tag == tag);
        if (target is { State: QueueItemState.Queued, Stage: WorkStage.Implement, Role: "implement", Round: 0, ParentAttemptId: not null })
        {
            if (initial.Items.Count(item => item.Tag == tag) != 1)
            {
                throw QueuePrelaunchCancellationValidator.Refusal(target, "the exact queue identity is duplicated");
            }
            return await CancelProvedPrelaunchAsync(
                target, initial, output, cancellationToken, beforePrelaunchCommit, beforePrelaunchWrite).ConfigureAwait(false);
        }
        QueueItem? observed = null;
        var cancelledAt = DateTimeOffset.UtcNow;
        await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot =>
        {
            observed = snapshot.Items.FirstOrDefault(item => string.Equals(item.Tag, tag, StringComparison.Ordinal));
            if (observed?.State != QueueItemState.Queued
                || observed.ReadinessMutationClaim is { Length: > 0 }
                || observed.DraftPullRequestCreateMarker is not null
                || QueueScheduler.IsActiveLifecycle(observed))
            {
                return snapshot;
            }

            return snapshot with
            {
                Items = snapshot.Items.Select(item => string.Equals(item.Tag, tag, StringComparison.Ordinal)
                    ? item with { State = QueueItemState.Cancelled, CancelledAt = cancelledAt }
                    : item).ToList(),
            };
        }, cancellationToken).ConfigureAwait(false);

        switch (observed)
        {
            case null:
                throw new CliArgumentException($"Queue item '{tag}' does not exist.", "run 'baton queue list' to see recorded tags.");
            case { ReadinessMutationClaim: { Length: > 0 } }:
                throw new CliArgumentException(
                    $"Queue item '{tag}' has an in-flight pull-request readiness update and cannot be cancelled yet.",
                    "the operation was already claimed; wait for reconciliation to finish, then retry cancellation.");
            case { DraftPullRequestCreateMarker: not null }:
                throw new CliArgumentException(
                    $"Queue item '{tag}' retains a may-have-called draft PR create marker and cannot be cancelled.",
                    "reconcile its exact PR state before changing lifecycle ownership.");
            case { State: QueueItemState.Queued } started when QueueScheduler.IsActiveLifecycle(started):
                throw new CliArgumentException(
                    $"Queue item '{tag}' is an already-started lifecycle waiting at stage '{WorkStages.Token(started.Stage!.Value)}' "
                    + "and cannot be cancelled as a pre-launch request.",
                    "resolve its PR or retire the settled lifecycle with 'baton queue retire <tag> --reason <why>'; "
                    + "a live room instead uses 'baton cancel <room-dir>'.");
            case { State: QueueItemState.Queued }:
                await QueueDecisionLedgerStore.AppendCancellationAsync(
                    cancelledAt, tag, BatonPaths.QueueDecisionLedgerFile, cancellationToken).ConfigureAwait(false);
                output.WriteLine($"Cancelled queued item '{tag}'. Its spec, worktree, and branch were retained.");
                return 0;
            case { State: QueueItemState.Cancelled }:
                // A failed append after the queue write must not make the promised fact unrecoverable.
                // Re-running cancel remains a refusal, but it first backfills the retained item's
                // uniquely keyed cancellation fact.
                await QueueDecisionLedgerStore.AppendCancellationAsync(
                    observed.CancelledAt ?? throw new QueueStoreException(
                        $"Cancelled queue item '{tag}' has no cancellation timestamp."),
                    tag, BatonPaths.QueueDecisionLedgerFile, cancellationToken).ConfigureAwait(false);
                throw new CliArgumentException($"Queue item '{tag}' was already cancelled at {observed.CancelledAt:O}.");
            case { State: QueueItemState.Launched, RoomDirectory: { Length: > 0 } room }:
                throw new CliArgumentException(
                    $"Queue item '{tag}' is already launched into room '{room}'.",
                    $"cancel the launched lane with 'baton cancel {room}'.");
            case { State: QueueItemState.Launched }:
                throw new CliArgumentException(
                    $"Queue item '{tag}' is already launched.",
                    "run 'baton queue list' to find its room, then use 'baton cancel <room-dir>'.");
            default:
                throw new CliArgumentException(
                    $"Queue item '{tag}' is already {observed.State.ToString().ToLowerInvariant()} and cannot be cancelled as a queued request.");
        }
    }

    private static async Task<int> CancelProvedPrelaunchAsync(
        QueueItem source, QueueSnapshot initial, TextWriter output, CancellationToken cancellationToken,
        Func<CancellationToken, Task>? beforeCommit, Action? beforeWrite)
    {
        var sourceRevision = QueueStore.ComputeRevision([source]);
        var at = DateTimeOffset.UtcNow;
        FleetEventLog.FleetEventProofLease lease;
        try
        {
            lease = FleetEventLog.OpenOperational().AcquireRetainedProof(strictSchema: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FleetEventLogReadException)
        {
            throw QueuePrelaunchCancellationValidator.Refusal(source, "retained fleet proof is unavailable: " + ex.Message);
        }
        using (lease)
        {
            var receipt = QueuePrelaunchCancellationValidator.Validate(source, initial, lease.Events, at);
            if (beforeCommit is not null)
            {
                await beforeCommit(cancellationToken).ConfigureAwait(false);
            }
            await QueueStore.MutateAsync(BatonPaths.QueueFile, current =>
            {
                var matches = current.Items.Where(item => item.Tag == source.Tag).ToList();
                if (matches.Count != 1 || QueueStore.ComputeRevision(matches) != sourceRevision
                    || !QueuePrelaunchCancellationReceipt.IsEligibleSource(matches[0]))
                {
                    throw QueuePrelaunchCancellationValidator.Refusal(source, "the exact queue row changed before commit");
                }
                QueuePrelaunchCancellationValidator.ValidatePending(
                    source, current, receipt.Ancestors.Select(ancestor => ancestor.AttemptId).ToHashSet());
                beforeWrite?.Invoke();
                return current with
                {
                    Items = current.Items.Select(item => item.Tag == source.Tag
                        ? item with { State = QueueItemState.Cancelled, CancelledAt = at, PrelaunchCancellation = receipt }
                        : item).ToList(),
                };
            }, cancellationToken).ConfigureAwait(false);
        }
        // Proof streams remain leased through the actual queue replacement, but must be disposed
        // before appending any fact. The existing keyed ledger path owns retry/backfill recovery.
        await QueueDecisionLedgerStore.AppendCancellationAsync(at, source.Tag, BatonPaths.QueueDecisionLedgerFile, cancellationToken)
            .ConfigureAwait(false);
        output.WriteLine($"Cancelled queued item '{source.Tag}'. Its spec, worktree, and branch were retained.");
        return 0;
    }

    private static Task<int> RetireAsync(
        string tag,
        string reason,
        int? mergedPullRequest,
        TextWriter output,
        CancellationToken cancellationToken,
        Func<string, CancellationToken, Task<RepositoryIdentity?>> repositoryResolver,
        IGhCliRunner ghRunner) =>
        mergedPullRequest is { } pullRequest
            ? RetireFromMergedPullRequestAsync(
                tag, reason, pullRequest, output, cancellationToken, repositoryResolver, ghRunner)
            : RetireOperatorAsync(tag, reason, output, cancellationToken);

    internal static async Task<int> RetireOperatorAsync(
        string tag, string reason, TextWriter output, CancellationToken cancellationToken,
        Func<CancellationToken, Task>? beforeCommit = null,
        Action? beforeWrite = null)
    {
        var before = await QueueStore.LoadAsync(BatonPaths.QueueFile, cancellationToken).ConfigureAwait(false);
        var observed = before.Items.FirstOrDefault(item => string.Equals(item.Tag, tag, StringComparison.Ordinal));
        if (observed is null)
        {
            throw new CliArgumentException($"Queue item '{tag}' does not exist.");
        }
        if (observed.Retirement is not null)
        {
            await ReconcileDispositionOutboxAsync(observed, cancellationToken).ConfigureAwait(false);
            if (observed.DispositionOutbox.LastOrDefault() is
                {
                    Decision: QueueDecisionEntry.Retired,
                } committed
                && string.Equals(committed.Reason, $"operator: {reason}", StringComparison.Ordinal))
            {
                await QueueDecisionLedgerStore.AppendDispositionAsync(
                    tag, committed, BatonPaths.QueueDecisionLedgerFile, cancellationToken).ConfigureAwait(false);
                output.WriteLine($"Retired lifecycle item '{tag}' as operator-handled. Its evidence was retained.");
                return 0;
            }
            throw new CliArgumentException($"Queue item '{tag}' is already retired as '{observed.Retirement.Kind}'.");
        }
        if (observed.Stage is null)
        {
            throw new CliArgumentException($"Queue item '{tag}' is ordinary queued work; use 'baton queue cancel {tag}'.");
        }
        if (observed.ReadinessMutationClaim is not null || observed.State == QueueItemState.Launched
            || PendingDraftPullRequestHandoff(observed))
        {
            throw new CliArgumentException($"Queue item '{tag}' is live or has an in-flight readiness mutation and cannot be retired.");
        }
        var readyClosed = observed is { Stage: WorkStage.Ready, State: QueueItemState.Queued, Repository: { Length: > 0 }, PullRequest: > 0 }
            && before.PullRequestObservations?.Any(observation =>
                string.Equals(observation.Repository, observed.Repository, StringComparison.Ordinal)
                && observation.PullRequest == observed.PullRequest
                && observation.State == PullRequestObservationStates.Closed
                && observation.ObservedAt is not null
                && observation.Error is null) == true;
        var terminalRoomProof = await ReadTerminalRoomProofAsync(observed, cancellationToken).ConfigureAwait(false);
        bool hasLegacyProof;
        using (var observedLegacyProof = TryAcquireLegacyRetirementProof(observed))
        {
            hasLegacyProof = observedLegacyProof is not null;
        }
        bool hasReprovenPrelaunchProof;
        using (var observedPrelaunchProof = TryAcquireReprovenPrelaunchCancellationProof(observed, before))
        {
            hasReprovenPrelaunchProof = observedPrelaunchProof is not null;
        }
        // A retained prelaunch receipt is exclusive authority at BOTH reads: a corrupt row cannot
        // bypass failed strict reproof by borrowing a terminal room or closed-PR disposition.
        var observedEligible = observed.PrelaunchCancellation is not null
            ? hasReprovenPrelaunchProof
            : observed.State == QueueItemState.Failed && terminalRoomProof.IsProven
                || hasLegacyProof || readyClosed;
        if (!observedEligible)
        {
            throw new CliArgumentException($"Queue item '{tag}' has insufficient settled failure evidence or trusted closed-PR evidence for operator retirement. It requires a terminal current room, exact refused-attempt/terminal-parent proof, recorded cancellation with terminal proof for every prior launched room, or a freshly reproven initial-prelaunch cancellation receipt.");
        }
        // A restoration can have committed its CAS while its ledger append failed. Replaying every
        // retained predecessor here is a fence: do not commit this successor unless the full ordered
        // outbox is durably acknowledged.
        await ReconcileDispositionOutboxAsync(observed, cancellationToken).ConfigureAwait(false);
        var eligible = false;
        var at = DateTimeOffset.UtcNow;
        var operation = new QueueDispositionOperation(
            Guid.NewGuid().ToString("N"), at, QueueDecisionEntry.Retired, $"operator: {reason}");
        var observedRevision = QueueStore.ComputeRevision([observed]);
        if (beforeCommit is not null)
        {
            await beforeCommit(cancellationToken).ConfigureAwait(false);
        }
        RoomJournalLease? journalLease = null;
        LegacyRetirementProofLease? legacyLease = null;
        LegacyRetirementProofLease? prelaunchLease = null;
        try
        {
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot =>
            {
                var current = snapshot.Items.FirstOrDefault(item => string.Equals(item.Tag, tag, StringComparison.Ordinal));
                var currentReadyClosed = current is
                {
                    Stage: WorkStage.Ready,
                    State: QueueItemState.Queued,
                    Repository: { Length: > 0 },
                    PullRequest: > 0,
                }
                    && snapshot.PullRequestObservations?.Any(observation =>
                        string.Equals(observation.Repository, current.Repository, StringComparison.Ordinal)
                        && observation.PullRequest == current.PullRequest
                        && observation.State == PullRequestObservationStates.Closed
                        && observation.ObservedAt is not null
                        && observation.Error is null) == true;
                if (current is not { Stage: not null, Retirement: null, ReadinessMutationClaim: null }
                    || current.State == QueueItemState.Launched
                    || PendingDraftPullRequestHandoff(current)
                    || !SameRetirementAttempt(observed, current)
                    || (observed.PrelaunchCancellation is not null || current.PrelaunchCancellation is not null
                        ? (prelaunchLease = snapshot.Items.Count(i => string.Equals(i.Tag, tag, StringComparison.Ordinal)) == 1
                            && QueueStore.ComputeRevision([current]) == observedRevision
                            ? TryAcquireReprovenPrelaunchCancellationProof(current, snapshot)
                            : null) is null
                        : (current.State != QueueItemState.Failed
                            || !HasTerminalRoomProofAtMutation(current, terminalRoomProof, out journalLease))
                            && (legacyLease = TryAcquireLegacyRetirementProof(current)) is null
                            && !currentReadyClosed))
                {
                    return snapshot;
                }
                beforeWrite?.Invoke();
                eligible = true;
                return snapshot with
                {
                    Items = snapshot.Items.Select(item => item.Tag == tag
                    ? item with
                    {
                        Retirement = new QueueRetirement(QueueRetirement.Operator, at, reason),
                        OriginatingPullRequestRecoveryClaim =
                            item.OriginatingPullRequestRecoveryClaim == item.AttemptId
                                ? null
                                : item.OriginatingPullRequestRecoveryClaim,
                        OriginatingPullRequestRecoveryProofDigest = null,
                        DispositionOperations = AppendDisposition(item, operation),
                    }
                    : item).ToList()
                };
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // QueueStore writes after its mutation callback. Keep the read-deny-write journal lease
            // through that write, not just through the callback's stamp comparison.
            journalLease?.Dispose();
            legacyLease?.Dispose();
            prelaunchLease?.Dispose();
        }

        if (!eligible)
        {
            throw new CliArgumentException($"Queue item '{tag}' changed while retirement was being recorded; retry after checking its current state.");
        }
        await QueueDecisionLedgerStore.AppendDispositionAsync(tag, operation, BatonPaths.QueueDecisionLedgerFile, cancellationToken)
            .ConfigureAwait(false);
        output.WriteLine($"Retired lifecycle item '{tag}' as operator-handled. Its evidence was retained.");
        return 0;
    }

    private static async Task<int> RetireFromMergedPullRequestAsync(
        string tag,
        string reason,
        int pullRequest,
        TextWriter output,
        CancellationToken cancellationToken,
        Func<string, CancellationToken, Task<RepositoryIdentity?>> repositoryResolver,
        IGhCliRunner ghRunner)
    {
        var before = await QueueStore.LoadAsync(BatonPaths.QueueFile, cancellationToken).ConfigureAwait(false);
        var observed = before.Items.FirstOrDefault(item => string.Equals(item.Tag, tag, StringComparison.Ordinal));
        if (observed is null)
        {
            throw new CliArgumentException($"Queue item '{tag}' does not exist.");
        }

        const string mergedDispositionPrefix = "merged: PR #";
        if (observed.Retirement is not null)
        {
            // A queue CAS may have committed the merged disposition immediately before its ledger
            // append failed. Replay the retained operation before deciding whether this invocation is
            // the same request, so a retry is idempotent and cannot create a second disposition.
            await ReconcileDispositionOutboxAsync(observed, cancellationToken).ConfigureAwait(false);
            if (observed.Retirement.Kind == QueueRetirement.Merged
                && observed.PullRequest == pullRequest
                && string.Equals(observed.Retirement.Reason, reason, StringComparison.Ordinal)
                && observed.DispositionOutbox.LastOrDefault() is
                {
                    Decision: QueueDecisionEntry.Retired,
                    Reason: var committedReason,
                }
                && string.Equals(committedReason, $"{mergedDispositionPrefix}{pullRequest}", StringComparison.Ordinal))
            {
                await QueueDecisionLedgerStore.AppendDispositionAsync(
                    tag,
                    observed.DispositionOutbox.Last(),
                    BatonPaths.QueueDecisionLedgerFile,
                    cancellationToken).ConfigureAwait(false);
                output.WriteLine($"Retired lifecycle item '{tag}' as merged PR #{pullRequest}; its evidence was retained.");
                return 0;
            }

            throw new CliArgumentException($"Queue item '{tag}' is already retired as '{observed.Retirement.Kind}'.");
        }

        if (observed.Stage is null)
        {
            throw new CliArgumentException($"Queue item '{tag}' is ordinary queued work; use 'baton queue cancel {tag}'.");
        }

        if (observed.ReadinessMutationClaim is not null || observed.State == QueueItemState.Launched
            || PendingDraftPullRequestHandoff(observed))
        {
            throw new CliArgumentException($"Queue item '{tag}' is live or has an in-flight readiness mutation and cannot be retired with explicit merged-PR evidence.");
        }

        if (observed.Repository is not { Length: > 0 } || observed.Branch is not { Length: > 0 })
        {
            throw new CliArgumentException(
                $"Queue item '{tag}' has no recorded repository and branch identity for explicit merged-PR retirement; the queue row was not changed.");
        }

        var canonicalRepository = RepositoryIdentity.TryCanonicalize(observed.Repository);
        if (!string.Equals(canonicalRepository, observed.Repository, StringComparison.Ordinal)
            || observed.Repository.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
        {
            throw new CliArgumentException(
                $"Queue item '{tag}' has a malformed recorded repository identity for explicit merged-PR retirement; the queue row was not changed.");
        }

        if (Directory.Exists(observed.Workspace))
        {
            var currentRepository = await repositoryResolver(observed.Workspace, cancellationToken).ConfigureAwait(false);
            if (currentRepository?.RemoteValue is not { Length: > 0 } resolvedRepository)
            {
                throw new CliArgumentException(
                    $"Queue item '{tag}' has no resolvable repository identity for explicit merged-PR retirement; the queue row was not changed.");
            }

            if (!string.Equals(resolvedRepository, observed.Repository, StringComparison.Ordinal))
            {
                throw new CliArgumentException(
                    $"Queue item '{tag}' repository identity '{resolvedRepository}' does not match recorded '{observed.Repository}'; the queue row was not changed.");
            }
        }

        var observedAt = DateTimeOffset.UtcNow;
        var reader = new WorkItemAdvancer(ghRunner, null, repositoryResolver);
        var lookup = await reader.ReadMergedPullRequestObservationAsync(
            observed, pullRequest, observedAt, cancellationToken).ConfigureAwait(false);
        if (lookup.Observation is not { } trustedObservation)
        {
            throw new CliArgumentException(
                $"Queue item '{tag}' could not verify merged PR #{pullRequest}: {lookup.Error ?? "the lookup returned no trusted evidence"}. The queue row was not changed.");
        }

        // A prior disposition must be acknowledged before this successor can be committed, just as
        // ordinary retirement fences the ordered outbox before its own CAS.
        await ReconcileDispositionOutboxAsync(observed, cancellationToken).ConfigureAwait(false);
        var at = DateTimeOffset.UtcNow;
        var operation = new QueueDispositionOperation(
            Guid.NewGuid().ToString("N"), at, QueueDecisionEntry.Retired, $"{mergedDispositionPrefix}{pullRequest}");
        var eligible = false;

        await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot =>
        {
            var current = snapshot.Items.FirstOrDefault(item => string.Equals(item.Tag, tag, StringComparison.Ordinal));
            if (current is not { Stage: not null, Retirement: null, ReadinessMutationClaim: null }
                || current.State == QueueItemState.Launched
                || PendingDraftPullRequestHandoff(current)
                || !SameMergedRetirementAttempt(observed, current))
            {
                return snapshot;
            }

            eligible = true;
            var observations = (snapshot.PullRequestObservations ?? [])
                .Where(observation =>
                    !string.Equals(observation.Repository, trustedObservation.Repository, StringComparison.Ordinal)
                    || observation.PullRequest != trustedObservation.PullRequest)
                .Append(trustedObservation)
                .OrderBy(observation => observation.Repository, StringComparer.Ordinal)
                .ThenBy(observation => observation.PullRequest)
                .ToList();
            return snapshot with
            {
                Items = snapshot.Items.Select(item => string.Equals(item.Tag, tag, StringComparison.Ordinal)
                    ? item with
                    {
                        PullRequest = pullRequest,
                        Retirement = new QueueRetirement(QueueRetirement.Merged, at, reason),
                        OriginatingPullRequestRecoveryClaim =
                            item.OriginatingPullRequestRecoveryClaim == item.AttemptId
                                ? null
                                : item.OriginatingPullRequestRecoveryClaim,
                        OriginatingPullRequestRecoveryProofDigest = null,
                        DispositionOperations = AppendDisposition(item, operation),
                    }
                    : item).ToList(),
                PullRequestObservations = observations,
            };
        }, cancellationToken).ConfigureAwait(false);

        if (!eligible)
        {
            throw new CliArgumentException(
                $"Queue item '{tag}' changed while merged PR #{pullRequest} was being verified; retry after checking its current state.");
        }

        await QueueDecisionLedgerStore.AppendDispositionAsync(
            tag, operation, BatonPaths.QueueDecisionLedgerFile, cancellationToken).ConfigureAwait(false);
        output.WriteLine($"Retired lifecycle item '{tag}' as merged PR #{pullRequest}; its evidence was retained.");
        return 0;
    }

    private static bool SameMergedRetirementAttempt(QueueItem observed, QueueItem current) =>
        SameRetirementAttempt(observed, current)
        && string.Equals(current.Workspace, observed.Workspace, StringComparison.Ordinal)
        && string.Equals(current.Repository, observed.Repository, StringComparison.Ordinal)
        && string.Equals(current.Branch, observed.Branch, StringComparison.Ordinal)
        && current.PullRequest == observed.PullRequest
        && current.AttemptBaseRevision == observed.AttemptBaseRevision
        && SameAttemptEnvelope(observed.AttemptEnvelope, current.AttemptEnvelope)
        && current.LaunchMayHaveBegunAt == observed.LaunchMayHaveBegunAt
        && current.AttemptAdmissionFactDurable == observed.AttemptAdmissionFactDurable
        && current.AttemptStartedFactDurable == observed.AttemptStartedFactDurable
        && current.AttemptRefusedFactDurable == observed.AttemptRefusedFactDurable
        && current.AttemptSettledFactDurable == observed.AttemptSettledFactDurable
        && current.LaunchRecoveryKind == observed.LaunchRecoveryKind
        && current.Halted == observed.Halted
        && current.ReconciliationKind == observed.ReconciliationKind;

    private static bool SameAttemptEnvelope(QueueAttemptEnvelope? observed, QueueAttemptEnvelope? current)
    {
        if (observed is null || current is null)
        {
            return observed is null && current is null;
        }

        return observed.AttemptId == current.AttemptId
            && observed.ParentAttemptId == current.ParentAttemptId
            && string.Equals(observed.WorkId, current.WorkId, StringComparison.Ordinal)
            && observed.Issue == current.Issue
            && observed.PullRequest == current.PullRequest
            && observed.Stage == current.Stage
            && string.Equals(observed.DeclaredRole, current.DeclaredRole, StringComparison.Ordinal)
            && string.Equals(observed.Adapter, current.Adapter, StringComparison.Ordinal)
            && string.Equals(observed.Model, current.Model, StringComparison.Ordinal)
            && string.Equals(observed.Effort, current.Effort, StringComparison.Ordinal)
            && SameAttemptEnvelopeList(observed.EffectiveGrant, current.EffectiveGrant)
            && SameAttemptEnvelopeList(observed.RequestedRequirements, current.RequestedRequirements)
            && SameAttemptEnvelopeList(observed.MissingCapabilities, current.MissingCapabilities)
            && string.Equals(observed.AdmissionDecision, current.AdmissionDecision, StringComparison.Ordinal)
            && string.Equals(observed.RoomDirectory, current.RoomDirectory, StringComparison.Ordinal)
            && string.Equals(observed.RoomId, current.RoomId, StringComparison.Ordinal)
            && string.Equals(observed.AttemptBaseRevision, current.AttemptBaseRevision, StringComparison.Ordinal)
            && observed.FactTimestamp == current.FactTimestamp;
    }

    private static bool SameAttemptEnvelopeList(
        IReadOnlyList<string>? observed,
        IReadOnlyList<string>? current) =>
        observed is null || current is null
            ? observed is null && current is null
            : observed.SequenceEqual(current, StringComparer.Ordinal);

    /// <summary>
    /// Protected invariant: a historical roomless next stage may leave WIP only when its exact
    /// previous attempt settled, and the current attempt was durably refused or the next stage was
    /// durably cancelled before launch. Missing, torn, or changing evidence keeps it active.
    /// LaunchedAt may record the scheduler's pre-room claim, not a worker start; a roomless refusal
    /// may use that stamp only when it equals the exact retained refusal time.
    /// The leases remain open through QueueStore's write, not merely its mutation callback.
    /// </summary>
    internal static bool SameRetirementAttempt(QueueItem observed, QueueItem current) =>
        current.State == observed.State
        && current.Stage == observed.Stage
        && current.RoomDirectory == observed.RoomDirectory
        && current.LaunchedAt == observed.LaunchedAt
        && current.AttemptId == observed.AttemptId
        && current.ParentAttemptId == observed.ParentAttemptId
        && current.CancelledAt == observed.CancelledAt;

    internal static LegacyRetirementProofLease? TryAcquireLegacyRetirementProof(QueueItem item)
    {
        // A versioned prelaunch receipt asserts that this ancestry never started. It must use
        // its strict proof arm; contradictory terminal-parent facts cannot relabel it as legacy.
        // Historical cancelled rows without that receipt retain their existing room-proof path.
        if (item.PrelaunchCancellation is not null)
        {
            return null;
        }
        var refused = item is
        {
            State: QueueItemState.Failed, RoomDirectory: null,
            AttemptId: not null, ParentAttemptId: not null,
        };
        var cancelled = item is
        {
            State: QueueItemState.Cancelled, RoomDirectory: null,
            AttemptId: null, ParentAttemptId: not null, CancelledAt: not null,
        };
        if ((!refused && !cancelled) || (cancelled && item.LaunchedAt is not null))
        {
            return null;
        }

        FleetEventLog.FleetEventProofLease? events = null;
        FileStream? decisionStream = null;
        var sentinels = new List<FileStream>();
        try
        {
            events = FleetEventLog.OpenOperational().AcquireRetainedProof();
            var workEvents = events.Events.Where(e =>
                string.Equals(e.WorkId?.Value, item.Tag, StringComparison.Ordinal)).ToList();
            var parent = item.ParentAttemptId!.Value;
            var parentEvents = events.Events.Where(e => e.AttemptId == parent
                && e.Kind is FleetEventKind.AttemptStarted or FleetEventKind.AttemptSettled).ToList();
            if (parentEvents.Any(e => e.WorkId?.Value != item.Tag))
            {
                return null;
            }
            var started = parentEvents.Where(e => e.AttemptId == parent
                && e.Kind == FleetEventKind.AttemptStarted).ToList();
            var settled = parentEvents.Where(e => e.AttemptId == parent
                && e.Kind == FleetEventKind.AttemptSettled).ToList();
            if (started.Count != 1 || settled.Count != 1
                || started[0].RoomId is not { Value.Length: > 0 } room
                || settled[0].RoomId != started[0].RoomId
                || !IsTerminalOutcome(settled[0].Outcome)
                || settled[0].At < started[0].At)
            {
                return null;
            }
            var parentRoom = BatonPaths.RecordKey(room.Value);
            if (!BatonPaths.RecordKeyComparer.Equals(parentRoom, room.Value))
            {
                return null;
            }
            var parentSentinel = new FileStream(Path.Combine(parentRoom, TerminalSentinelWriter.TerminalSentinelFileName),
                FileMode.Open, FileAccess.Read, FileShare.Read);
            sentinels.Add(parentSentinel);
            var status = JsonSerializer.Deserialize<WorkflowStatusView>(parentSentinel);
            if (status is null || !IsTerminalOutcome(status.State)
                || !string.Equals(status.State, settled[0].Outcome, StringComparison.Ordinal))
            {
                return null;
            }

            if (refused)
            {
                var current = item.AttemptId!.Value;
                var currentEvents = events.Events.Where(e => e.AttemptId == current).ToList();
                var admission = currentEvents.Where(e => e.Kind == FleetEventKind.AdmissionDecided).ToList();
                var refusal = currentEvents.Where(e => e.Kind == FleetEventKind.AttemptRefused
                    && e.ParentAttemptId == parent && e.RoomId is null
                    && e.WorkId?.Value == item.Tag).ToList();
                // Permission admission is not a launch. Every other same-ID event must be the
                // sole exact refusal; conflicting metadata or attempt history fails closed.
                if (refusal.Count != 1 || admission.Count > 1
                    || currentEvents.Count != refusal.Count + admission.Count
                    || item.LaunchedAt is { } claimAt && claimAt != refusal[0].At
                    || refusal[0].At < settled[0].At
                    || admission.Any(e => e.WorkId?.Value != item.Tag
                        || e.ParentAttemptId != parent || e.RoomId is not null
                        || e.At < settled[0].At || e.At > refusal[0].At))
                {
                    return null;
                }
            }
            else
            {
                if (item.CancelledAt < settled[0].At
                    || workEvents.Any(e => e.At > item.CancelledAt
                        && e.Kind == FleetEventKind.AttemptStarted))
                {
                    return null;
                }
                decisionStream = new FileStream(BatonPaths.QueueDecisionLedgerFile,
                    FileMode.Open, FileAccess.Read, FileShare.Read);
                var decisions = ReadStrictDecisionProof(decisionStream);
                if (!decisions.Any(d => d.Tag == item.Tag
                    && d.Decision == QueueDecisionEntry.Cancelled && d.At == item.CancelledAt)
                    || decisions.Any(d => d.Tag == item.Tag && d.At > item.CancelledAt
                        && d.Decision == QueueDecisionEntry.Launched))
                {
                    return null;
                }
                foreach (var launch in decisions.Where(d => d.Tag == item.Tag
                    && d.Decision == QueueDecisionEntry.Launched))
                {
                    if (launch.Room is not { Length: > 0 } priorRoom
                        || launch.At > item.CancelledAt)
                    {
                        return null;
                    }
                    var path = BatonPaths.RecordKey(priorRoom);
                    if (!BatonPaths.RecordKeyComparer.Equals(path, priorRoom))
                    {
                        return null;
                    }
                    var priorSentinel = new FileStream(
                        Path.Combine(path, TerminalSentinelWriter.TerminalSentinelFileName),
                        FileMode.Open, FileAccess.Read, FileShare.Read);
                    sentinels.Add(priorSentinel);
                    if (!IsTerminalOutcome(JsonSerializer.Deserialize<WorkflowStatusView>(priorSentinel)?.State))
                    {
                        return null;
                    }
                }
            }

            var proof = new LegacyRetirementProofLease(events, decisionStream, sentinels);
            events = null;
            decisionStream = null;
            sentinels = [];
            return proof;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or ArgumentException or BatonFlowException)
        {
            throw new LegacyRetirementProofReadException(item.Tag, ex);
        }
        finally
        {
            foreach (var sentinel in sentinels) sentinel.Dispose();
            decisionStream?.Dispose();
            events?.Dispose();
        }
    }

    private static readonly JsonSerializerOptions StrictDecisionProofJson = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private static IReadOnlyList<QueueDecisionEntry> ReadStrictDecisionProof(FileStream stream, bool strictSchema = false)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var text = reader.ReadToEnd();
        if (!text.EndsWith('\n'))
        {
            throw new IOException("The queue-decision proof has an incomplete tail.");
        }
        var rows = new List<QueueDecisionEntry>();
        foreach (var line in text.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (strictSchema)
            {
                using var document = JsonDocument.Parse(line);
                if (FleetEventLog.HasDuplicateMembers(document.RootElement))
                {
                    throw new JsonException("A queue-decision proof row has duplicate fields.");
                }
                // Protected invariant: omitted producer fields must not deserialize to plausible
                // default values and disappear in the later same-tag filter (for example {}).
                var element = document.RootElement;
                if (element.ValueKind != JsonValueKind.Object
                    || !element.TryGetProperty("at", out _)
                    || !element.TryGetProperty("decision", out _)
                    || !element.TryGetProperty("liveWeight", out _)
                    || !element.TryGetProperty("floorGb", out _))
                {
                    throw new JsonException("A queue-decision proof row omits required producer fields.");
                }
                var row = element.Deserialize<QueueDecisionEntry>(StrictDecisionProofJson)
                    ?? throw new JsonException("Empty queue-decision proof row.");
                if (row.At == default || row.Decision is not (QueueDecisionEntry.Launched
                        or QueueDecisionEntry.Waited or QueueDecisionEntry.Failed or QueueDecisionEntry.Cancelled
                        or QueueDecisionEntry.Retired or QueueDecisionEntry.Restored or QueueDecisionEntry.Advanced)
                    || !double.IsFinite(row.LiveWeight) || row.LiveWeight < 0
                    || !double.IsFinite(row.FloorGb) || row.FloorGb < 0
                    || row.FreeGb is { } freeGb && (!double.IsFinite(freeGb) || freeGb < 0)
                    || !Enum.IsDefined(row.SelectionSource)
                    || row.Tag is not null && string.IsNullOrWhiteSpace(row.Tag)
                    || row.Tag is null && row.Decision is (QueueDecisionEntry.Launched
                        or QueueDecisionEntry.Cancelled or QueueDecisionEntry.Retired
                        or QueueDecisionEntry.Restored or QueueDecisionEntry.Advanced))
                {
                    throw new JsonException("A queue-decision proof row has invalid producer values.");
                }
                rows.Add(row);
                continue;
            }
            rows.Add(JsonSerializer.Deserialize<QueueDecisionEntry>(line)
                ?? throw new JsonException("Empty queue-decision proof row."));
        }
        return rows;
    }

    /// <summary>
    /// Freshly reproves a round-zero initial-implementation <see cref="QueuePrelaunchCancellationReceipt"/>
    /// against retained fleet and decision sources. <see cref="QueuePrelaunchCancellationReceipt.IsValidFor"/>
    /// alone checks the receipt's structure and its binding to the exact row; it never reads fleet history,
    /// so a legitimately produced receipt could otherwise outlive the source evidence it once proved.
    /// </summary>
    internal static LegacyRetirementProofLease? TryAcquireReprovenPrelaunchCancellationProof(
        QueueItem item, QueueSnapshot snapshot)
    {
        if (item.PrelaunchCancellation is not { } receipt || !QueuePrelaunchCancellationReceipt.IsValidFor(item))
        {
            return null;
        }

        FleetEventLog.FleetEventProofLease? events = null;
        FileStream? decisionStream = null;
        try
        {
            events = FleetEventLog.OpenOperational().AcquireRetainedProof(strictSchema: true);
            if (!QueuePrelaunchCancellationValidator.ReproveAncestryForRetirement(item, events.Events, receipt))
            {
                return null;
            }

            decisionStream = new FileStream(
                BatonPaths.QueueDecisionLedgerFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decisions = ReadStrictDecisionProof(decisionStream, strictSchema: true);
            var tagDecisions = decisions.Where(d => d.Tag == item.Tag).ToList();
            var cancellations = tagDecisions.Where(d => d.Decision == QueueDecisionEntry.Cancelled).ToList();
            if (cancellations.Count != 1 || tagDecisions.Any(d => d.Decision != QueueDecisionEntry.Cancelled
                    && d.Decision != QueueDecisionEntry.Waited)
                || cancellations[0] is not
                {
                    At: var cancelledAt,
                    Reason: "operator cancelled before launch",
                    LiveWeight: 0, FreeGb: null, FloorGb: 0,
                    Tier: null, Adapter: null, Model: null, Effort: null,
                    TierOverride: false, OverrideReason: null, Room: null,
                    SelectionSource: QueueSelectionSource.StageDefault,
                    Admission: null, ActiveLifecycles: null, PrePullRequestLifecycles: null,
                    LiveReviews: null, PriorityBand: null, PassedNewWorkHead: null,
                    OldestOccupyingLifecycle: null, ConsumingLifecycles: null,
                    NewWorkHeadCap: null, OperationKey: null,
                }
                || cancelledAt != item.CancelledAt)
            {
                return null;
            }

            try
            {
                QueuePrelaunchCancellationValidator.ValidatePending(
                    item, snapshot, receipt.Ancestors.Select(ancestor => ancestor.AttemptId).ToHashSet());
            }
            catch (CliArgumentException)
            {
                return null;
            }

            var proof = new LegacyRetirementProofLease(events, decisionStream, []);
            events = null;
            decisionStream = null;
            return proof;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or ArgumentException or BatonFlowException)
        {
            throw new LegacyRetirementProofReadException(item.Tag, ex);
        }
        finally
        {
            decisionStream?.Dispose();
            events?.Dispose();
        }
    }

    private static bool IsTerminalOutcome(string? outcome) => outcome is
        WorkflowOutcome.Succeeded or WorkflowOutcome.SucceededWithLateFailure or WorkflowOutcome.FinishedDuringTeardown or WorkflowOutcome.Failed
        or WorkflowOutcome.Cancelled or WorkflowOutcome.Indeterminate;

    internal sealed class LegacyRetirementProofLease(
        FleetEventLog.FleetEventProofLease events, FileStream? decisions, IReadOnlyList<FileStream> sentinels) : IDisposable
    {
        public void Dispose()
        {
            foreach (var sentinel in sentinels) sentinel.Dispose();
            decisions?.Dispose();
            events.Dispose();
        }
    }

    internal sealed class LegacyRetirementProofReadException(string tag, Exception cause)
        : BatonFlowException($"Queue item '{tag}' retirement proof read failed: {cause.Message}. The row remains active; repair the retained source before retrying.", cause);

    private static async Task<int> RestoreAsync(string tag, string reason, TextWriter output, CancellationToken cancellationToken)
    {
        var before = await QueueStore.LoadAsync(BatonPaths.QueueFile, cancellationToken).ConfigureAwait(false);
        var observed = before.Items.FirstOrDefault(item => string.Equals(item.Tag, tag, StringComparison.Ordinal));
        if (observed is null)
        {
            throw new CliArgumentException($"Queue item '{tag}' does not exist.");
        }
        if (observed.Retirement?.Kind == QueueRetirement.Merged)
        {
            throw new CliArgumentException($"Queue item '{tag}' was retired after merge and cannot be restored.");
        }
        if (observed.Retirement is null)
        {
            await ReconcileDispositionOutboxAsync(observed, cancellationToken).ConfigureAwait(false);
            if (observed.DispositionOutbox.LastOrDefault() is
                {
                    Decision: QueueDecisionEntry.Restored,
                } committed && string.Equals(committed.Reason, $"operator: {reason}", StringComparison.Ordinal))
            {
                output.WriteLine($"Restored lifecycle item '{tag}' to active attention; its launch state was unchanged.");
                return 0;
            }
        }
        if (observed.Retirement?.Kind != QueueRetirement.Operator)
        {
            throw new CliArgumentException($"Queue item '{tag}' is not operator-retired.");
        }
        // Do not permit a successor CAS until every predecessor can be durably replayed. A keyed
        // append is idempotent, so this is safe after a successful prior acknowledgement too.
        await ReconcileDispositionOutboxAsync(observed, cancellationToken).ConfigureAwait(false);
        var at = DateTimeOffset.UtcNow;
        var operation = new QueueDispositionOperation(
            Guid.NewGuid().ToString("N"), at, QueueDecisionEntry.Restored, $"operator: {reason}");
        var restored = false;
        await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot =>
        {
            var current = snapshot.Items.FirstOrDefault(item => string.Equals(item.Tag, tag, StringComparison.Ordinal));
            if (current?.Retirement?.Kind != QueueRetirement.Operator)
            {
                return snapshot;
            }
            restored = true;
            return snapshot with
            {
                Items = snapshot.Items.Select(item => item.Tag == tag
                ? item with { Retirement = null, DispositionOperations = AppendDisposition(item, operation) } : item).ToList()
            };
        }, cancellationToken).ConfigureAwait(false);
        if (!restored)
        {
            throw new CliArgumentException($"Queue item '{tag}' changed while restoration was being recorded; retry after checking its current state.");
        }
        await QueueDecisionLedgerStore.AppendDispositionAsync(tag, operation, BatonPaths.QueueDecisionLedgerFile, cancellationToken)
            .ConfigureAwait(false);
        output.WriteLine($"Restored lifecycle item '{tag}' to active attention; its launch state was unchanged.");
        return 0;
    }

    internal sealed record TerminalRoomProof(bool FromSentinel, RoomJournalStamp? Journal)
    {
        public bool IsProven => FromSentinel || Journal is not null;
    }

    internal sealed record RoomJournalStamp(long LogLength, DateTime LogModifiedUtc,
        long SnapshotLength, DateTime SnapshotModifiedUtc);

    internal sealed class RoomJournalLease(FileStream log, FileStream snapshot) : IDisposable
    {
        public void Dispose()
        {
            snapshot.Dispose();
            log.Dispose();
        }
    }

    private static RoomJournalStamp? ReadRoomJournalStamp(string room)
    {
        try
        {
            var log = new FileInfo(Path.Combine(room, BatonPaths.FlowLogFileName));
            var snapshot = new FileInfo(Path.Combine(room, BatonPaths.SnapshotFileName));
            return log.Exists && snapshot.Exists
                ? new RoomJournalStamp(log.Length, log.LastWriteTimeUtc, snapshot.Length, snapshot.LastWriteTimeUtc)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static async Task<TerminalRoomProof> ReadTerminalRoomProofAsync(
        QueueItem item, CancellationToken cancellationToken)
    {
        if (item.RoomDirectory is not { Length: > 0 } room)
        {
            // A roomless timeout can mark the row failed before a late launcher persists its room.
            // No directory is absence of proof, never proof that a live room cannot exist.
            return new TerminalRoomProof(false, null);
        }

        if (await TerminalSentinelWriter.TryReadAsync(room, cancellationToken).ConfigureAwait(false) is not null)
        {
            return new TerminalRoomProof(true, null);
        }

        // A dead-pump probe deliberately writes only a journal fact, never terminal.json. Use the
        // same read-only terminal projector as baton status, and accept it only if no journal or
        // snapshot change raced that read. Missing/malformed evidence cannot widen this guard.
        var before = ReadRoomJournalStamp(room);
        if (before is null)
        {
            return new TerminalRoomProof(false, null);
        }
        try
        {
            var entries = await new FlowEventLogReader(Path.Combine(room, BatonPaths.FlowLogFileName))
                .ReadAllEntriesWithTimestampsAsync(cancellationToken).ConfigureAwait(false);
            if (!entries.OfType<LogEntry.FlowLogEntry>()
                .Any(entry => DeadPumpProbe.IsTerminalDiagnostic(entry.Event)))
            {
                return new TerminalRoomProof(false, null);
            }
            var projected = await WorkflowTerminalProbe.ProbeAsync(room, cancellationToken).ConfigureAwait(false);
            var after = ReadRoomJournalStamp(room);
            return projected.IsTerminal && before == after
                ? new TerminalRoomProof(false, after)
                : new TerminalRoomProof(false, null);
        }
        catch (Exception ex) when (ex is BatonFlowException or IOException or UnauthorizedAccessException or JsonException)
        {
            return new TerminalRoomProof(false, null);
        }
    }

    /// <summary>
    /// Re-proves the terminal account while the queue mutex is held. Journal proof returns a
    /// read-deny-write lease that the caller retains through QueueStore's later write. This deliberately
    /// uses synchronous file I/O: an await would strand the thread-affine queue mutex.
    /// </summary>
    internal static bool HasTerminalRoomProofAtMutation(
        QueueItem item, TerminalRoomProof proof, out RoomJournalLease? lease)
    {
        lease = null;
        if (item.RoomDirectory is not { Length: > 0 } room)
        {
            return false;
        }

        // Protected invariant: a late journal append or snapshot rebind must invalidate the
        // dead-pump proof, and no append/rebind may interleave before the queue CAS writes retirement.
        if (proof.Journal is { } journal)
        {
            try
            {
                var log = new FileStream(Path.Combine(room, BatonPaths.FlowLogFileName),
                    FileMode.Open, FileAccess.Read, FileShare.Read);
                try
                {
                    var snapshot = new FileStream(Path.Combine(room, BatonPaths.SnapshotFileName),
                        FileMode.Open, FileAccess.Read, FileShare.Read);
                    lease = new RoomJournalLease(log, snapshot);
                }
                catch
                {
                    log.Dispose();
                    throw;
                }
                if (ReadRoomJournalStamp(room) == journal)
                {
                    return true;
                }
                lease.Dispose();
                lease = null;
                return false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        var path = Path.Combine(room, TerminalSentinelWriter.TerminalSentinelFileName);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<WorkflowStatusView>(stream) is not null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<int> ImportAsync(QueueOptions options, TextWriter output, CancellationToken cancellationToken)
    {
        var path = options.ImportFilePath!;
        if (!File.Exists(path))
        {
            throw new CliArgumentException($"File to import '{path}' does not exist. {QueueOptionsParser.Usage}");
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var imported = QueueImport.Parse(json, BatonPaths.QueueSpecFile, DateTimeOffset.UtcNow)
            .Select(item => item with
            {
                Skills = item.Skills is null ? null : DispatchOptionsParser.NormalizeSkills(item.Skills),
            })
            .ToList();

        // The spec each imported item points at is baton's own path, which the runner never wrote to.
        // Said out loud per item rather than assumed: a QUEUED import with no spec on disk would fail
        // at launch time with a stack trace instead of here with a sentence.
        var missingSpecs = imported
            .Where(i => i.State == QueueItemState.Queued && !File.Exists(i.SpecFile))
            .Select(i => i.Tag)
            .ToList();

        await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot =>
        {
            var importedTags = imported.Select(i => i.Tag).ToHashSet(StringComparer.Ordinal);
            var taskConflict = snapshot.Items.FirstOrDefault(i => i.OwnedTask is not null
                && importedTags.Contains(i.Tag));
            if (taskConflict is not null)
            {
                throw new CliArgumentException(
                    $"Item '{taskConflict.Tag}' belongs to retained task '{taskConflict.OwnedTask!.Id}'. Import cannot replace task evidence.");
            }
            var lifecycleConflict = snapshot.Items.FirstOrDefault(i => i.Stage is not null
                && importedTags.Contains(i.Tag));
            if (lifecycleConflict is not null)
            {
                throw new CliArgumentException(
                    $"Item '{lifecycleConflict.Tag}' retains an issue lifecycle"
                    + (lifecycleConflict.IssuePreparation?.State == TaskPreparationState.Preparing
                        ? " still preparing" : "")
                    + "; scratchpad import cannot replace its reservation or stage evidence.");
            }
            var issueConflict = imported.FirstOrDefault(incoming => incoming.Stage is not null
                && incoming.Issue is not null && incoming.Repository is not null
                && snapshot.Items.Any(current => current.Stage is not null
                    && current.Issue == incoming.Issue
                    && string.Equals(current.Repository, incoming.Repository, StringComparison.OrdinalIgnoreCase)));
            if (issueConflict is not null)
            {
                var owner = snapshot.Items.First(current => current.Stage is not null
                    && current.Issue == issueConflict.Issue
                    && string.Equals(current.Repository, issueConflict.Repository, StringComparison.OrdinalIgnoreCase));
                throw new CliArgumentException(
                    $"Issue {issueConflict.Issue} in '{issueConflict.Repository}' is already owned by '{owner.Tag}'"
                    + (owner.OwnedTask is null ? "." : $" (task {owner.OwnedTask.Id})."));
            }
            var duplicateImport = imported.Where(i => i.Stage is not null && i.Issue is not null && i.Repository is not null)
                .GroupBy(i => $"{i.Repository!.ToLowerInvariant()}#{i.Issue}", StringComparer.Ordinal)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicateImport is not null)
            {
                throw new CliArgumentException($"Import contains duplicate issue lifecycle '{duplicateImport.Key}'.");
            }
            var claimed = snapshot.Items.FirstOrDefault(
                item => (item.ReadinessMutationClaim is { Length: > 0 }
                    || item.DraftPullRequestCreateMarker is not null
                    || item.ReplacementReviewAction is not null) && importedTags.Contains(item.Tag));
            if (claimed is not null)
            {
                throw new CliArgumentException(
                    $"Item '{claimed.Tag}' retains a readiness, draft PR, or replacement-review action. "
                    + "Importing it would erase durable external-action evidence.",
                    "remove that tag from the import and reconcile the existing queue row.");
            }

            var cancelled = snapshot.Items.FirstOrDefault(
                item => item.State == QueueItemState.Cancelled && importedTags.Contains(item.Tag));
            if (cancelled is not null)
            {
                throw new CliArgumentException(
                    $"Item '{cancelled.Tag}' was cancelled before launch. Importing it would overwrite its retained cancellation record.",
                    "remove that tag from the import, or use a different tag for new work.");
            }

            var retired = snapshot.Items.FirstOrDefault(
                item => item.Retirement is not null && importedTags.Contains(item.Tag));
            if (retired is not null)
            {
                throw new CliArgumentException(
                    $"Item '{retired.Tag}' is retired as '{retired.Retirement!.Kind}'. Importing it would overwrite retained disposition evidence.",
                    "remove that tag from the import, or use a different tag for new work.");
            }

            var kept = snapshot.Items.Where(i => !importedTags.Contains(i.Tag)).ToList();
            kept.AddRange(imported);
            return snapshot with { Items = kept };
        }, cancellationToken).ConfigureAwait(false);

        output.WriteLine($"Imported {imported.Count} item(s) from '{path}'.");
        foreach (var missing in missingSpecs)
        {
            output.WriteLine(
                $"  '{missing}' is queued but has no spec at {BatonPaths.QueueSpecFile(missing)} — the runner kept "
                + "its briefs elsewhere. Copy it there, or re-add the item with 'baton queue add … --spec <file>'; "
                + "it will fail at launch otherwise.");
        }

        return 0;
    }

    private static bool PendingDraftPullRequestHandoff(QueueItem item) =>
        item.DraftPullRequestCreateMarker is not null
        && item.Stage == WorkStage.Implement
        && item.ReconciliationKind == QueueReconciliationKind.AwaitingVerifiedPullRequest;

    private static IReadOnlyList<QueueDispositionOperation> AppendDisposition(
        QueueItem item, QueueDispositionOperation operation) => [.. item.DispositionOutbox, operation];

    private static async Task ReconcileDispositionOutboxAsync(QueueItem item, CancellationToken cancellationToken)
    {
        foreach (var operation in item.DispositionOutbox)
        {
            await QueueDecisionLedgerStore.AppendDispositionAsync(
                item.Tag, operation, BatonPaths.QueueDecisionLedgerFile, cancellationToken).ConfigureAwait(false);
        }
    }

    private static (string? Adapter, QueueTierResolution Tier, bool AdapterFromModel,
        IReadOnlyList<QueueStageSelection>? StageSelections) ResolveTierForAdd(
        QueueOptions options, QueueSettings settings)
    {
        try
        {
            _ = WorkerRoleCatalog.For(options.Role!);
        }
        catch (KeyNotFoundException ex)
        {
            throw new CliArgumentException(ex.Message);
        }

        var (adapter, adapters, adapterFromModel) = InferAdapterForModel(
            options.ScopeClass, options.Adapter, options.Model);
        var stageSelections = options.Lifecycle
            ? NormalizeLifecycleStageSelections(options.StageSelections, options.ScopeClass)
            : options.StageSelections;
        var tier = QueueTierTable.Resolve(
            new QueueItem
            {
                Tag = options.Tag!,
                Role = options.Role!,
                Workspace = "",
                SpecFile = "",
                ScopeClass = options.ScopeClass?.ToLowerInvariant(),
                Adapter = adapter,
                Model = options.Model,
                Effort = options.Effort,
                Reason = options.Reason,
                StageSelections = stageSelections,
                LifecyclePin = options.LifecyclePin,
            },
            settings,
            WorkerRoleCatalog.QueueTierFor,
            WorkerRoleCatalog.QueueTierForRole);

        if (options.Lifecycle)
        {
            var lifecycleItem = new QueueItem
            {
                Tag = options.Tag!,
                Role = options.Role!,
                Workspace = "",
                SpecFile = "",
                ScopeClass = options.ScopeClass?.ToLowerInvariant(),
                Adapter = adapter,
                Model = options.Model,
                Effort = options.Effort,
                Reason = options.Reason,
                StageSelections = stageSelections,
                LifecyclePin = options.LifecyclePin,
                LifecycleReason = options.LifecycleReason,
                Stage = WorkStage.Implement,
            };

            ValidateLifecycleSelections(lifecycleItem, settings);
            tier = QueueTierTable.ResolveForStage(
                lifecycleItem, WorkStage.Implement, settings,
                WorkerRoleCatalog.QueueTierFor, WorkerRoleCatalog.QueueTierForRole);
        }

        if (adapters.Count > 0
            && (tier.Adapter is null || !adapters.Contains(tier.Adapter, StringComparer.OrdinalIgnoreCase)))
        {
            var actualAdapter = tier.Adapter ?? "unconfigured";
            throw new CliArgumentException(
                $"--model '{options.Model}' is known by {string.Join(", ", adapters)}, but the resolved {actualAdapter} adapter cannot use it.");
        }

        return (adapter, tier, adapterFromModel, stageSelections);
    }

    /// <summary>
    /// Applies ordinary add-time model routing to each explicit stage selection. A model-only
    /// selection must retain the adapter inferred from its unique model candidate, because the
    /// stage resolver otherwise has no item-level adapter to launch with after a later advance.
    /// </summary>
    internal static IReadOnlyList<QueueStageSelection>? NormalizeLifecycleStageSelections(
        IReadOnlyList<QueueStageSelection>? selections, string? scopeClass)
    {
        if (selections is null)
        {
            return null;
        }

        return selections.Select(selection =>
        {
            var (adapter, _, _) = InferAdapterForModel(scopeClass, selection.Adapter, selection.Model);
            return selection with { Adapter = adapter };
        }).ToList();
    }

    private static (string? Adapter, IReadOnlyList<string> Candidates, bool AdapterFromModel) InferAdapterForModel(
        string? scopeClass, string? adapter, string? model)
    {
        if (adapter is not null && model is not null)
        {
            ValidateAdapterModel(adapter, model);
        }

        var candidates = model is null
            ? Array.Empty<string>() : WorkerModelCatalog.AdaptersFor(model);
        if (model is not null && adapter is null && candidates.Count == 0)
        {
            throw new CliArgumentException(
                $"--model '{model}' has no recorded adapter candidate; specify --adapter to use its model validation.");
        }

        if (adapter is null && candidates.Count > 1)
        {
            throw new CliArgumentException(
                $"--model '{model}' has multiple candidate adapters: {string.Join(", ", candidates)}; specify --adapter.");
        }

        var adapterFromModel = scopeClass is null && adapter is null && candidates.Count == 1;
        return (adapterFromModel ? candidates[0] : adapter, candidates, adapterFromModel);
    }

    private static void ValidateAdapterModel(string adapter, string model)
    {
        var worker = WorkerAdapterRegistry.Default
            .FirstOrDefault(pair => string.Equals(pair.Key, adapter, StringComparison.OrdinalIgnoreCase)).Value;
        if (worker is null)
        {
            throw new CliArgumentException($"Unknown adapter '{adapter}'.");
        }

        try
        {
            worker.ValidateRequestedModel(model);
        }
        catch (BatonFlowException ex)
        {
            throw WorkerInvocationModelPolicy.Refusal(ex.Message, adapter);
        }
    }

    /// <summary>
    /// Every explicitly selected lifecycle stage is validated before worktree provisioning, so an
    /// invalid later review/fix choice cannot survive until it spends a worker launch. The resolver
    /// remains the source of the actual adapter for a partial selection.
    /// </summary>
    private static void ValidateLifecycleSelections(QueueItem item, QueueSettings settings)
    {
        var missingReasons = new List<WorkStage>();
        foreach (var stage in new[]
                 {
                     WorkStage.Implement, WorkStage.Review, WorkStage.Fix, WorkStage.ReReview, WorkStage.Continue,
                 })
        {
            var (_, source) = QueueTierTable.SelectionForStage(item, stage);
            if (source == QueueSelectionSource.StageDefault)
            {
                continue;
            }

            var tier = QueueTierTable.ResolveForStage(
                item, stage, settings, WorkerRoleCatalog.QueueTierFor, WorkerRoleCatalog.QueueTierForRole);
            if (tier.IsOverride && string.IsNullOrWhiteSpace(tier.OverrideReason))
            {
                missingReasons.Add(stage);
            }

            if (tier.Adapter is { } adapter)
            {
                try
                {
                    WorkerInvocationValidation.Validate(
                        adapter, tier.Model, null, tier.Effort, WorkerAdapterRegistry.Default);
                }
                catch (CliArgumentException ex)
                {
                    var message = $"The {WorkStages.Token(stage)} selection is invalid: {ex.Message}";
                    throw ex.TryInvocation is { } tryInvocation
                        ? new CliArgumentException(message, tryInvocation)
                        : new CliArgumentException(message);
                }
            }
        }

        if (missingReasons.Count > 0)
        {
            var stages = string.Join(", ", missingReasons.Select(WorkStages.Token));
            throw new CliArgumentException(
                $"Explicit lifecycle routing for stage(s) {stages} needs a rationale. "
                + "Pass '--lifecycle-reason <why>' once, or pass '--reason <why>' while naming each stage with '--stage'.");
        }
    }

    private static string DescribeTier(QueueTierResolution tier, bool adapterFromModel)
    {
        var parts = new List<string>();
        if (tier.TierKey is { Length: > 0 } key)
        {
            parts.Add(key);
        }

        parts.Add(tier.Adapter is null
            ? "unconfigured adapter"
            : adapterFromModel ? $"{tier.Adapter} (from --model {tier.Model})" : tier.Adapter);
        parts.Add(tier.Model ?? "role default model");
        parts.Add(tier.Effort ?? "role default effort");
        return string.Join(" / ", parts) + (tier.IsOverride ? " (overridden)" : string.Empty);
    }

    /// <summary>
    /// "Is it still waiting, and on what" — the question spec/baton.md §13 sends the reader here to
    /// ask, and the reason the decision ledger is allowed to collapse a repeated verdict to one row
    /// instead of writing a per-tick heartbeat.
    /// </summary>
    /// <remarks>
    /// Read off the ledger's LAST row rather than recomputed: this verb must not take a second memory
    /// reading or re-tally the live rooms, because a number that disagreed with the scheduler's own
    /// would be worse than no number. Printed only when that last row is a wait — after a launch or a
    /// failure the queue is not waiting on anything, and the row's own <c>at</c> is when the wait
    /// began, since an unchanged verdict is not re-appended. It is a QUEUE-level line, never folded
    /// into an item's: a row that names a tag names the CANDIDATE the scheduler looked at, which is
    /// not the same claim as "this item is waiting", and some rows name no tag at all.
    /// </remarks>
    private static async Task PrintWaitAsync(TextWriter output, CancellationToken cancellationToken)
    {
        var ledger = await QueueDecisionLedgerStore
            .ReadAllAsync(BatonPaths.QueueDecisionLedgerFile, cancellationToken).ConfigureAwait(false);
        if (ledger.Count == 0 || ledger[^1] is not { Decision: QueueDecisionEntry.Waited } wait)
        {
            return;
        }

        // Two of the six tokens say nothing this listing does not already say better, so they are
        // suppressed rather than printed: 'no-items' would sit above "Queue is empty." announcing that
        // the queue is waiting on being empty (and an idle fleet's steady state is exactly that row),
        // and 'hold' would repeat the HELD line immediately above it. Compared against
        // QueueWaitReasons.Token rather than a literal, so renaming a token cannot silently switch
        // either line back on.
        if (wait.Reason == QueueWaitReasons.Token(QueueWaitReason.NoItems)
            || wait.Reason == QueueWaitReasons.Token(QueueWaitReason.Hold))
        {
            return;
        }

        var counters = wait.FreeGb is { } free
            ? $"live weight {Number(wait.LiveWeight)}, free {Number(free)} GiB against a {Number(wait.FloorGb)} GiB floor"
            : $"live weight {Number(wait.LiveWeight)}, free memory unmeasured";
        var candidate = wait.Tag is { Length: > 0 } tag ? $", candidate '{tag}'" : string.Empty;
        output.WriteLine($"Waiting on {wait.Reason} since {wait.At:u} ({counters}{candidate}).");
    }

    /// <summary>Renders a count the same way everywhere. Invariant culture on purpose: a queue whose
    /// numbers change shape with the host's locale is one nobody can grep two machines of.</summary>
    internal static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
