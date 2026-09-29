using System.Globalization;
using System.Text.Json;
using Baton.Accounting;
using Baton.Queue;
using Baton.Status;
using Baton.Vendors;

namespace Baton.Cli.Daemon;

public sealed partial class WorkItemAdvancer
{
    private const string DraftPullRequestBase = "main";
    private const int DraftPullRequestHistoryLimit = 100;
    private const string DraftHistoryJsonFields =
        "number,state,isDraft,headRefOid,headRefName,baseRefName,isCrossRepository";
    private static readonly TimeSpan DefaultDraftPullRequestCommandTimeout = TimeSpan.FromSeconds(20);

    internal static bool IsDraftHandoffEnabledNow(string? repository)
    {
        if (repository is null || !File.Exists(BatonPaths.SettingsFile)) return false;
        try
        {
            var settings = JsonSerializer.Deserialize<DaemonSettings>(File.ReadAllText(BatonPaths.SettingsFile));
            return settings?.Queue.IsDraftPullRequestHandoffEnabled(repository) == true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Draft PR handoff settings are unreadable; automatic creation remains off: {ex.Message}");
            return false;
        }
    }

    private async Task<(QueueItem Item, PullRequestObservation Observation)?> TryCreateDraftPullRequestAsync(
        QueueItem item, string outcome, string? observedHead, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!EligibleSettledImplementation(item, outcome, observedHead))
        {
            await RetainDraftHandoffReasonAsync(item,
                "automatic draft PR refused: current implementation attempt lacks durable succeeded-shaped settlement identity")
                .ConfigureAwait(false);
            return null;
        }

        var proof = await ReadPushedCleanHeadAsync(item, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(proof.Head, observedHead, StringComparison.Ordinal))
        {
            await RetainDraftHandoffReasonAsync(item, proof.Error ??
                "automatic draft PR refused: workspace or remote branch tip changed").ConfigureAwait(false);
            return null;
        }

        // A successful, complete all-state history is required. Open-only absence is not absence:
        // closed and merged PRs mean this branch may already have been delivered or rejected.
        var history = await ReadDraftPullRequestHistoryAsync(item, cancellationToken).ConfigureAwait(false);
        if (!history.Succeeded || history.PullRequests.Count != 0)
        {
            await RetainDraftHandoffReasonAsync(item, history.Error ??
                "automatic draft PR refused: this branch already has PR history; reconcile it with the operator")
                .ConfigureAwait(false);
            return null;
        }

        // The immediate pre-admission rereads reduce stale evidence. Neither GitHub nor settings.json
        // is transactional with queue.json: disabling after the marker cannot revoke that admitted
        // caller, and an unrelated actor may still create a PR after the last absence read.
        proof = await ReadPushedCleanHeadAsync(item, cancellationToken).ConfigureAwait(false);
        history = await ReadDraftPullRequestHistoryAsync(item, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(proof.Head, observedHead, StringComparison.Ordinal)
            || !history.Succeeded || history.PullRequests.Count != 0)
        {
            await RetainDraftHandoffReasonAsync(item, proof.Error ?? history.Error ??
                "automatic draft PR refused: branch or PR history changed before admission")
                .ConfigureAwait(false);
            return null;
        }

