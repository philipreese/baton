using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Core;
using Baton.Domain;

namespace Baton.Mutation;

/// <summary>
/// Which of <see cref="DeliveryVerifier.CheckAsync"/>'s four verdicts applies. Mirrors
/// <see cref="VerifyOutcome"/>'s own shape (pass / fail-with-members / not-run-with-reason / cancelled)
/// rather than reusing that record directly — a delivery check names no gate command and carries no
/// <see cref="VerifyFailedKind"/> of its own; the caller (<c>MutationInterface</c>) picks
/// <see cref="Domain.VerifyFailedKind.DeliveryFailed"/> for it.
/// </summary>
public enum DeliveryCheckStatus
{
    Passed,
    Failed,
    NotRun,
    // #1788 review: the operator's own cancellation firing mid-check must never be misread as a spawn
    // failure (which would report a misleading NotRun reason) or, worse, fall through to the ordinary
    // Succeeded outcome append the way an unrelated NotRun does. Mirrors VerifyRunner's own "cancellation
    // takes precedence over whatever exit code the child happened to produce" rule.
    Cancelled,
}

/// <summary>
/// The result of one <see cref="DeliveryVerifier.CheckAsync"/> call (#1788). <see cref="FailingMembers"/>
/// is populated only for <see cref="DeliveryCheckStatus.Failed"/>, using exactly the two names the issue
/// names (<c>branch-not-pushed</c>, <c>pr-not-open</c>) — never a third, and never fabricated for a
/// <see cref="DeliveryCheckStatus.NotRun"/> verdict. <see cref="Tail"/> is a short, human-readable line
/// per failing member, meant to become the room's <c>verifyTail</c> the same way
/// <see cref="VerifyOutcome.Tail"/> does. <see cref="NotRunReason"/> is populated only for
/// <see cref="DeliveryCheckStatus.NotRun"/>.
/// </summary>
public sealed record DeliveryCheckOutcome(
    DeliveryCheckStatus Status,
    IReadOnlyList<string>? FailingMembers = null,
    string? Tail = null,
    string? NotRunReason = null,
    string? CheckedLocalHead = null,
    string? CheckedRemoteHead = null,
    bool CheckedPullRequestOpen = false,
    int? CheckedPullRequestNumber = null,
    string? CheckedPullRequestHead = null)
{
    public static readonly DeliveryCheckOutcome Pass = new(DeliveryCheckStatus.Passed);
    public static readonly DeliveryCheckOutcome CancelledOutcome = new(DeliveryCheckStatus.Cancelled);
}

/// <summary>The immutable, machine-owned post-execution delivery observation.</summary>
public sealed record DeliveryEvidence(
    [property: JsonPropertyName("observedAt")] string ObservedAt,
    [property: JsonPropertyName("localHead")] string? LocalHead,
    [property: JsonPropertyName("branch")] string? Branch,
    [property: JsonPropertyName("remoteHead")] string? RemoteHead,
    [property: JsonPropertyName("pullRequestNumber")] int? PullRequestNumber,
    [property: JsonPropertyName("verification")] DeliveryCheckStatus Verification,
    [property: JsonPropertyName("failingMembers")] IReadOnlyList<string>? FailingMembers,
    [property: JsonPropertyName("verificationReason")] string? VerificationReason,
    [property: JsonPropertyName("observationProblem")] string? ObservationProblem,
    [property: JsonPropertyName("pullRequestHead")] string? PullRequestHead = null)
{
    public FlowEvent.DeliveryObservationRecorded ToRecordedEvent(ExecutionId executionId) =>
        new(executionId, ObservedAt, LocalHead, Branch, RemoteHead, PullRequestNumber,
            Verification.ToString(), FailingMembers, VerificationReason, ObservationProblem, PullRequestHead);

    public DeliveryCheckOutcome ToOutcome() => Verification switch
    {
        DeliveryCheckStatus.Failed => new(Verification, FailingMembers, VerificationReason),
        DeliveryCheckStatus.NotRun => new(Verification, NotRunReason: VerificationReason),
        _ => new(Verification),
    };
}

/// <summary>The result of opening an immutable stamp; unreadable is distinct from absent.</summary>
public sealed record DeliveryEvidenceReading(DeliveryEvidence? Evidence, string? Problem = null)
{
    public bool IsMissing => Evidence is null && Problem is null;
}

