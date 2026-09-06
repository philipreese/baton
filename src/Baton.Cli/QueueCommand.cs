using System.Globalization;
using Baton.Queue;
using Baton.Status;
using Baton.Vendors;

namespace Baton.Cli;

/// <summary>
/// <c>baton queue add|list|hold|resume|import</c> (#1934 slice 1): the operator's control surface over
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
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);

        return options.Verb switch
        {
            QueueVerb.Add => AddAsync(options, output, repositoryDirectory, cancellationToken),
            QueueVerb.List => ListAsync(output, cancellationToken),
            QueueVerb.Hold => SetHoldAsync(true, output, cancellationToken),
            QueueVerb.Resume => SetHoldAsync(false, output, cancellationToken),
            QueueVerb.Import => ImportAsync(options, output, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(options)),
        };
    }

    private static async Task<int> AddAsync(
        QueueOptions options, TextWriter output, string? repositoryDirectory, CancellationToken cancellationToken)
    {
        var tag = options.Tag!;
        var specSource = options.SpecFilePath;
        if (specSource is not null && !File.Exists(specSource))
        {
            throw new CliArgumentException(
                $"Spec file '{specSource}' does not exist.",
                "pass an existing file to --spec; the queue copies it, so the original may be deleted afterwards.");
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

        // Provisioning first, before anything is written to the queue: a `gh issue develop` that fails
        // must leave no half-added item behind, the same pre-provision-refusal placement
        // DispatchCommand's own drain/continue checks use.
        var workspace = options.Issue is { } issue
            ? await IssueWorktreeProvisioner.ProvisionAsync(
                issue,
                repositoryDirectory ?? Directory.GetCurrentDirectory(),
                (await DaemonSettingsStore.LoadAsync(BatonPaths.SettingsFile, cancellationToken).ConfigureAwait(false))
                    .Queue.WorktreeRoot,
                cancellationToken: cancellationToken).ConfigureAwait(false)
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
            Adapter = options.Adapter,
            Model = options.Model,
            Effort = options.Effort,
            TimeoutMinutes = options.TimeoutMinutes,
            MaxToolSteps = options.MaxToolSteps,
            TokenBudget = options.TokenBudget,
            OverrideRunwayReason = options.OverrideRunwayReason,
            Reason = options.Reason,
            Issue = options.Issue,
            Stage = options.Lifecycle ? WorkStage.Implement : null,
            Branch = options.Lifecycle ? IssueWorktreeProvisioner.BranchNameFor(options.Issue!.Value) : null,
            AddedAt = DateTimeOffset.UtcNow,
        };

        if (options.Lifecycle)
        {
            // The templates are the product (operator ruling 2026-09-06): a work item's brief is
            // RENDERED here, not written by hand and copied. A --spec is still honoured -- its text
            // becomes the template's "## Do" section -- so an operator who has already written the
            // instructions keeps them, and gets the standing rules and the ship block for free.
            var (title, body) = specSource is null
                ? await IssueWorktreeProvisioner.FetchIssueAsync(
                    options.Issue!.Value, repositoryDirectory ?? Directory.GetCurrentDirectory(),
                    cancellationToken: cancellationToken).ConfigureAwait(false)
                : ($"Implement #{options.Issue}", await File.ReadAllTextAsync(specSource, cancellationToken).ConfigureAwait(false));

            // Captured on the ITEM as well as rendered into the brief -- QueueItem.Instructions' own
            // remarks say why the brief cannot be the register for this.
            item = item with { Instructions = body.Trim() };

            await File.WriteAllTextAsync(
                specDestination,
                QueueBriefTemplates.Compose(
                    WorkStage.Implement, item, new QueueBriefTemplates.BriefContext(Title: title, Do: body.Trim())),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            File.Copy(specSource!, specDestination, overwrite: true);
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

            replaced = existing is not null;
            var items = snapshot.Items.Where(i => !string.Equals(i.Tag, tag, StringComparison.Ordinal)).ToList();
            items.Add(item);
            return snapshot with { Items = items };
        }, cancellationToken).ConfigureAwait(false);

        var settings = await DaemonSettingsStore.LoadAsync(BatonPaths.SettingsFile, cancellationToken).ConfigureAwait(false);
        var tier = QueueTierTable.Resolve(item, settings.Queue);
        output.WriteLine($"{(replaced ? "Replaced" : "Queued")} '{tag}' ({item.Role}) in {workspace}");
        output.WriteLine($"  spec: {specDestination}");
        output.WriteLine($"  tier: {DescribeTier(tier)}");
        if (tier.IsOverride)
        {
            output.WriteLine($"  override: {tier.OverrideReason}");
        }

        return 0;
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
        if (existing is { State: QueueItemState.Launched })
        {
            throw new CliArgumentException(
                $"Item '{tag}' is already launched into room '{existing.RoomDirectory}'. Re-adding it would "
                + "overwrite that lane's record.",
                "pick a different tag, or wait for the lane to settle.");
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
            var state = item.State.ToString().ToLowerInvariant();
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

    private static string DescribeTier(QueueTierResolution tier)
    {
        var parts = new List<string>();
        if (tier.TierKey is { Length: > 0 } key)
        {
            parts.Add(key);
        }

        parts.Add(tier.Adapter ?? "role default adapter");
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
