using System.Text.Json;
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
    string? NotRunReason = null)
{
    public static readonly DeliveryCheckOutcome Pass = new(DeliveryCheckStatus.Passed);
    public static readonly DeliveryCheckOutcome CancelledOutcome = new(DeliveryCheckStatus.Cancelled);
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
        }

        if (expectPr)
        {
            var prResult = await RunAsync(ghProgram, ["pr", "list", "--head", branch, "--json", "number"], workingDirectory, cancellationToken)
                .ConfigureAwait(false);
            if (!prResult.Spawned)
            {
                notRunReasons.Add($"could not spawn '{ghProgram}'");
            }
            else if (prResult.ExitCode != 0)
            {
                notRunReasons.Add("'gh pr list' did not succeed (gh/network unavailable)");
            }
            else
            {
                var isEmpty = TryIsEmptyJsonArray(prResult.Output);
                if (isEmpty is true)
                {
                    failingMembers.Add("pr-not-open");
                    tailLines.Add($"pr-not-open: no open PR found for branch '{branch}' — open one before this lane can settle Succeeded.");
                }
                else if (isEmpty is null)
                {
                    // Neither fabricated pass nor fabricated failure -- see TryIsEmptyJsonArray's own doc.
                    notRunReasons.Add("'gh pr list' succeeded but its output did not parse as the expected JSON array");
                }
            }
        }

        if (failingMembers.Count > 0)
        {
            return new DeliveryCheckOutcome(DeliveryCheckStatus.Failed, failingMembers, string.Join("\n", tailLines));
        }

        if (notRunReasons.Count > 0)
        {
            return new DeliveryCheckOutcome(DeliveryCheckStatus.NotRun, NotRunReason: string.Join("; ", notRunReasons));
        }

        return DeliveryCheckOutcome.Pass;
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

        var ancestorResult = await RunAsync(
            gitProgram, ["merge-base", "--is-ancestor", "HEAD", $"origin/{branch}"], workingDirectory, cancellationToken)
            .ConfigureAwait(false);
        if (!ancestorResult.Spawned)
        {
            return new DeliveryCheckOutcome(
                DeliveryCheckStatus.NotRun, NotRunReason: $"could not spawn '{gitProgram}' to check ancestry");
        }

        if (ancestorResult.ExitCode == 0)
        {
            return DeliveryCheckOutcome.Pass;
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
    /// <see langword="true"/>/<see langword="false"/> for a positively-parsed JSON array (empty or not);
    /// <see langword="null"/> when <paramref name="output"/> does not parse as one at all -- a
    /// third, "unmeasurable" outcome distinct from either boolean, so a caller never has to fabricate a
    /// pass or a failure from output that plainly did not answer the question.
    /// </summary>
    private static bool? TryIsEmptyJsonArray(string output)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            return document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.GetArrayLength() == 0
                : null;
        }
        catch (JsonException)
        {
            return null;
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