/// <summary>
/// #1978: what <c>gh pr list --head &lt;branch&gt; --json number</c> answered, as three states rather
/// than a bool — <see cref="AnyOpen"/> <see langword="null"/> means the question was NOT answered
/// (<c>gh</c> missing, unauthenticated, network down, a detached HEAD, output that did not parse), and
/// <see cref="NotRunReason"/> says which. Only <see langword="false"/> is positive evidence that no PR
/// is open; only a non-null <see cref="Number"/> may be printed to an operator as a PR reference.
/// </summary>
/// <param name="Number">
/// The first open PR's number when one could be read. <see langword="null"/> both when nothing is open
/// and when the answer was unmeasurable — never a fabricated number, so a consumer must read
/// <see cref="AnyOpen"/> to tell those apart.
/// </param>
public sealed record OpenPullRequestReading(bool? AnyOpen, int? Number = null, string? NotRunReason = null,
    string? Head = null)
{
    /// <summary>The question was answered and no PR is open for the branch.</summary>
    public static readonly OpenPullRequestReading NoneOpen = new(AnyOpen: false);

    /// <summary>The question was not answered at all — <paramref name="reason"/> says why.</summary>
    public static OpenPullRequestReading NotRun(string reason) => new(AnyOpen: null, NotRunReason: reason);
}

/// <summary>
/// #1788 (contract: <c>spec/baton.md</c> §3, "Post-exit delivery check"): resolves whether a workspace
/// has actually delivered what a <c>DeliversBranch</c> role's brief promises — pushed and, when expected,
/// PR'd. Full rationale (why <c>NotRun</c> vs <c>Failed</c> is drawn where it is, the <c>--heads</c>/
/// explicit-refspec/credential-prompt details, and the known gap this does NOT close) lives there, not
/// restated here. Spawns through the plain, ambient-environment form of
/// <see cref="VerifyRunner.CaptureAsync"/> — unlike <see cref="VerifyCommandResolver"/>'s hardened git
/// reads (whose output decides what a later step executes), every spawn here only answers a question
/// about the workspace's remote-visible state.
/// </summary>
public static class DeliveryVerifier
{
    /// <summary>The machine-owned, append-only observation written after a delivery-capable worker exits.</summary>
    public const string DeliveryEvidenceFileName = "delivery-evidence.json";

    private static readonly JsonSerializerOptions EvidenceJsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// <c>git ls-remote --exit-code</c>'s own documented meaning for this exit code: the query succeeded
    /// in reaching the remote, and no ref matched.
    /// </summary>
    private const int LsRemoteRefAbsentExitCode = 2;

    /// <summary><c>git merge-base --is-ancestor</c>'s own documented meaning for this exit code: reachable, but not an ancestor.</summary>
    private const int MergeBaseNotAncestorExitCode = 1;

    /// <summary>
    /// Non-interactive hardening for the two NETWORK-touching git spawns (<c>ls-remote</c>, <c>fetch</c>)
    /// only — <c>rev-parse</c>/<c>merge-base</c> never reach the network and need none of this. Without
    /// it, a host whose credential helper needs a refresh can block on an OS credential-manager prompt
    /// that reads no stdin (so <c>GIT_TERMINAL_PROMPT=0</c> alone does not stop it) rather than failing
    /// fast into this check's own <see cref="DeliveryCheckStatus.NotRun"/> arm (#1788 review).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> NonInteractiveGitEnv =
        new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0", ["GCM_INTERACTIVE"] = "never" };

    private static readonly string[] NonInteractiveGitArgs = ["-c", "credential.interactive=false"];

    /// <param name="shippingCeilingExceeded">
    /// #1998: the final run-command in this execution's captured stream was a shipping-class command
    /// Baton killed at its ceiling (<see cref="ShippingCeilingStreamReader"/> is what answers it). It
    /// changes no verdict — a branch that is not on origin fails either way — only what the
    /// <c>branch-not-pushed</c> tail SAYS: the cause the room can act on rather than the symptom a
    /// conductor then has to reconstruct.
    /// </param>
    public static async Task<DeliveryCheckOutcome> CheckAsync(
        string? workingDirectory,
        bool expectPr,
        CancellationToken cancellationToken,
        string gitProgram = "git",
        string ghProgram = "gh",
        bool shippingCeilingExceeded = false)
    {
        var outcome = await CheckCoreAsync(
            workingDirectory, expectPr, cancellationToken, gitProgram, ghProgram, shippingCeilingExceeded).ConfigureAwait(false);

        // #1788 review: cancellation wins over whatever the accumulated verdict happened to compute --
        // the same precedence VerifyRunner.RunProcessAsync's own post-capture check applies, so an
        // operator cancel landing mid-check can never be misread as a tool-unavailable NotRun (or,
        // worse, silently fall through to the ordinary Succeeded outcome append a NotRun does).
        return cancellationToken.IsCancellationRequested ? DeliveryCheckOutcome.CancelledOutcome : outcome;
    }

