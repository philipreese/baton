using System.Globalization;
using Baton.Accounting;
using Baton.Queue;
using Baton.Status;
using Baton.Vendors;

namespace Baton.Cli;

/// <summary>
/// <c>baton queue add|list|hold|resume|cancel|import</c> (#1934 slice 1): the operator's control surface over
/// the dispatch queue the daemon's scheduler drains. Produces no <see cref="CommandResult"/> — there
/// is no workflow to pump — so it joins <c>trust</c>/<c>keep</c>/<c>watch</c> in <c>Program.cs</c>'s
/// carve-out rather than the CommandResult/FlowStateReporter switch.
/// </summary>
/// <remarks>
/// <b>Nothing here starts a lane</b> — spec/baton.md §13 states that split and why. What it means for
/// this file specifically: every method below returns having written the queue file (and, for
/// <c>add</c>, the spec copy and possibly a worktree), and never having touched a room.
/// </remarks>
public static class QueueCommand
{
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
            static (issue, sourceRepository, worktreeRoot, repository, writer, token) =>
                IssueWorktreeProvisioner.ProvisionAsync(
                    issue,
                    sourceRepository,
                    worktreeRoot,
                    repository,
                    output: writer,
                    cancellationToken: token));

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
        Func<int, string, string?, string, TextWriter, CancellationToken, Task<string>> issueProvisioner)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(repositoryResolver);
        ArgumentNullException.ThrowIfNull(issueProvisioner);

        return options.Verb switch
        {
            QueueVerb.Add => AddAsync(
                options, output, repositoryDirectory, repositoryResolver, issueProvisioner, cancellationToken),
            QueueVerb.List => ListAsync(output, cancellationToken),
            QueueVerb.Hold => SetHoldAsync(true, output, cancellationToken),
            QueueVerb.Resume => SetHoldAsync(false, output, cancellationToken),
            QueueVerb.Cancel => CancelAsync(options.Tag!, output, cancellationToken),
            QueueVerb.Import => ImportAsync(options, output, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(options)),
        };
    }

    private static async Task<int> AddAsync(
        QueueOptions options,
        TextWriter output,
        string? repositoryDirectory,
        Func<string, CancellationToken, Task<RepositoryIdentity?>> repositoryResolver,
        Func<int, string, string?, string, TextWriter, CancellationToken, Task<string>> issueProvisioner,
        CancellationToken cancellationToken)
    {
        var tag = options.Tag!;
        var specSource = options.SpecFilePath;
        if (specSource is not null && !File.Exists(specSource))
        {
            throw new CliArgumentException(
                $"Spec file '{specSource}' does not exist.",
                "pass an existing file to --spec; the queue copies it, so the original may be deleted afterwards.");
        }

        var settings = await DaemonSettingsStore.LoadAsync(BatonPaths.SettingsFile, cancellationToken).ConfigureAwait(false);
        var (adapter, tier, adapterFromModel, stageSelections) = ResolveTierForAdd(options, settings.Queue);

        // #2142: queue admission uses the already-resolved tuple the launcher will forward, not the
        // raw item fields and not RoleDispatch's display-only stamp. Refuse before the early tag read,
        // spec copy, worktree provision, or queue mutation so a bad request leaves no queue side effect.
        if (ClaudeInvocationModelPolicy.RefusalMessage(tier.Adapter, tier.Model) is { } refusal)
        {
            throw new CliArgumentException(refusal, ClaudeInvocationModelPolicy.ExplicitModelRemedy + ".");
        }

        // The launched-tag refusal is raised HERE, before the spec copy and before any worktree is
        // provisioned — not only inside the mutate below (#1939 review). File.Copy(overwrite: true)
        // would otherwise already have replaced the running lane's brief by the time the refusal was
        // raised, which is the exact record that refusal exists to protect. This read is the early
        // half; the mutate re-checks under the file lock, which is where the authority stays.
        RefuseIfNotReplaceable(
            (await QueueStore.LoadAsync(BatonPaths.QueueFile, cancellationToken).ConfigureAwait(false))
                .Items.FirstOrDefault(i => string.Equals(i.Tag, tag, StringComparison.Ordinal)),
            tag);

        var sourceRepository = repositoryDirectory ?? Directory.GetCurrentDirectory();
        var issueRepository = options.Issue is not null
            ? await ResolveIssueRepositoryAsync(sourceRepository, repositoryResolver, cancellationToken).ConfigureAwait(false)
            : null;

        // Provisioning first, before anything is written to the queue: a `gh issue develop` that fails
        // must leave no half-added item behind, the same pre-provision-refusal placement
        // DispatchCommand's own drain/continue checks use.
        var workspace = options.Issue is { } issue
            ? await issueProvisioner(
                issue,
                sourceRepository,
                settings.Queue.WorktreeRoot,
                issueRepository!,
                output,
                cancellationToken).ConfigureAwait(false)
            : Path.GetFullPath(options.WorkspaceDirectory!);

        if (!Directory.Exists(workspace))
        {
            throw new CliArgumentException(
                $"Workspace '{workspace}' does not exist.",
                "create it, or pass --issue <n> to have the queue provision a worktree for you.");
        }

        // Q6: the spec is COPIED, not referenced. The runner's briefs were rewritten inline eight
        // times in one evening (#1934 body); an item that launched days later against whatever the
        // file had become is the failure this copy exists to stop.
        Directory.CreateDirectory(BatonPaths.QueueSpecsDirectory);
        var specDestination = BatonPaths.QueueSpecFile(tag);

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
            StageSelections = stageSelections,
            LifecyclePin = options.LifecyclePin,
            TimeoutMinutes = options.TimeoutMinutes,
            MaxToolSteps = options.MaxToolSteps,
            TokenBudget = options.TokenBudget,
            OverrideRunwayReason = options.OverrideRunwayReason,
            Reason = options.Reason,
            Issue = options.Issue,
            Stage = options.Lifecycle ? WorkStage.Implement : null,
            Branch = options.Lifecycle ? IssueWorktreeProvisioner.BranchNameFor(options.Issue!.Value) : null,
            Repository = issueRepository,
            // Explicit false distinguishes a newly-created lifecycle item from a pre-#2131 item
            // whose persisted history has no trustworthy automatic-fix budget.
            AutomaticFixUsed = options.Lifecycle ? false : null,
            AddedAt = DateTimeOffset.UtcNow,
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
                : ($"Implement #{options.Issue}", await File.ReadAllTextAsync(specSource, cancellationToken).ConfigureAwait(false));

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
        await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot =>
        {
            // A tag is an identity, not just a label: it names one spec file, so two items sharing one
            // would silently share a brief. Re-adding a tag that is still QUEUED replaces it (the
            // operator is editing their list); re-adding one that has LAUNCHED is refused, because the
            // running lane's own record would be overwritten.
            var existing = snapshot.Items.FirstOrDefault(i => string.Equals(i.Tag, tag, StringComparison.Ordinal));
            RefuseIfNotReplaceable(existing, tag);

            // The copied brief is part of replacing this tag, not a preliminary side effect. Keep it
            // inside the queue's authoritative mutation so a cancellation that wins the same lock is
            // refused before it can overwrite the retained brief.
            File.WriteAllText(specDestination, specContents);

            replaced = existing is not null;
            var items = snapshot.Items.Where(i => !string.Equals(i.Tag, tag, StringComparison.Ordinal)).ToList();
            items.Add(item);
            return snapshot with { Items = items };
        }, cancellationToken).ConfigureAwait(false);

        output.WriteLine($"{(replaced ? "Replaced" : "Queued")} '{tag}' ({item.Role}) in {workspace}");
        output.WriteLine($"  spec: {specDestination}");
        output.WriteLine($"  tier: {DescribeTier(tier, adapterFromModel)}");
        if (tier.IsOverride)
        {
            output.WriteLine($"  override: {tier.OverrideReason}");
        }

        return 0;
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

    private static async Task<int> ListAsync(TextWriter output, CancellationToken cancellationToken)
    {
        var snapshot = await QueueStore.LoadAsync(BatonPaths.QueueFile, cancellationToken).ConfigureAwait(false);
        var settings = snapshot.Items.Any(i => i.Stage is not null)
            ? (await DaemonSettingsStore.LoadAsync(BatonPaths.SettingsFile, cancellationToken).ConfigureAwait(false)).Queue
            : null;
        if (snapshot.Held)
        {
            output.WriteLine("Queue is HELD — no new launches until 'baton queue resume'. Live lanes are unaffected.");
        }

        await PrintWaitAsync(output, cancellationToken).ConfigureAwait(false);

        if (snapshot.Items.Count == 0)
        {
            output.WriteLine("Queue is empty.");
            return 0;
        }

        foreach (var item in snapshot.Items)
        {
            // `halted` in the status column: an item the queue has given up on and one that merely
            // failed its lane and is still an advance candidate otherwise print identically, and
            // halted is precisely the fact that tells the operator they must act — nothing in the
            // product clears it (QueueItem.Halted).
            var state = item.State.ToString().ToLowerInvariant() + (item.Halted ? " halted" : string.Empty);
            var where = item.RoomDirectory is { Length: > 0 } room ? $"  room: {room}" : string.Empty;
            var external = item.External ? "  (external — counted, never launched)" : string.Empty;

            // The stage, when there is one. A slice-1 dispatch request prints exactly what it printed
            // before -- the absence of a stage is the absence of a lifecycle, and inventing a word for
            // it ("none", "single") would read as a stage the product has.
            var stage = item.Stage is { } workStage
                ? $"  stage: {WorkStages.Token(workStage)}"
                    + (item.Round > 0 ? $" (round {item.Round})" : string.Empty)
                    + (item.PullRequest is { } pr ? $"  PR #{pr}" : string.Empty)
                : string.Empty;
            output.WriteLine($"{item.Tag}  {state}  {item.Role}{stage}{external}{where}");
            if (item.Stage is not null && settings is not null)
            {
                output.WriteLine($"  effective stage plan: {DescribeStagePlan(item, settings)}");
            }
            if (item.Error is { Length: > 0 } error)
            {
                output.WriteLine($"  error: {error}");
            }
        }

        return 0;
    }

    private static async Task<int> SetHoldAsync(bool held, TextWriter output, CancellationToken cancellationToken)
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
    private static async Task<int> CancelAsync(string tag, TextWriter output, CancellationToken cancellationToken)
    {
        QueueItem? observed = null;
        var cancelledAt = DateTimeOffset.UtcNow;
        await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot =>
        {
            observed = snapshot.Items.FirstOrDefault(item => string.Equals(item.Tag, tag, StringComparison.Ordinal));
            if (observed?.State != QueueItemState.Queued
                || observed.ReadinessMutationClaim is { Length: > 0 })
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

    private static async Task<int> ImportAsync(QueueOptions options, TextWriter output, CancellationToken cancellationToken)
    {
        var path = options.ImportFilePath!;
        if (!File.Exists(path))
        {
            throw new CliArgumentException($"File to import '{path}' does not exist. {QueueOptionsParser.Usage}");
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var imported = QueueImport.Parse(json, BatonPaths.QueueSpecFile, DateTimeOffset.UtcNow);

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
            var claimed = snapshot.Items.FirstOrDefault(
                item => item.ReadinessMutationClaim is { Length: > 0 } && importedTags.Contains(item.Tag));
            if (claimed is not null)
            {
                throw new CliArgumentException(
                    $"Item '{claimed.Tag}' has an in-flight pull-request readiness update. Importing it would "
                    + "supersede an operation that is already authorized.",
                    "remove that tag from the import, or wait for reconciliation to finish and retry.");
            }

            var cancelled = snapshot.Items.FirstOrDefault(
                item => item.State == QueueItemState.Cancelled && importedTags.Contains(item.Tag));
            if (cancelled is not null)
            {
                throw new CliArgumentException(
                    $"Item '{cancelled.Tag}' was cancelled before launch. Importing it would overwrite its retained cancellation record.",
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

        if (options.Model is not null && options.Adapter is null && tier.Adapter is not null)
        {
            ValidateAdapterModel(tier.Adapter, options.Model);
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

    /// <summary>
    /// Every explicitly selected lifecycle stage is validated before worktree provisioning, so an
    /// invalid later review/fix choice cannot survive until it spends a worker launch. The resolver
    /// remains the source of the actual adapter for a partial selection.
    /// </summary>
    private static void ValidateLifecycleSelections(QueueItem item, QueueSettings settings)
    {
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
            if (ClaudeInvocationModelPolicy.RefusalMessage(tier.Adapter, tier.Model) is { } refusal)
            {
                throw new CliArgumentException(
                    $"The {WorkStages.Token(stage)} selection is invalid: {refusal}",
                    ClaudeInvocationModelPolicy.ExplicitModelRemedy + ".");
            }

            if (tier.Adapter is { } adapter && tier.Model is { } model)
            {
                var candidates = WorkerModelCatalog.AdaptersFor(model);
                if (candidates.Count > 0 && !candidates.Contains(adapter, StringComparer.OrdinalIgnoreCase))
                {
                    throw new CliArgumentException(
                        $"The {WorkStages.Token(stage)} selection's resolved model '{model}' is known by "
                        + $"{string.Join(", ", candidates)}, but the resolved {adapter} adapter cannot use it.");
                }

                try
                {
                    ValidateAdapterModel(adapter, model);
                }
                catch (CliArgumentException ex)
                {
                    throw new CliArgumentException(
                        $"The {WorkStages.Token(stage)} selection's resolved {adapter}/{model} tuple is invalid: {ex.Message}");
                }
            }
        }
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
            throw new CliArgumentException(ex.Message);
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