        var marker = new QueueDraftPullRequestCreateMarker(
            item.AttemptId!.Value, item.RoomDirectory!, item.Repository!, item.Branch!, observedHead!,
            DraftPullRequestBase, now);
        var expectedJson = JsonSerializer.Serialize(item);
        QueueItem? claimed = null;
        await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot =>
        {
            var current = snapshot.Items.FirstOrDefault(candidate => candidate.Tag == item.Tag);
            if (current is null || !string.Equals(JsonSerializer.Serialize(current), expectedJson, StringComparison.Ordinal)
                || current.DraftPullRequestCreateMarker is not null
                || !EligibleSettledImplementation(current, outcome, observedHead)
                || current.AttemptEnvelope is not { } envelope
                || QueueFleetEventOutbox.HasPendingFor(snapshot, envelope.AttemptId)
                || !IsDraftHandoffEnabledNow(current.Repository))
            {
                return snapshot;
            }

            // Protected invariant: this durable marker commits BEFORE the one external create call.
            // It is never cleared, even when later review stages replace the current attempt fields.
            claimed = current with
            {
                DraftPullRequestCreateMarker = marker,
                Error = $"draft PR creation may have been called for {marker.Branch} at {marker.HeadSha}; "
                    + "reconciling exact GitHub state before review",
            };
            return snapshot with
            {
                Items = snapshot.Items.Select(candidate => ReferenceEquals(candidate, current) ? claimed : candidate).ToList(),
            };
        }, CancellationToken.None).ConfigureAwait(false);
        if (claimed is null) return null;

        // The winner alone may call create. A post-marker proof failure consumes the allowance but
        // cannot accidentally publish a drifted branch; restart can only observe, never retry create.
        proof = await ReadPushedCleanHeadAsync(claimed, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(proof.Head, marker.HeadSha, StringComparison.Ordinal))
        {
            await RetainDraftHandoffReasonAsync(claimed, proof.Error ??
                "draft PR create was withheld after its marker because branch proof changed")
                .ConfigureAwait(false);
            return null;
        }

        try
        {
            _ = await RunBoundedGhAsync(claimed,
                RepositoryArgs(claimed, "pr", "create", "--draft", "--head", marker.Branch,
                    "--base", marker.BaseBranch,
                    "--title", $"Implement #{claimed.Issue}: {claimed.Tag}",
                    "--body", $"Implementation from the settled queue attempt for #{claimed.Issue}.\n\nCloses #{claimed.Issue}"),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The marker already owns this possibly-started action. Next daemon pass observes only.
            return null;
        }

        var observation = await RecoverDraftPullRequestAsync(claimed, cancellationToken).ConfigureAwait(false);
        return observation is null ? null : (claimed, observation);
    }

    private static bool EligibleSettledImplementation(QueueItem item, string outcome, string? head) =>
        item is
        {
            Stage: WorkStage.Implement,
            State: QueueItemState.Failed,
            Halted: true,
            ReconciliationKind: QueueReconciliationKind.AwaitingVerifiedPullRequest,
            Retirement: null,
            External: false,
            PullRequest: null,
            RoomDirectory: { Length: > 0 },
            Repository: { Length: > 0 },
            Branch: { Length: > 0 },
            Issue: > 0,
            AttemptId: not null,
            AttemptEnvelope: not null,
            AttemptAdmissionFactDurable: true,
            AttemptStartedFactDurable: true,
            AttemptSettledFactDurable: true,
            DraftPullRequestCreateMarker: null,
        }
        && item.AttemptEnvelope.AttemptId == item.AttemptId
        && string.Equals(item.AttemptEnvelope.WorkId, item.Tag, StringComparison.Ordinal)
        && item.AttemptEnvelope.Issue == item.Issue
        && string.Equals(item.AttemptEnvelope.RoomDirectory, item.RoomDirectory, StringComparison.Ordinal)
        && item.AttemptEnvelope.Stage == WorkStage.Implement
        && item.AttemptEnvelope.AdmissionDecision == TaskRequirementAdmission.Admitted
        && (item.WorkspaceOrigin == WorkspaceOrigins.IssueProvisioned
            || item.WorkspaceOrigin == WorkspaceOrigins.OperatorSupplied
                && item.RetainedWorktreeReuse is { } reuse
                && string.Equals(reuse.Repository, item.Repository, StringComparison.Ordinal)
                && string.Equals(reuse.Branch, item.Branch, StringComparison.Ordinal))
        && WorkflowOutcome.IsSucceededShaped(outcome)
        && head is { Length: 40 } && head.All(char.IsAsciiHexDigit)
        && string.Equals(RepositoryIdentity.TryCanonicalize(item.Repository), item.Repository, StringComparison.Ordinal)
        && !item.Repository.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase);

    private async Task<(string? Head, string? Error)> ReadPushedCleanHeadAsync(
        QueueItem item, CancellationToken cancellationToken)
    {
        if (item.Repository is null || item.Branch is null || !Directory.Exists(item.Workspace))
            return (null, "automatic draft PR refused: workspace or recorded identity is unavailable");
        var actual = await ReadTrustedDraftRepositoryIdentityAsync(item, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actual?.RemoteValue, item.Repository, StringComparison.Ordinal))
            return (null, "automatic draft PR refused: workspace origin differs from the recorded canonical repository");

        var branch = await RunBoundedGitAsync(item, ["symbolic-ref", "--quiet", "--short", "HEAD"], cancellationToken)
            .ConfigureAwait(false);
        var status = await RunBoundedGitAsync(item,
            ["status", "--porcelain=v1", "--untracked-files=all", "--ignore-submodules=none"], cancellationToken)
            .ConfigureAwait(false);
        var head = await RunBoundedGitAsync(item, ["rev-parse", "--verify", "HEAD"], cancellationToken)
            .ConfigureAwait(false);
        var remote = await RunBoundedGitAsync(item,
            ["ls-remote", "--heads", "origin", $"refs/heads/{item.Branch}", "refs/heads/main"], cancellationToken)
            .ConfigureAwait(false);
        if (!Good(branch) || !Good(status) || !Good(head) || !Good(remote)
            || !string.Equals(branch.Stdout.Trim(), item.Branch, StringComparison.Ordinal)
            || status.Stdout.Length != 0)
            return (null, "automatic draft PR refused: branch, clean status, HEAD or origin read was unavailable or changed");

        var sha = head.Stdout.Trim();
        if (sha.Length != 40 || !sha.All(char.IsAsciiHexDigit))
            return (null, "automatic draft PR refused: local HEAD is not a full commit SHA");
        var refs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in remote.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split('\t');
            if (parts.Length != 2 || parts[0].Length != 40 || !parts[0].All(char.IsAsciiHexDigit)
                || !refs.TryAdd(parts[1], parts[0]))
                return (null, "automatic draft PR refused: origin returned ambiguous branch/base refs");
        }

        return refs.Count == (item.Branch == DraftPullRequestBase ? 1 : 2)
            && refs.TryGetValue($"refs/heads/{item.Branch}", out var remoteHead)
            && refs.ContainsKey("refs/heads/main")
            && string.Equals(sha, remoteHead, StringComparison.OrdinalIgnoreCase)
            ? (sha, null)
            : (null, "automatic draft PR refused: exact origin branch tip or main base is not proven");
    }

    private static bool Good(GhCliResult result) => result.Started && result.ExitCode == 0;

    // Every Git answer used to authorize this opt-in mutation comes from the same bounded,
    // outside-workspace executable. The ordinary lifecycle probes have a weaker trust contract.
    private async Task<RepositoryIdentity?> ReadTrustedDraftRepositoryIdentityAsync(
        QueueItem item, CancellationToken cancellationToken)
    {
        var origin = await RunBoundedGitAsync(item,
            ["config", "--get", "remote.origin.url"], cancellationToken).ConfigureAwait(false);
        return Good(origin) ? RepositoryIdentity.From(origin.Stdout.Trim(), null) : null;
    }

    private async Task<string?> ReadTrustedDraftHeadAsync(QueueItem item, CancellationToken cancellationToken)
    {
        var head = await RunBoundedGitAsync(item, ["rev-parse", "--verify", "HEAD"], cancellationToken)
            .ConfigureAwait(false);
        var sha = head.Stdout.Trim();
        return Good(head) && sha.Length == 40 && sha.All(char.IsAsciiHexDigit) ? sha : null;
    }

    private async Task<GhCliResult> RunBoundedGitAsync(
        QueueItem item, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var executable = _gitInjected ? "git" : OutsideWorkspaceExecutableResolver.TryResolve(
            Environment.GetEnvironmentVariable("PATH"), item.Workspace, "git", OperatingSystem.IsWindows());
        if (executable is null)
            return new GhCliResult(false, -1, string.Empty,
                "Could not resolve a link-free git executable outside the worker workspace.");
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bound.CancelAfter(_draftCommandTimeout);
        try
        {
            // Git status can execute a repository-configured fsmonitor hook. Never allow that
            // worker-controlled command to run while proving the clean tree for PR creation.
            var safeArgs = new[] { "-c", "core.fsmonitor=false" }.Concat(args).ToArray();
            return await _git(executable, item.Workspace, safeArgs, bound.Token).WaitAsync(bound.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new GhCliResult(false, -1, string.Empty, "git branch proof timed out");
        }
    }

    private async Task<GhCliResult> RunBoundedGhAsync(
        QueueItem item, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bound.CancelAfter(_draftCommandTimeout);
        try
        {
            return await _draftGh.RunAsync(item.Workspace, args, bound.Token).WaitAsync(bound.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new GhCliResult(false, -1, string.Empty, "GitHub command timed out");
        }
    }

    private async Task<(bool Succeeded, IReadOnlyList<PullRequestObservation> PullRequests, string? Error)>
        ReadDraftPullRequestHistoryAsync(QueueItem item, CancellationToken cancellationToken)
    {
        var args = RepositoryArgs(item,
            "pr", "list", "--head", item.Branch!, "--base", DraftPullRequestBase,
            "--state", "all", "--limit",
            DraftPullRequestHistoryLimit.ToString(CultureInfo.InvariantCulture), "--json", DraftHistoryJsonFields);
        var result = await RunBoundedGhAsync(item, args, cancellationToken).ConfigureAwait(false);
        if (!Good(result)) return (false, [], "automatic draft PR refused: all-state GitHub history is unavailable");
        try
        {
            using var document = JsonDocument.Parse(result.Stdout);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return (false, [], "automatic draft PR refused: all-state GitHub history is not an array");
            var entries = document.RootElement.EnumerateArray().ToList();
            if (entries.Count >= DraftPullRequestHistoryLimit)
                return (false, [], "automatic draft PR refused: all-state GitHub history hit its bounded result limit");
            var parsed = new List<PullRequestObservation>();
            foreach (var entry in entries)
            {
                if (!TryReadPullRequest(entry, item, null, out var pr, out var error))
                    return (false, [], $"automatic draft PR refused: {error}");
                parsed.Add(pr);
            }

            return (true, parsed, null);
        }
        catch (JsonException)
        {
            return (false, [], "automatic draft PR refused: all-state GitHub history is malformed JSON");
        }
    }

    private async Task<PullRequestObservation?> RecoverDraftPullRequestAsync(
        QueueItem item, CancellationToken cancellationToken)
    {
        var marker = item.DraftPullRequestCreateMarker;
        if (marker is null || item.AttemptId != marker.AttemptId
            || !string.Equals(item.RoomDirectory, marker.RoomDirectory, StringComparison.Ordinal)
            || !string.Equals(item.Repository, marker.Repository, StringComparison.Ordinal)
            || !string.Equals(item.Branch, marker.Branch, StringComparison.Ordinal)
            || marker.BaseBranch != DraftPullRequestBase)
        {
            await RetainDraftHandoffReasonAsync(item,
                "draft PR create marker identity conflicts with the current queue attempt; operator reconciliation required")
                .ConfigureAwait(false);
            return null;
        }

        // Opt-out stops new creates, not recovery of a may-have-called marker. Recovery still
        // needs trusted local identity and the exact pinned HEAD before it can release Review.
        var actual = await ReadTrustedDraftRepositoryIdentityAsync(item, cancellationToken).ConfigureAwait(false);
        var localHead = await ReadTrustedDraftHeadAsync(item, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actual?.RemoteValue, marker.Repository, StringComparison.Ordinal)
            || !string.Equals(localHead, marker.HeadSha, StringComparison.OrdinalIgnoreCase))
        {
            await RetainDraftHandoffReasonAsync(item,
                "draft PR create marker no longer matches the trusted workspace origin and local HEAD; operator reconciliation required")
                .ConfigureAwait(false);
            return null;
        }

        var history = await ReadDraftPullRequestHistoryAsync(item, cancellationToken).ConfigureAwait(false);
        if (!history.Succeeded || history.PullRequests.Count != 1
            || history.PullRequests[0] is not { Number: { } number, IsOpen: true, IsDraft: true } found
            || !string.Equals(found.HeadSha, marker.HeadSha, StringComparison.OrdinalIgnoreCase))
        {
            await RetainDraftHandoffReasonAsync(item, history.Error ??
                "draft PR create may have been called, but one open draft on the pinned head is not verified; "
                + "the operator must reconcile it; Baton will not create again").ConfigureAwait(false);
            return null;
        }

        var exact = await ReadPinnedDraftSnapshotAsync(item with { PullRequest = number }, cancellationToken)
            .ConfigureAwait(false);
        if (!exact.Succeeded || exact.Number != number || exact.IsOpen != true || exact.IsDraft != true
            || !string.Equals(exact.HeadSha, marker.HeadSha, StringComparison.OrdinalIgnoreCase))
        {
            await RetainDraftHandoffReasonAsync(item,
                "draft PR changed during pinned-head verification; operator reconciliation required")
                .ConfigureAwait(false);
            return null;
        }

        return exact;
    }

    private async Task<PullRequestObservation> ReadPinnedDraftSnapshotAsync(
        QueueItem item, CancellationToken cancellationToken)
    {
        var number = item.PullRequest!.Value;
        async Task<PullRequestObservation> SnapshotAsync()
        {
            var result = await RunBoundedGhAsync(item,
                RepositoryArgs(item, "pr", "view", number.ToString(CultureInfo.InvariantCulture),
                    "--json", PullRequestJsonFields), cancellationToken).ConfigureAwait(false);
            if (!Good(result)) return PullRequestObservation.Failed("pinned PR view is unavailable");
            try
            {
                using var document = JsonDocument.Parse(result.Stdout);
                return TryReadPullRequest(document.RootElement, item, number, out var observed, out var error)
                    ? observed : PullRequestObservation.Failed(error!);
            }
            catch (JsonException)
            {
                return PullRequestObservation.Failed("pinned PR view returned malformed JSON");
            }
        }

        var before = await SnapshotAsync().ConfigureAwait(false);
        if (!before.Succeeded || before.IsOpen != true || before.IsDraft != true)
            return before;
        var checkResult = await RunBoundedGhAsync(item,
            RepositoryArgs(item, "pr", "checks", number.ToString(CultureInfo.InvariantCulture),
                "--required", "--json", "bucket,name,state,workflow"), cancellationToken).ConfigureAwait(false);
        // gh pr checks exits nonzero for pending/failing checks even with valid typed JSON.
        // Those states gate readiness later; they do not block the first review handoff.
        var required = checkResult.Started
            ? PullRequestChecks.TrySummarizeRequired(checkResult.Stdout) : null;
        if (required is null) return PullRequestObservation.Failed("pinned PR required checks are unreadable", before);
        var after = await SnapshotAsync().ConfigureAwait(false);
        return after.Succeeded && after.Number == before.Number && after.IsOpen == true && after.IsDraft == true
            && string.Equals(after.HeadSha, before.HeadSha, StringComparison.OrdinalIgnoreCase)
            ? after with { RequiredChecks = required }
            : PullRequestObservation.Failed("pinned PR changed during required-check observation", after);
    }

    private static async Task RetainDraftHandoffReasonAsync(QueueItem item, string reason) =>
        _ = await TryMarkAsync(item, current => current with
        {
            Error = reason,
            Halted = true,
            ReconciliationKind = QueueReconciliationKind.AwaitingVerifiedPullRequest,
        }).ConfigureAwait(false);
}