    /// <summary>
    /// Makes one post-exit machine observation. Its returned value becomes authoritative only when
    /// the engine appends <see cref="FlowEvent.DeliveryObservationRecorded"/> to its own flow ledger;
    /// a file already in the worker-writable output directory is never input to this method.
    /// </summary>
    public static async Task<DeliveryEvidence> ObserveAsync(string? workingDirectory, bool expectPr,
        DeliveryCheckOutcome deliveryOutcome, CancellationToken cancellationToken, string gitProgram = "git", string ghProgram = "gh")
    {
        string? branch = null, localHead = null, remoteHead = null, observationProblem = null;
        int? pullRequestNumber = null;
        string? pullRequestHead = null;
        OpenPullRequestReading? finalPullRequest = null;
        if (string.IsNullOrWhiteSpace(workingDirectory)) observationProblem = "no working directory for this execution";
        else
        {
            var branchResult = await RunAsync(gitProgram, ["rev-parse", "--abbrev-ref", "HEAD"], workingDirectory, cancellationToken).ConfigureAwait(false);
            var headResult = await RunAsync(gitProgram, ["rev-parse", "HEAD"], workingDirectory, cancellationToken).ConfigureAwait(false);
            branch = branchResult.Spawned && branchResult.ExitCode == 0 ? branchResult.Output.Trim() : null;
            localHead = headResult.Spawned && headResult.ExitCode == 0 ? headResult.Output.Trim() : null;
            if (branch is null || localHead is null) observationProblem = "could not determine the final local branch and HEAD";
            else if (!string.Equals(branch, "HEAD", StringComparison.Ordinal))
            {
                var remote = await RunNetworkAsync(gitProgram, ["ls-remote", "--heads", "origin", branch], workingDirectory, cancellationToken).ConfigureAwait(false);
                remoteHead = remote.Spawned && remote.ExitCode == 0 ? remote.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() : null;
                if (expectPr)
                {
                    finalPullRequest = await ReadOpenPullRequestAsync(workingDirectory, branch, ghProgram, cancellationToken)
                        .ConfigureAwait(false);
                    pullRequestNumber = finalPullRequest.Number;
                    pullRequestHead = finalPullRequest.Head;
                }
            }
        }
        var evidence = new DeliveryEvidence(
            DateTimeOffset.UtcNow.ToString("O"), localHead, branch, remoteHead, pullRequestNumber,
            deliveryOutcome.Status, deliveryOutcome.FailingMembers,
            deliveryOutcome.Tail ?? deliveryOutcome.NotRunReason, observationProblem, pullRequestHead);
        return deliveryOutcome.Status == DeliveryCheckStatus.Passed && expectPr
            ? ReconcileExpectedPullRequest(evidence, deliveryOutcome, finalPullRequest)
            : evidence;
    }

    /// <summary>
    /// Protected invariant (#2309): an earlier open PR cannot certify its own later forge state.
    /// A passing expected-PR check applies only while that same readable PR identity/head remains
    /// open at the engine's final observation; absence or a changed head never inherits the pass.
    /// </summary>
    private static DeliveryEvidence ReconcileExpectedPullRequest(
        DeliveryEvidence evidence, DeliveryCheckOutcome checkedOutcome, OpenPullRequestReading? finalPullRequest)
    {
        if (!checkedOutcome.CheckedPullRequestOpen || finalPullRequest?.AnyOpen is null)
        {
            var reason = finalPullRequest?.NotRunReason
                ?? "passing delivery check did not retain its expected PR reading";
            return evidence with
            {
                Verification = DeliveryCheckStatus.NotRun,
                VerificationReason = $"final expected PR observation could not be confirmed: {reason}",
                ObservationProblem = reason,
            };
        }

        if (finalPullRequest.AnyOpen is false)
        {
            return FailExpectedPullRequest(evidence,
                "the PR open during delivery verification is no longer open at final observation");
        }

        if (checkedOutcome.CheckedPullRequestNumber is { } checkedNumber)
        {
            if (finalPullRequest.Number is null)
                return UnknownExpectedPullRequest(evidence, "the final open PR number could not be read");
            if (finalPullRequest.Number != checkedNumber)
                return FailExpectedPullRequest(evidence, "the final open PR number differs from the checked PR");
        }

        if (checkedOutcome.CheckedPullRequestHead is { } checkedHead)
        {
            if (!IsObjectId(finalPullRequest.Head))
                return UnknownExpectedPullRequest(evidence, "the final open PR head could not be read");
            if (!string.Equals(finalPullRequest.Head, checkedHead, StringComparison.OrdinalIgnoreCase))
                return FailExpectedPullRequest(evidence, "the final open PR head differs from the checked PR head");
        }

        return evidence;
    }

