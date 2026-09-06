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
/// <b>Nothing here launches anything.</b> Adding an item is a durable request; the daemon decides when
/// it runs, records why, and is the only thing that dispatches. That split is what makes the launch
/// decision auditable — a verb that could also launch would leave two paths into a room and only one
/// of them recorded.
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
        var specSource = options.SpecFilePath!;
        if (!File.Exists(specSource))
        {
            throw new CliArgumentException(
                $"Spec file '{specSource}' does not exist.",
                "pass an existing file to --spec; the queue copies it, so the original may be deleted afterwards.");
        }

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
        File.Copy(specSource, specDestination, overwrite: true);

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
            AddedAt = DateTimeOffset.UtcNow,
        };

        var replaced = false;
        await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot =>
        {
            // A tag is an identity, not just a label: it names one spec file, so two items sharing one
            // would silently share a brief. Re-adding a tag that is still QUEUED replaces it (the
            // operator is editing their list); re-adding one that has LAUNCHED is refused, because the
            // running lane's own record would be overwritten.
            var existing = snapshot.Items.FirstOrDefault(i => string.Equals(i.Tag, tag, StringComparison.Ordinal));
            if (existing is { State: QueueItemState.Launched })
            {
                throw new CliArgumentException(
                    $"Item '{tag}' is already launched into room '{existing.RoomDirectory}'. Re-adding it would "
                    + "overwrite that lane's record.",
                    "pick a different tag, or wait for the lane to settle.");
            }

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

    private static async Task<int> ListAsync(TextWriter output, CancellationToken cancellationToken)
    {
        var snapshot = await QueueStore.LoadAsync(BatonPaths.QueueFile, cancellationToken).ConfigureAwait(false);
        if (snapshot.Held)
        {
            output.WriteLine("Queue is HELD — no new launches until 'baton queue resume'. Live lanes are unaffected.");
        }

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
            output.WriteLine($"{item.Tag}  {state}  {item.Role}{external}{where}");
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

    /// <summary>Formats a decision the way the ledger's <c>reason</c> field carries it — used by the
    /// daemon's service and kept here so the queue's verbs and its scheduler word a wait identically.</summary>
    public static string DescribeWait(QueueWaitReason reason, double liveWeight, double? freeGb, double floorGb) =>
        reason switch
        {
            QueueWaitReason.Slots => FormattableString.Invariant(
                $"slots (live weight {liveWeight:0.##})"),
            QueueWaitReason.Memory => FormattableString.Invariant(
                $"memory (free {freeGb ?? 0:0.##} GiB below the {floorGb:0.##} GiB floor)"),
            _ => QueueWaitReasons.Token(reason),
        };

    /// <summary>Renders a count the same way everywhere. Invariant culture on purpose: this string
    /// reaches a JSONL ledger a script parses, not only a terminal.</summary>
    internal static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