    private static DeliveryEvidence UnknownExpectedPullRequest(DeliveryEvidence evidence, string reason) =>
        evidence with
        {
            Verification = DeliveryCheckStatus.NotRun,
            VerificationReason = reason,
            ObservationProblem = reason,
        };

    private static DeliveryEvidence FailExpectedPullRequest(DeliveryEvidence evidence, string reason) =>
        evidence with
        {
            Verification = DeliveryCheckStatus.Failed,
            FailingMembers = ["pr-not-open"],
            VerificationReason = $"pr-not-open: {reason}",
            ObservationProblem = reason,
        };

    /// <summary>
    /// A convenience projection of an already-journalled observation for a human holding only the
    /// execution directory. Status, lifecycle, and recovery MUST NOT take authority from this cache.
    /// Replacing a worker-preplaced cache is safe after the worker has exited; the ledger event itself
    /// stays immutable and the worker's named handoff is never rewritten.
    /// </summary>
    public static async Task WriteEvidenceSnapshotAsync(string outputDirectory, DeliveryEvidence evidence,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var path = Path.Combine(outputDirectory, DeliveryEvidenceFileName);
        var json = JsonSerializer.Serialize(evidence, EvidenceJsonOptions);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    /// <summary>Legacy fixture writer; the fixed-path file it creates is not a routing authority.</summary>
    public static async Task WriteEvidenceAsync(string outputDirectory, string? workingDirectory, bool expectPr,
        DeliveryCheckOutcome deliveryOutcome, CancellationToken cancellationToken, string gitProgram = "git", string ghProgram = "gh")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (File.Exists(Path.Combine(outputDirectory, DeliveryEvidenceFileName))) return;
        var evidence = await ObserveAsync(workingDirectory, expectPr, deliveryOutcome, cancellationToken, gitProgram, ghProgram)
            .ConfigureAwait(false);
        await WriteEvidenceSnapshotAsync(outputDirectory, evidence, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads only the engine-owned ledger fact; absent or incomplete events fail closed.</summary>
    public static DeliveryEvidenceReading ReadRecordedEvidence(FlowEvent.DeliveryObservationRecorded? recorded)
    {
        if (recorded is null) return new(null, "delivery observation event is absent");
        if (!Enum.TryParse<DeliveryCheckStatus>(recorded.Verification, ignoreCase: false, out var verification)
            || !Enum.IsDefined(verification)
            || !string.Equals(recorded.Verification, verification.ToString(), StringComparison.Ordinal))
        {
            return new(null, "delivery observation event is incomplete");
        }

        var evidence = new DeliveryEvidence(recorded.ObservedAt, recorded.LocalHead, recorded.Branch,
            recorded.RemoteHead, recorded.PullRequestNumber, verification, recorded.FailingMembers,
            recorded.VerificationReason, recorded.ObservationProblem, recorded.PullRequestHead);
        return IsCompleteEvidence(evidence)
            ? new(evidence)
            : new(null, "delivery observation event is incomplete");
    }

    /// <summary>Reads the immutable observation for an execution, if its complete JSON is available.</summary>
    public static async Task<DeliveryEvidenceReading> ReadEvidenceAsync(string outputDirectory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var path = Path.Combine(outputDirectory, DeliveryEvidenceFileName);
        if (!File.Exists(path)) return new(null);
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var evidence = await JsonSerializer.DeserializeAsync<DeliveryEvidence>(stream, EvidenceJsonOptions, cancellationToken).ConfigureAwait(false);
            return IsCompleteEvidence(evidence)
                ? new(evidence)
                : new(null, "delivery evidence stamp is incomplete");
        }
        catch (JsonException) { return new(null, "delivery evidence stamp is unreadable"); }
        catch (IOException) { return new(null, "delivery evidence stamp could not be read"); }
    }

    private static bool IsCompleteEvidence(DeliveryEvidence? evidence)
    {
        if (evidence is null
            || !DateTimeOffset.TryParse(evidence.ObservedAt, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out _)
            || !Enum.IsDefined(evidence.Verification)
            || (evidence.PullRequestHead is not null && !IsObjectId(evidence.PullRequestHead)))
        {
            return false;
        }

        // A pass is an affirmative claim about exact Git heads and, when it names a PR, that
        // PR's exact head. Dropping the optional PR-head member from a valid journal line cannot
        // silently turn an expected-PR pass into branch-only success on replay. Failed and NotRun
        // permit unavailable facts so a truthful fail-closed observation stays readable.
        if (evidence.Verification == DeliveryCheckStatus.NotRun
            && string.IsNullOrWhiteSpace(evidence.VerificationReason)) return false;

        return evidence.Verification != DeliveryCheckStatus.Passed
            || (string.IsNullOrWhiteSpace(evidence.ObservationProblem)
                && IsObjectId(evidence.LocalHead)
                && !string.IsNullOrWhiteSpace(evidence.Branch)
                && !string.Equals(evidence.Branch, "HEAD", StringComparison.Ordinal)
                && IsObjectId(evidence.RemoteHead)
                && (evidence.PullRequestNumber is null
                    ? evidence.PullRequestHead is null
                    : evidence.PullRequestNumber > 0 && IsObjectId(evidence.PullRequestHead)));
    }

    private static bool IsObjectId(string? value) =>
        value is { Length: 40 or 64 } && value.All(char.IsAsciiHexDigit);

    private static async Task<DeliveryCheckOutcome> CheckCoreAsync(
        string? workingDirectory, bool expectPr, CancellationToken cancellationToken, string gitProgram, string ghProgram,
        bool shippingCeilingExceeded)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return new DeliveryCheckOutcome(
                DeliveryCheckStatus.NotRun, NotRunReason: "delivery check not run: no working directory for this execution");
        }

        var branchResult = await RunAsync(gitProgram, ["rev-parse", "--abbrev-ref", "HEAD"], workingDirectory, cancellationToken)
            .ConfigureAwait(false);
        if (!branchResult.Spawned)
        {
            return new DeliveryCheckOutcome(DeliveryCheckStatus.NotRun, NotRunReason: $"delivery check not run: could not spawn '{gitProgram}'");
        }

        // `git rev-parse --abbrev-ref HEAD` failing outright (non-zero exit -- not a git repository,
        // an unreadable HEAD, or some other spawn-level git problem) is a git/engine-environment
        // question this check cannot answer, mirroring VerifyCommandResolver's own "a probe failure is
        // never read as evidence" rule -- measured directly against #1788's own end-to-end fixtures,
        // which dispatch `implement` against a plain (non-git) scratch directory and must still settle
        // Succeeded on their own, orthogonal not-run reason (a foreign/non-pixi workspace).
        if (branchResult.ExitCode != 0)
        {
            return new DeliveryCheckOutcome(
                DeliveryCheckStatus.NotRun, NotRunReason: "delivery check not run: could not determine the current branch (not a git repository?)");
        }

        var branch = branchResult.Output.Trim();

        // Unlike the arm above, a SUCCESSFUL rev-parse that answers "HEAD" is a detached HEAD --
        // spec/baton.md §3 states why that settles Failed rather than NotRun.
        if (branch.Length == 0 || string.Equals(branch, "HEAD", StringComparison.Ordinal))
        {
            return new DeliveryCheckOutcome(
                DeliveryCheckStatus.Failed,
                ["branch-not-pushed"],
                "branch-not-pushed: the workspace has no checked-out branch (detached HEAD) — commit onto a named branch and push it before this lane can settle Succeeded.");
        }

        var failingMembers = new List<string>();
        var tailLines = new List<string>();
        var notRunReasons = new List<string>();
        DeliveryCheckOutcome? checkedPush = null;
        OpenPullRequestReading? checkedPr = null;

        var lsRemoteResult = await RunNetworkAsync(
            gitProgram, ["ls-remote", "--exit-code", "--heads", "origin", branch], workingDirectory, cancellationToken)
            .ConfigureAwait(false);
        if (!lsRemoteResult.Spawned)
        {
            notRunReasons.Add($"could not spawn '{gitProgram}' to query origin");
        }
        else if (lsRemoteResult.ExitCode == LsRemoteRefAbsentExitCode)
        {
            failingMembers.Add("branch-not-pushed");
            tailLines.Add(shippingCeilingExceeded
                ? $"branch-not-pushed: {ShellCommandCeilings.ShippingBreachReason()}, so origin has no branch ref for '{branch}'."
                : $"branch-not-pushed: origin has no branch ref for '{branch}' — it has never been pushed.");
        }
        else if (lsRemoteResult.ExitCode != 0)
        {
            notRunReasons.Add($"'git ls-remote origin {branch}' could not reach the remote (network, auth, or credential prompt unavailable)");
        }
        else
        {
            var pushed = await CheckPushedAsync(
                gitProgram, workingDirectory, branch, shippingCeilingExceeded, cancellationToken).ConfigureAwait(false);
            if (pushed.Status == DeliveryCheckStatus.Failed)
            {
                failingMembers.AddRange(pushed.FailingMembers!);
                tailLines.Add(pushed.Tail!);
            }
            else if (pushed.Status == DeliveryCheckStatus.NotRun)
            {
                notRunReasons.Add(pushed.NotRunReason!);
            }
            else
            {
                checkedPush = pushed;
            }
        }

        if (expectPr)
        {
            var pr = await ReadOpenPullRequestAsync(workingDirectory, branch, ghProgram, cancellationToken).ConfigureAwait(false);
            if (pr.AnyOpen is null)
            {
                // Neither fabricated pass nor fabricated failure -- see OpenPullRequestReading's own doc.
                notRunReasons.Add(pr.NotRunReason!);
            }
            else if (pr.AnyOpen is false)
            {
                failingMembers.Add("pr-not-open");
                tailLines.Add($"pr-not-open: no open PR found for branch '{branch}' — open one before this lane can settle Succeeded.");
            }
            else if (pr.Number.GetValueOrDefault() <= 0 || !IsObjectId(pr.Head))
            {
                // Protected invariant (#2309): an unnameable open PR is positive evidence of
                // existence, but cannot certify the exact PR head after a later forge mutation.
                // Keep the general three-state PR reader; only delivery's positive assertion
                // requires the identity and object ID needed for the final comparison.
                notRunReasons.Add("exact PR number/head was unavailable in the first open PR reading");
            }
            else checkedPr = pr;
        }

        if (failingMembers.Count > 0)
        {
            return new DeliveryCheckOutcome(DeliveryCheckStatus.Failed, failingMembers, string.Join("\n", tailLines));
        }

        if (notRunReasons.Count > 0)
        {
            return new DeliveryCheckOutcome(DeliveryCheckStatus.NotRun, NotRunReason: string.Join("; ", notRunReasons));
        }

        return checkedPush is null
            ? new DeliveryCheckOutcome(DeliveryCheckStatus.NotRun,
                NotRunReason: "delivery push check supplied no exact checked heads")
            : checkedPush with
            {
                CheckedPullRequestOpen = checkedPr?.AnyOpen == true,
                CheckedPullRequestNumber = checkedPr?.Number,
                CheckedPullRequestHead = checkedPr?.Head,
            };
    }

    /// <summary>
    /// Only reached once <c>ls-remote</c> has already confirmed a matching branch ref exists on origin --
    /// so a fetch failure here is a transient/engine-environment problem (NotRun), never re-litigated as
    /// "never pushed" (that positive evidence was already ruled out above).
    /// </summary>
    private static async Task<DeliveryCheckOutcome> CheckPushedAsync(
        string gitProgram, string workingDirectory, string branch, bool shippingCeilingExceeded,
        CancellationToken cancellationToken)
    {
        // Name the exact local object BEFORE the fetch/ancestry probe. A later HEAD change cannot
        // inherit this verdict merely because the symbolic name moved during separate spawns.
        var localResult = await RunAsync(gitProgram, ["rev-parse", "HEAD"], workingDirectory, cancellationToken)
            .ConfigureAwait(false);
        var localHead = localResult.Output.Trim();
        if (!localResult.Spawned || localResult.ExitCode != 0 || !IsObjectId(localHead))
        {
            return new DeliveryCheckOutcome(DeliveryCheckStatus.NotRun,
                NotRunReason: "could not read the exact local HEAD for delivery verification");
        }
        // spec/baton.md §3 states why the explicit refspec form is used here rather than a bare
        // `git fetch origin <branch>`.
        var fetchResult = await RunNetworkAsync(
            gitProgram, ["fetch", "origin", $"+refs/heads/{branch}:refs/remotes/origin/{branch}"], workingDirectory, cancellationToken)
            .ConfigureAwait(false);
        if (!fetchResult.Spawned || fetchResult.ExitCode != 0)
        {
            return new DeliveryCheckOutcome(
                DeliveryCheckStatus.NotRun,
                NotRunReason: $"'git fetch origin {branch}' did not succeed (network, auth, or credential prompt unavailable)");
        }

        var remoteResult = await RunAsync(gitProgram, ["rev-parse", $"origin/{branch}"], workingDirectory,
            cancellationToken).ConfigureAwait(false);
        var remoteHead = remoteResult.Output.Trim();
        if (!remoteResult.Spawned || remoteResult.ExitCode != 0 || !IsObjectId(remoteHead))
        {
            return new DeliveryCheckOutcome(DeliveryCheckStatus.NotRun,
                NotRunReason: "could not read the exact fetched remote HEAD for delivery verification");
        }

        var ancestorResult = await RunAsync(
            gitProgram, ["merge-base", "--is-ancestor", localHead, remoteHead], workingDirectory, cancellationToken)
            .ConfigureAwait(false);
        if (!ancestorResult.Spawned)
        {
            return new DeliveryCheckOutcome(
                DeliveryCheckStatus.NotRun, NotRunReason: $"could not spawn '{gitProgram}' to check ancestry");
        }

        if (ancestorResult.ExitCode == 0)
        {
            return new DeliveryCheckOutcome(DeliveryCheckStatus.Passed,
                CheckedLocalHead: localHead, CheckedRemoteHead: remoteHead);
        }

        if (ancestorResult.ExitCode == MergeBaseNotAncestorExitCode)
        {
            return new DeliveryCheckOutcome(
                DeliveryCheckStatus.Failed,
                ["branch-not-pushed"],
                shippingCeilingExceeded
                    ? $"branch-not-pushed: {ShellCommandCeilings.ShippingBreachReason()}, so HEAD is not reachable from origin/{branch}."
                    : $"branch-not-pushed: HEAD is not reachable from origin/{branch} — push the branch before this lane can settle Succeeded.");
        }

        return new DeliveryCheckOutcome(
            DeliveryCheckStatus.NotRun,
            NotRunReason: $"'git merge-base --is-ancestor' could not determine ancestry against origin/{branch}");
    }

    /// <summary>
    /// #1978/#2309: <c>gh pr list --head &lt;branch&gt; --json number,headRefOid</c>, resolving the branch from
    /// <paramref name="workingDirectory"/> itself. Extracted from this class's own <c>expectPr</c> block
    /// so the timeout summary (<c>Outcomes.OutcomeClassifier</c>'s #1373 mutated-workspace arm, wired at
    /// <c>MutationInterface</c>) names a PR through the SAME question and the same spelling this check
    /// already asks — one PR-detection path, not two. <c>Cli.WorkspaceDeliveryProbe</c> asks the same
    /// question a third time on purpose and says so in its own doc: it lives in another assembly behind
    /// its own bounded spawner.
    /// <para>
    /// <b>Unbounded in time, like every other spawn in this class.</b> A caller that is not already
    /// inside a bounded step must pass a token it has bounded itself — <c>MutationInterface</c>'s
    /// <c>OpenPullRequestLookupTimeout</c> is that bound and states why one is needed at all.
    /// </para>
    /// </summary>
    public static async Task<OpenPullRequestReading> ReadOpenPullRequestAsync(
        string? workingDirectory,
        CancellationToken cancellationToken,
        string gitProgram = "git",
        string ghProgram = "gh")
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return OpenPullRequestReading.NotRun("no working directory for this execution");
        }

        var branchResult = await RunAsync(gitProgram, ["rev-parse", "--abbrev-ref", "HEAD"], workingDirectory, cancellationToken)
            .ConfigureAwait(false);
        if (!branchResult.Spawned || branchResult.ExitCode != 0)
        {
            return OpenPullRequestReading.NotRun("could not determine the current branch (not a git repository?)");
        }

        var branch = branchResult.Output.Trim();
        if (branch.Length == 0 || string.Equals(branch, "HEAD", StringComparison.Ordinal))
        {
            // A detached HEAD names no branch to ask `gh` about. Unmeasurable, never "no PR is open":
            // this reading is only ever used to ADD a fact, so a fabricated absence would be silent.
            return OpenPullRequestReading.NotRun("the workspace has no checked-out branch (detached HEAD)");
        }

        return await ReadOpenPullRequestAsync(workingDirectory, branch, ghProgram, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="ReadOpenPullRequestAsync(string?, CancellationToken, string, string)"/>
    /// <remarks>The overload for a caller that has already resolved the branch — <see cref="CheckCoreAsync"/>.</remarks>
    private static async Task<OpenPullRequestReading> ReadOpenPullRequestAsync(
        string workingDirectory, string branch, string ghProgram, CancellationToken cancellationToken)
    {
        var prResult = await RunAsync(ghProgram, ["pr", "list", "--head", branch, "--json", "number,headRefOid"], workingDirectory, cancellationToken)
            .ConfigureAwait(false);
        if (!prResult.Spawned)
        {
            return OpenPullRequestReading.NotRun($"could not spawn '{ghProgram}'");
        }

        if (prResult.ExitCode != 0)
        {
            return OpenPullRequestReading.NotRun("'gh pr list' did not succeed (gh/network unavailable)");
        }

        return ParseOpenPullRequests(prResult.Output);
    }

    /// <summary>
    /// The three readings <c>gh pr list --json number,headRefOid</c>'s stdout admits, kept as three rather than
    /// collapsed: a positively-parsed empty array (no PR), a non-empty one (at least one PR, named when
    /// its <c>number</c> is readable), and output that does not parse as an array at all.
    /// </summary>
    private static OpenPullRequestReading ParseOpenPullRequests(string output)
    {
        const string unparsedReason = "'gh pr list' succeeded but its output did not parse as the expected JSON array";

        try
        {
            using var document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return OpenPullRequestReading.NotRun(unparsedReason);
            }

            if (document.RootElement.GetArrayLength() == 0)
            {
                return OpenPullRequestReading.NoneOpen;
            }

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.Object
                    && element.TryGetProperty("number", out var number)
                    && number.ValueKind == JsonValueKind.Number
                    && number.TryGetInt32(out var value))
                {
                    // The first of several open PRs for one branch, which `gh` orders newest-first --
                    // the same choice Cli.WorkspaceDeliveryProbe makes for the same output.
                    string? head = null;
                    if (element.TryGetProperty("headRefOid", out var headRefOid)
                        && headRefOid.ValueKind == JsonValueKind.String)
                    {
                        head = headRefOid.GetString();
                    }
                    return new OpenPullRequestReading(AnyOpen: true, Number: value, Head: head);
                }
            }

            // A non-empty array whose entries carry no readable number stays "a PR is open,
            // unnameable" rather than fabricating either polarity. The timeout summary names
            // nothing; the stricter delivery check records NotRun because it cannot compare an
            // exact PR identity/head at the final observation.
            return new OpenPullRequestReading(AnyOpen: true, Number: null);
        }
        catch (JsonException)
        {
            return OpenPullRequestReading.NotRun(unparsedReason);
        }
    }

    private readonly record struct SpawnResult(bool Spawned, int ExitCode, string Output);

    /// <summary>
    /// Plain <see cref="VerifyRunner.CaptureAsync"/> — no environment allowlist, no PATH scrubbing, no
    /// credential hardening (see this class's own remarks for why the first two buy nothing here; the
    /// third is <see cref="RunNetworkAsync"/>'s job for the two spawns that actually touch the network).
    /// A failed spawn (missing binary, cancellation) reports <see cref="SpawnResult.Spawned"/> false
    /// rather than throwing, mirroring <see cref="VerifyCommandResolver"/>'s own "an optional read must
    /// never abort the caller" rule.
    /// </summary>
    private static async Task<SpawnResult> RunAsync(
        string program, IReadOnlyList<string> args, string workingDirectory, CancellationToken cancellationToken)
    {
        try
        {
            var (exitCode, output) = await VerifyRunner.CaptureAsync(program, args, workingDirectory, cancellationToken, stdoutOnly: true)
                .ConfigureAwait(false);
            return new SpawnResult(true, exitCode, output);
        }
        catch (BatonException)
        {
            return new SpawnResult(false, -1, string.Empty);
        }
        catch (OperationCanceledException)
        {
            return new SpawnResult(false, -1, string.Empty);
        }
    }

    /// <summary>
    /// <see cref="RunAsync"/>, plus the non-interactive git hardening (<see cref="NonInteractiveGitEnv"/>/
    /// <see cref="NonInteractiveGitArgs"/>) — only for the spawns that actually reach a remote
    /// (<c>ls-remote</c>, <c>fetch</c>); <c>gh</c> reads its own non-interactive env implicitly when
    /// stdin/stdout are not a terminal, which is always true of a spawned child here.
    /// </summary>
    private static Task<SpawnResult> RunNetworkAsync(
        string program, IReadOnlyList<string> args, string workingDirectory, CancellationToken cancellationToken) =>
        RunWithEnvAsync(program, [.. NonInteractiveGitArgs, .. args], workingDirectory, NonInteractiveGitEnv, cancellationToken);

    private static async Task<SpawnResult> RunWithEnvAsync(
        string program, IReadOnlyList<string> args, string workingDirectory,
        IReadOnlyDictionary<string, string> environmentOverrides, CancellationToken cancellationToken)
    {
        try
        {
            var (exitCode, output) = await VerifyRunner.CaptureAsync(
                program, args, workingDirectory, cancellationToken, stdoutOnly: true, environmentOverrides: environmentOverrides)
                .ConfigureAwait(false);
            return new SpawnResult(true, exitCode, output);
        }
        catch (BatonException)
        {
            return new SpawnResult(false, -1, string.Empty);
        }
        catch (OperationCanceledException)
        {
            return new SpawnResult(false, -1, string.Empty);
        }
    }
}
